#!/usr/bin/env python3
"""票119 vocab 派生 (ECDICT 子集词 → rank/tag, 供 WordNet 子集与音频构建)。
Derive vocab.json (word -> {rank, tag}) for subset words from full ecdict.csv.
rank = frq if frq>0 else (bnc if bnc>0 else None). Restricted to subset keys."""
import csv, json, gzip

csv.field_size_limit(10 ** 7)
import os, sys
BASE = os.environ.get("DICT_BUILD_DIR", os.path.dirname(os.path.abspath(__file__)))
subset_words = set()
with gzip.open(f"{BASE}/ecdict-subset.tsv.gz", "rt", encoding="utf-8") as f:
    for line in f:
        subset_words.add(line.split("\t", 1)[0])
print("subset words:", len(subset_words))

vocab = {}
with open(f"{BASE}/ecdict.csv", newline="", encoding="utf-8") as f:
    for row in csv.DictReader(f):
        key = (row["word"] or "").lower()
        if key not in subset_words or key in vocab:
            continue
        def num(s):
            try: return int(s or 0)
            except Exception: return 0
        frq, bnc = num(row["frq"]), num(row["bnc"])
        rank = frq if frq > 0 else (bnc if bnc > 0 else None)
        vocab[key] = {"rank": rank, "tag": (row["tag"] or "").strip()}
with open(f"{BASE}/vocab.json", "w", encoding="utf-8") as f:
    json.dump(vocab, f, ensure_ascii=False)
ranked = sum(1 for v in vocab.values() if v["rank"])
tagged = sum(1 for v in vocab.values() if v["tag"])
core = sum(1 for v in vocab.values() if (v["rank"] and v["rank"] <= 6000) or v["tag"])
print(f"vocab: {len(vocab)} ranked: {ranked} tagged: {tagged} core(rank<=6000|tag): {core}")
