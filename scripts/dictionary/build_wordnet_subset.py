#!/usr/bin/env python3
"""票119 WordNet subset builder (SPEC §3.2).

Inputs : WNdb 3.1 files (index.{noun,verb,adj,adv}, data.{noun,verb,adj,adv},
         {noun,verb,adj,adv}.exc) + the §3.1 vocabulary (ECDICT subset words).
Outputs: wordnet-subset.jsonl.gz  {lemma, posBlocks:[{pos, synsets:[{gloss, example?}]}]}
         wordnet-exceptions.json  {inflectedForm: [lemma, ...]}  (from .exc files)
         wordnet-subset.manifest.json
Deterministic: gzip mtime=0, sorted lemmas, WNdb synset order preserved.
"""
import gzip, hashlib, io, json, os, sys

BASE = os.path.dirname(os.path.abspath(__file__))
DICT_DIR = sys.argv[1] if len(sys.argv) > 1 else os.path.join(BASE, "dict")
SUBSET_TSV = sys.argv[2] if len(sys.argv) > 2 else os.path.join(BASE, "ecdict-subset.tsv.gz")
OUT_SUBSET = os.path.join(BASE, "wordnet-subset.jsonl.gz")
OUT_EXC = os.path.join(BASE, "wordnet-exceptions.json")
OUT_MANIFEST = os.path.join(BASE, "wordnet-subset.manifest.json")
POS_FILES = [("noun", "n"), ("verb", "v"), ("adj", "a"), ("adv", "r")]

def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()

def gz_write(path, text_iter):
    with open(path, "wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", compresslevel=6, fileobj=raw, mtime=0) as gz:
            for chunk in text_iter:
                gz.write(chunk.encode("utf-8"))

# ---- vocabulary (same word list as §3.1 subset) ----
vocab = set()
with gzip.open(SUBSET_TSV, "rt", encoding="utf-8") as f:
    for line in f:
        vocab.add(line.split("\t", 1)[0])
print("vocab:", len(vocab), flush=True)

# ---- pass 1: index files -> lemma -> {pos: [offsets in WNdb order]} ----
lemma_pos_offsets = {}
for fname, pos in POS_FILES:
    path = os.path.join(DICT_DIR, f"index.{fname}")
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            if line.startswith(" ") or not line.strip():
                continue  # 29-line license header / blanks
            parts = line.split()
            lemma_raw, lpos = parts[0], parts[1]
            synset_cnt = int(parts[2])
            p_cnt = int(parts[3])
            offsets = parts[4 + p_cnt + 2: 4 + p_cnt + 2 + synset_cnt]
            lemma = lemma_raw.replace("_", " ").lower()
            if lemma not in vocab:
                continue
            lemma_pos_offsets.setdefault(lemma, {}).setdefault(pos, []).extend(int(o) for o in offsets)
print("lemmas with synsets:", len(lemma_pos_offsets), flush=True)

needed = {pos: set() for _, pos in POS_FILES}
for poses in lemma_pos_offsets.values():
    for pos, offs in poses.items():
        needed[pos].update(offs)

# ---- pass 2: data files -> offset -> {gloss, example?} ----
def parse_gloss(gloss_raw):
    segs = [s.strip() for s in gloss_raw.split(";")]
    defs, examples = [], []
    for s in segs:
        if not s:
            continue
        if s.startswith('"'):
            examples.append(s.strip('"').strip())
        else:
            defs.append(s)
    out = {"gloss": "; ".join(defs)}
    if examples:
        out["example"] = examples[0]
    return out

synsets = {pos: {} for _, pos in POS_FILES}
for fname, pos in POS_FILES:
    path = os.path.join(DICT_DIR, f"data.{fname}")
    want = needed[pos]
    if not want:
        continue
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            if line.startswith(" ") or "|" not in line:
                continue
            head, gloss_raw = line.split("|", 1)
            offset = int(head.split()[0])
            if offset in want:
                synsets[pos][offset] = parse_gloss(gloss_raw.strip())
print("synsets extracted:", {p: len(s) for p, s in synsets.items()}, flush=True)

# ---- emit subset ----
POS_ORDER = ["n", "v", "a", "r"]
total_synsets = 0
def gen():
    global total_synsets
    for lemma in sorted(lemma_pos_offsets):
        blocks = []
        for pos in POS_ORDER:
            offs = lemma_pos_offsets[lemma].get(pos)
            if not offs:
                continue
            ss = []
            for o in offs:
                s = synsets[pos].get(o)
                if s:
                    ss.append(s); total_synsets += 1
            if ss:
                blocks.append({"pos": pos, "synsets": ss})
        if blocks:
            yield json.dumps({"lemma": lemma, "posBlocks": blocks}, ensure_ascii=False) + "\n"
gz_write(OUT_SUBSET, gen())

# ---- exceptions (.exc) ----
exc = {}
for fname, _ in POS_FILES:
    path = os.path.join(DICT_DIR, f"{fname}.exc")
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            parts = line.split()
            if len(parts) < 2:
                continue
            form = parts[0].replace("_", " ").lower()
            lemmas = [p.replace("_", " ").lower() for p in parts[1:]]
            exc.setdefault(form, set()).update(lemmas)
exc_out = {k: sorted(v) for k, v in sorted(exc.items())}
with open(OUT_EXC, "w", encoding="utf-8") as f:
    json.dump(exc_out, f, ensure_ascii=False, sort_keys=True)

src_sha = {}
for fname, _ in POS_FILES:
    for kind in ("index", "data"):
        p = os.path.join(DICT_DIR, f"{kind}.{fname}")
        src_sha[f"{kind}.{fname}"] = sha256(p)
    src_sha[f"{fname}.exc"] = sha256(os.path.join(DICT_DIR, f"{fname}.exc"))
manifest = {
    "builder": "build_wordnet_subset.py v1",
    "vocab_source": os.path.basename(SUBSET_TSV),
    "lemmas": sum(1 for _ in gzip.open(OUT_SUBSET, "rt", encoding="utf-8")),
    "synsets": total_synsets,
    "exception_forms": len(exc_out),
    "source_sha256": src_sha,
    "outputs": {
        "wordnet-subset.jsonl.gz": {"sha256": sha256(OUT_SUBSET), "bytes": os.path.getsize(OUT_SUBSET)},
        "wordnet-exceptions.json": {"sha256": sha256(OUT_EXC), "bytes": os.path.getsize(OUT_EXC)},
    },
}
with open(OUT_MANIFEST, "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=1, ensure_ascii=False)
print(json.dumps(manifest, ensure_ascii=False)[:600], flush=True)
