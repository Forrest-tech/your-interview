#!/usr/bin/env python3
"""票119 音频核心包采收 (SPEC §3.2 stage 3)。

Inputs : audio-index.json (stage 2 产物) + vocab.json (rank/tag)。
Rule   : 主体 = rank<=6000 词的经典档(En-us-/En-uk-)录音,US 优先、无 US 取 UK
         (§3.2 + RESEARCH §8.3 的 4,147 文件投影口径);增量 = 考试标签词的经典档
         按 rank 升序填满 79 MB 预算(80 MB 硬上限留 1 MB 余量)。
         非经典档(LL 等)不入包 —— WAV 转码 MP3 体积失控,运行时走懒拉取层。
         许可红线:含 NC/ND 的许可与 Unknown 不入包(不可再分发/不可考者
         只留懒拉取层,运行时按 cache-index 记来源与许可)。
Outputs: <out-dir>/cNNNNNN.mp3 ... + audio-core-manifest.json
         manifest.files[] = {key, word, accent, file(源文件名), storedAs,
         license, artist, bytes, sha256} —— 与后端 AudioResolver 的
         EnsureCoreLoaded 读取口径一致(storedAs = 本地文件名)。
体积门:SPEC §3.2 上限 80 MB;超限时以 --max-rank 3000 收紧重跑。
"""
import hashlib, json, os, sys, urllib.request
from concurrent.futures import ThreadPoolExecutor

BASE = os.environ.get("DICT_BUILD_DIR", os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.environ.get("AUDIO_CORE_DIR", os.path.join(BASE, "audio-core"))
MAX_RANK = int(os.environ.get("CORE_MAX_RANK", "6000"))
UA = {"User-Agent": "your-interview-dict-build/1.0 (research; contact: dev@localhost)"}

vocab = json.load(open(os.path.join(BASE, "vocab.json"), encoding="utf-8"))
index = json.load(open(os.path.join(BASE, "audio-index.json"), encoding="utf-8"))
core_words = {w for w, v in vocab.items()
              if (v.get("rank") and v["rank"] <= MAX_RANK) or v.get("tag")}
print("core words:", len(core_words), "maxRank:", MAX_RANK, flush=True)


def license_ok(lic):
    s = (lic or "").upper()
    if not s or s == "UNKNOWN":
        return False
    return "NC" not in s and "ND" not in s


def is_classic(rec):
    f = (rec.get("file") or "").lower()
    return f.startswith("en-us-") or f.startswith("en-uk-")


# 核心包构成(§3.2 + RESEARCH §8.3 + 80 MB 硬上限的实测调和,见 VERIFY D-5):
#   主体 = rank<=6000 的经典档(En-us-/En-uk-)录音 —— §8.3 投影 4,147 文件 ≈ 63.5 MB
#   的口径正是此集;US 优先、无 US 取 UK。
#   增量 = 带考试标签词的经典档,按 rank 升序(无 rank 者殿后)填充剩余预算。
# LL 等非经典档不入包:WAV 转码 MP3 体积失控且质量参差,运行时走懒拉取层。
# 预算以 BUDGET_BYTES 为硬顶,按波次(200 条)累计,超顶即停并在 manifest 记录截断点。
BUDGET_BYTES = int(os.environ.get("CORE_BUDGET_BYTES", str(79 * 1024 * 1024)))
main_words = {w for w, v in vocab.items() if v.get("rank") and v["rank"] <= 6000}
tagged_extra = sorted(
    (w for w in core_words if w not in main_words),
    key=lambda w: (vocab[w].get("rank") is None, vocab[w].get("rank") or 0, w))


def pick_classic(w):
    recs = [r for r in (index.get(w) or [])
            if is_classic(r) and license_ok(r.get("license")) and r.get("mp3Url")]
    for acc in ("us", "uk"):
        for r in recs:
            if r.get("accent") == acc:
                return (w, acc, r)
    return None


candidates = []  # (word, accent, rec)
# 主体按 rank 升序:若预算在主体内截断,丢的是最低频词而非字母序末尾词。
for w in sorted(main_words, key=lambda w: (vocab[w]["rank"], w)):
    picked = pick_classic(w)
    if picked:
        candidates.append(picked)
main_count = len(candidates)
for w in tagged_extra:
    picked = pick_classic(w)
    if picked:
        candidates.append(picked)
print(f"candidates: main(rank<=6000 classic)={main_count}, "
      f"tagged-extra(classic)={len(candidates) - main_count}", flush=True)
os.makedirs(OUT_DIR, exist_ok=True)


def fetch(item):
    seq, (w, acc, r) = item
    key = f"c{seq:06d}"
    url = r["mp3Url"]
    last_err = None
    for _ in range(3):
        try:
            req = urllib.request.Request(url, headers=UA)
            with urllib.request.urlopen(req, timeout=30) as resp:
                data = resp.read()
            if not data:
                raise ValueError("empty body")
            path = os.path.join(OUT_DIR, f"{key}.mp3")
            with open(path, "wb") as f:
                f.write(data)
            return {
                "key": key, "word": w, "accent": acc, "file": r["file"],
                "storedAs": f"{key}.mp3", "license": r.get("license", ""),
                "artist": r.get("artist", ""), "bytes": len(data),
                "sha256": hashlib.sha256(data).hexdigest(),
            }
        except Exception as e:  # noqa: BLE001 - 单文件失败跳过并计数,不中断整批
            last_err = e
    print("FAILED", w, acc, r["file"], last_err, flush=True)
    return None


results = []
total_so_far = 0
truncated_at = None
numbered = list(enumerate(candidates, start=1))
with ThreadPoolExecutor(max_workers=8) as ex:
    for wave_start in range(0, len(numbered), 200):
        wave = numbered[wave_start:wave_start + 200]
        for entry in ex.map(fetch, wave):
            if entry:
                results.append(entry)
                total_so_far += entry["bytes"]
        if total_so_far > BUDGET_BYTES:
            truncated_at = wave_start + len(wave)
            break
results.sort(key=lambda e: e["key"])
# 硬顶修剪:波次粒度会冲过预算,落盘后按候选序从尾部(最低优先)逐个剔除至 ≤ CAP。
CAP_BYTES = int(os.environ.get("CORE_CAP_BYTES", "80000000"))
total = sum(e["bytes"] for e in results)
trimmed = 0
while total > CAP_BYTES and results:
    victim = results.pop()
    total -= victim["bytes"]
    trimmed += 1
    try:
        os.remove(os.path.join(OUT_DIR, victim["storedAs"]))
    except OSError:
        pass
print(f"cap trim: removed {trimmed} tail files -> {len(results)} files, {total} bytes "
      f"(cap {CAP_BYTES})", flush=True)
manifest = {
    "builder": "harvest_audio_core.py",
    "coreRule": ("rank<=6000 classic (En-us-/En-uk-) first, US preferred; exam-tagged classic "
                 "fills remaining budget in rank order; LL/non-classic excluded (lazy tier serves them); "
                 "NC/ND/Unknown licenses excluded"),
    "budgetBytes": BUDGET_BYTES,
    "capBytes": CAP_BYTES,
    "truncatedAtCandidate": truncated_at,
    "capTrimmedFiles": trimmed,
    "files": results,
    "totalFiles": len(results),
    "totalBytes": total,
    "failed": len(candidates) - len(results) if truncated_at is None else None,
}
with open(os.path.join(OUT_DIR, "audio-core-manifest.json"), "w", encoding="utf-8") as f:
    json.dump(manifest, f, ensure_ascii=False, indent=1)
print(json.dumps({k: manifest[k] for k in ("totalFiles", "totalBytes", "failed")}), flush=True)
print(f"total MB: {total / 1024 / 1024:.1f} (cap 80 MB)", flush=True)
