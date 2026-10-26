#!/usr/bin/env python3
"""票119 audio-index stage 2 (SPEC §3.2 / RESEARCH §8.5):
kaikki-sounds.jsonl + Commons API -> audio-index.json + manifest.

- Kaikki records keep their mp3_url; missing ones get the Commons transcoded-MP3
  URL derived by the md5 path rule (verified pattern, RESEARCH §8.4).
- Core words (rank<=6000 or exam tag) get a Commons supplement pass for the
  classic File:En-us-/En-uk-{word}.ogg titles Kaikki did not list.
- Every file gets license + artist from Commons extmetadata (batched 50/req).
"""
import hashlib, html, json, re, urllib.parse, urllib.request
from concurrent.futures import ThreadPoolExecutor

import os
BASE = os.environ.get("DICT_BUILD_DIR", os.path.dirname(os.path.abspath(__file__)))
API = "https://commons.wikimedia.org/w/api.php"
UA = {"User-Agent": "your-interview-dict-build/1.0 (research; contact: dev@localhost)"}

vocab = json.load(open(f"{BASE}/vocab.json", encoding="utf-8"))
core_words = {w for w, v in vocab.items() if (v["rank"] and v["rank"] <= 6000) or v["tag"]}
print("core words:", len(core_words), flush=True)

index = {}
with open(f"{BASE}/kaikki-sounds.jsonl", encoding="utf-8") as f:
    for line in f:
        d = json.loads(line)
        recs = []
        seen_files = set()
        for s in d["sounds"]:
            if s["file"] in seen_files:
                continue
            seen_files.add(s["file"])
            rec = {"file": s["file"], "accent": s["accent"]}
            if s.get("ipa"): rec["ipa"] = s["ipa"]
            if s.get("mp3Url"): rec["mp3Url"] = s["mp3Url"]
            recs.append(rec)
        if recs:
            index[d["word"]] = recs
print("kaikki words:", len(index), flush=True)

def transcode_url(fname):
    f = fname.replace(" ", "_")
    h = hashlib.md5(f.encode("utf-8")).hexdigest()
    return f"https://upload.wikimedia.org/wikipedia/commons/transcoded/{h[0]}/{h[:2]}/{f}/{f}.mp3"

def strip_html(s):
    return html.unescape(re.sub(r"<[^>]+>", "", s or "")).strip()

def api_imageinfo(titles):
    """titles: list of 'File:...' -> {requestedTitle: {url, license, artist} or None}

    Commons 会对标题做规范化(首字母大写)与重定向:响应 pages 以规范标题为键,
    必须经 query.normalized / query.redirects 链映射回请求标题,否则对不上号
    (实现期实测 KeyError 教训)。
    """
    q = urllib.parse.urlencode({
        "action": "query", "format": "json", "prop": "imageinfo",
        "iiprop": "url|extmetadata", "titles": "|".join(titles),
    })
    req = urllib.request.Request(f"{API}?{q}", headers=UA)
    with urllib.request.urlopen(req, timeout=30) as resp:
        data = json.load(resp)
    query = data.get("query") or {}
    norm = {n["from"]: n["to"] for n in query.get("normalized") or []}
    redir = {r["from"]: r["to"] for r in query.get("redirects") or []}

    def resolve(title):
        seen = set()
        while title in norm and title not in seen:
            seen.add(title)
            title = norm[title]
        seen = set()
        while title in redir and title not in seen:
            seen.add(title)
            title = redir[title]
        return title

    by_canonical = {}
    for page in (query.get("pages") or {}).values():
        ii = (page.get("imageinfo") or [None])[0]
        if ii is None:
            by_canonical[page.get("title", "")] = None
            continue
        em = ii.get("extmetadata") or {}
        by_canonical[page.get("title", "")] = {
            "url": ii.get("url", ""),
            "license": (em.get("LicenseShortName") or {}).get("value", ""),
            "artist": strip_html((em.get("Artist") or {}).get("value", "")),
        }
    return {t: by_canonical.get(resolve(t)) for t in titles}

def batches(items, n=50):
    for i in range(0, len(items), n):
        yield items[i:i + n]

# ---------- Step B: Commons classic supplement for core words ----------
want = {}
for w in sorted(core_words):
    have = {r["file"] for r in index.get(w, [])}
    for acc in ("us", "uk"):
        fname = f"En-{acc}-{w}.ogg"
        if fname not in have:
            want[f"File:{fname}"] = (w, acc, fname)
print("supplement titles to check:", len(want), flush=True)

added = 0
def check_batch(titles):
    try:
        return api_imageinfo(titles)
    except Exception as e:
        print("api batch failed:", e, flush=True)
        return {}
title_list = list(want.keys())
with ThreadPoolExecutor(max_workers=6) as ex:
    for result in ex.map(check_batch, list(batches(title_list))):
        for title, info in result.items():
            if info is None:
                continue
            w, acc, fname = want[title]
            index.setdefault(w, []).append({
                "file": fname, "accent": acc,
                "license": info["license"], "artist": info["artist"],
                "mp3Url": transcode_url(fname),
            })
            added += 1
print("supplement added:", added, flush=True)

# ---------- Step C: license/artist fill for all records ----------
need = {}
for w, recs in index.items():
    for r in recs:
        if "license" not in r:
            need[f"File:{r['file']}"] = r
print("extmetadata to fill:", len(need), flush=True)
filled = 0
def fill_batch(titles):
    try:
        return api_imageinfo(titles)
    except Exception as e:
        print("api batch failed:", e, flush=True)
        return {}
with ThreadPoolExecutor(max_workers=6) as ex:
    for result in ex.map(fill_batch, list(batches(list(need.keys())))):
        for title, info in result.items():
            r = need[title]
            if info is None:
                r["license"] = "Unknown"; r["artist"] = ""
            else:
                r["license"] = info["license"] or "Unknown"
                r["artist"] = info["artist"]
            filled += 1
for recs in index.values():
    for r in recs:
        if "mp3Url" not in r:
            r["mp3Url"] = transcode_url(r["file"])
        r.setdefault("license", "Unknown")
        r.setdefault("artist", "")

# ---------- order: classic first, then LL/others; words sorted ----------
def rec_order(r):
    if r["file"].startswith("En-us-") or r["file"].startswith("En-uk-"):
        return 0
    if r["file"].startswith("LL-"):
        return 1
    return 2
out_index = {}
files = 0
by_accent = {"us": 0, "uk": 0, "other": 0}
for w in sorted(index):
    recs = sorted(index[w], key=rec_order)
    out_index[w] = recs
    files += len(recs)
    for r in recs:
        by_accent[r["accent"]] = by_accent.get(r["accent"], 0) + 1

with open(f"{BASE}/audio-index.json", "w", encoding="utf-8") as f:
    json.dump(out_index, f, ensure_ascii=False)
manifest = {
    "builder": "build_audio_index.py (stage1 stream) + build_audio_index_stage2.py",
    "words": len(out_index), "files": files, "byAccent": by_accent,
    "commonsSupplementChecked": len(want), "commonsSupplementAdded": added,
    "licensesFilled": filled,
}
with open(f"{BASE}/audio-index.manifest.json", "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=1, ensure_ascii=False)
print(json.dumps(manifest, ensure_ascii=False), flush=True)
