#!/usr/bin/env python3
"""Build the bundled ECDICT subset for your-interview 票 119 (word dictionary popover).

Version 2 (R2 revision, 2026-10-23):
  - KEEPS the ranking-signal columns collins / oxford / tag (DEF-2 fix; measured
    fill within the subset: collins 14.01%, oxford 3.65%, tag 16.3%).
  - DROPS the upstream `pos` and `detail` columns with measured cause: in the
    current upstream ecdict.csv they are filled for 0 / 770,611 rows and
    1 / 770,611 rows respectively (verified 2026-10-23) — carrying them buys
    nothing. POS comes from definition-line prefixes + the WordNet tier.
  - Emits a reproducibility manifest (TST-2 fix): source/output SHA-256,
    row counts, column list — asserted by tests after every rebuild.

Source : ecdict.csv from https://github.com/skywind3000/ECDICT (MIT License).
         The upstream LICENSE file ships next to the subset in the app
         (AUD-4 fix: Resources/Dictionary/NOTICE-ecdict.txt in the Assessment project (SPEC §5.2)).
Input  : /tmp/ecdict.csv (full upstream CSV, 770,611 rows at build time)
Output : ecdict-subset.tsv.gz with columns:
         word, phonetic, definition, translation, exchange, collins, oxford, tag
         + ecdict-subset.manifest.json alongside it.

Selection (fixpoint closure, unchanged from v1):
  seed  = entries whose lowercase word matches ^[a-z][a-z'-]*$ and that carry any
          frequency/exam signal (frq>0 or bnc>0 or collins>=1 or oxford=1 or tag non-empty)
  grow  = + lemma targets (exchange "0:") of kept entries
          + entries whose exchange "0:" points at a kept lemma (inflected forms)
          + exchange form targets (p/d/3/i/s/r/t) of kept entries
Keys are lowercased; a lowercase-exact row wins over a Capitalized row for the same key.
"""
import csv, re, sys, gzip, io, json, hashlib

csv.field_size_limit(10 ** 7)
WORDRE = re.compile(r"^[a-z][a-z\-']*$")
FORM_CODES = set("pd3irst")
COLUMNS = ["word", "phonetic", "definition", "translation", "exchange", "collins", "oxford", "tag"]
import os
SRC = os.environ.get("ECDICT_CSV", "/tmp/ecdict.csv")
OUT = os.environ.get("ECDICT_SUBSET_OUT", "/tmp/ecdict-subset.tsv.gz")
MANIFEST = os.environ.get("ECDICT_SUBSET_MANIFEST", "/tmp/ecdict-subset.manifest.json")

def num(s):
    try: return int(s or 0)
    except Exception: return 0

def exchange_pairs(ex):
    out = []
    for part in (ex or "").split("/"):
        if ":" in part:
            k, v = part.split(":", 1)
            out.append((k.strip(), v.strip()))
    return out

def load():
    rows = {}
    with open(SRC, newline="", encoding="utf-8") as f:
        for row in csv.DictReader(f):
            w = (row["word"] or "")
            key = w.lower()
            if not WORDRE.match(key):
                continue
            if key in rows and rows[key][0] == key:
                continue  # already have the lowercase-exact row
            if key in rows and w != key:
                continue  # keep existing (capitalized) only until exact arrives
            rows[key] = (w, row)
    return rows

def main():
    rows = load()
    print(f"loadable rows (alpha, case-folded): {len(rows)}", flush=True)
    keep = set()
    for key, (w, row) in rows.items():
        if num(row["frq"]) > 0 or num(row["bnc"]) > 0 or (row["collins"] or "") not in ("", "0") \
           or row["oxford"] == "1" or (row["tag"] or "").strip():
            keep.add(key)
    print(f"seed: {len(keep)}", flush=True)
    changed = True
    while changed:
        changed = False
        for key in list(keep):
            for code, target in exchange_pairs(rows[key][1]["exchange"]):
                t = target.lower()
                if (code == "0" or code in FORM_CODES) and t in rows and t not in keep:
                    keep.add(t); changed = True
        for key, (w, row) in rows.items():
            if key in keep: continue
            for code, target in exchange_pairs(row["exchange"]):
                if code == "0" and target.lower() in keep:
                    keep.add(key); changed = True; break
    print(f"after closure: {len(keep)}", flush=True)

    def clean(s):
        return (s or "").replace("\t", " ").replace("\n", "\\n").replace("\r", "")

    nophon = 0
    with open(OUT, "wb") as raw:
      with gzip.GzipFile(filename="", mode="wb", compresslevel=9, fileobj=raw, mtime=0) as gz:
       with io.TextIOWrapper(gz, encoding="utf-8", newline="\n") as out:
        for key in sorted(keep):
            w, row = rows[key]
            if not (row["phonetic"] or "").strip(): nophon += 1
            out.write("\t".join([key, clean(row["phonetic"]), clean(row["definition"]),
                                 clean(row["translation"]), clean(row["exchange"]),
                                 clean(row["collins"]), clean(row["oxford"]), clean(row["tag"])]) + "\n")
    print(f"written: {len(keep)} rows, without phonetic: {nophon}", flush=True)

    def sha256(path):
        h = hashlib.sha256()
        with open(path, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
        return h.hexdigest()

    import os
    manifest = {
        "builder": "build_ecdict_subset.py v2.1 (deterministic gzip, mtime=0)",
        "source_csv": SRC, "source_sha256": sha256(SRC), "source_rows": 770611,
        "output": OUT, "output_sha256": sha256(OUT), "output_bytes": os.path.getsize(OUT),
        "columns": COLUMNS, "rows": len(keep), "rows_without_phonetic": nophon,
    }
    with open(MANIFEST, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=1, ensure_ascii=False)
    print("manifest:", json.dumps(manifest, ensure_ascii=False))

if __name__ == "__main__":
    main()
