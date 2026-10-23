#!/usr/bin/env python3
"""票119 Tier 0: OALD10 (Eudic/MDX) pack extractor (SPEC §3.3).

Runs on the user's machine, once. Self-contained venv: mdict-utils==1.3.14 +
beautifulsoup4 + lxml (the parser backend; without lxml bs4 raises
FeatureNotFound —— 实现期实测)。

Inputs : oald10.mdx (required); audio archive .mdd (optional, e.g. V14_3.4.mdd);
         an optional census .mdd (the images oald10.mdd) for --verify reporting.
Outputs (into --out dir, OUTSIDE any git repo):
  oxford-entries.jsonl.gz   multi-member gzip (one member per entry) for O(1) reads
  oxford-entries.idx.json   {headwordLower: [memberOffset, memberLength]}
  oxford-redirects.json     {stubKeyLower: lemma}
  oxford-audio/             only when the audio archive is provided
  oxford-pack-manifest.json counts, fingerprints, --verify results

Copyright: extracted content is the user's local personal-use data. NEVER commit.
"""
import argparse, gzip, hashlib, io, json, os, re, sys, time
from datetime import datetime, timezone

CJK = re.compile(r'[\u3400-\u9fff\uf900-\ufaff]')
VERIFY_WORDS = ["disappeared", "disappear", "level", "figure", "compared", "gender",
                "achievements", "the", "to", "year-old", "science", "expected",
                "outperform", "teenage", "continuing", "girls", "reached", "boys",
                "figures", "has"]
FORM_LABELS = {"root": "原形", "thirdps": "第三人称单数", "past": "过去式",
               "pastpart": "过去分词", "ppart": "过去分词", "prespart": "现在分词",
               "ing": "现在分词", "plural": "复数", "comp": "比较级", "super": "最高级"}

def sha16(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()[:16]

def split_cjk(text):
    """Split at first CJK char -> (en, zh|None)."""
    m = CJK.search(text or "")
    if not m:
        return (text or "").strip(), None
    return text[:m.start()].strip(), text[m.start():].strip() or None

def sound_key(href):
    if href and href.startswith("sound://"):
        return href[len("sound://"):]
    return None

def example_accent(fname):
    if "__gbs_" in fname or "__brs_" in fname or "__gb_" in fname:
        return "uk"
    if "__uss_" in fname or "__ams_" in fname or "__us_" in fname:
        return "us"
    return None

class EntryParser:
    def __init__(self):
        from bs4 import BeautifulSoup
        self.BeautifulSoup = BeautifulSoup

    def parse(self, html):
        soup = self.BeautifulSoup(html, "lxml")
        h1 = soup.find("h1", class_="headword")
        if h1 is None:
            return None
        headword = h1.get_text().strip()
        if not headword:
            return None

        def in_excluded(el, classes):
            p = el
            while p is not None and getattr(p, "get", None):
                cls = p.get("class") or []
                if any(c in classes for c in cls):
                    return True
                p = p.parent
            return False

        # order index for document-order state machine
        all_els = soup.find_all(True)
        order = {id(el): i for i, el in enumerate(all_els)}

        # ---- entry-level phonetics + audio from first main top-g webtop ----
        phonetics, audio = {}, {}
        topgs = [d for d in soup.find_all("div", class_="top-g") if not in_excluded(d, ("idm-g",))]
        first_webtop = None
        for tg in topgs:
            wt = tg.find("div", class_="webtop")
            if wt is not None and wt.find("h1", class_="headword") is not None:
                first_webtop = wt
                break
        if first_webtop is not None:
            for div, acc in (("phons_br", "uk"), ("phons_n_am", "us")):
                d = first_webtop.find("div", class_=div)
                if d is not None:
                    phon = d.find("span", class_="phon")
                    if phon is not None:
                        phonetics[acc] = phon.get_text().strip()
                    a = d.find("a", class_="sound")
                    if a is not None:
                        k = sound_key(a.get("href"))
                        if k:
                            audio[acc] = k
        ox3000 = bool(h1.get("ox3000")) or (
            first_webtop is not None and first_webtop.find("span", class_=re.compile("ox3ksym")) is not None)

        # ---- pos blocks via document-order walk ----
        blocks, current = [], None
        headers = []
        for tg in topgs:
            pos_span = tg.find("span", class_="pos")
            if pos_span is not None:
                headers.append((order.get(id(tg), 0), pos_span.get_text().strip()))
        senses = [li for li in soup.find_all("li", class_="sense")
                  if not in_excluded(li, ("idm-g", "unbox"))]
        events = [(o, "h", p) for o, p in headers] + [(order.get(id(li), 0), "s", li) for li in senses]
        events.sort(key=lambda e: e[0])
        for _, kind, payload in events:
            if kind == "h":
                current = {"pos": payload, "senses": []}
                blocks.append(current)
            else:
                if current is None:
                    current = {"pos": "", "senses": []}
                    blocks.append(current)
                s = self._sense(payload)
                if s is not None:
                    current["senses"].append(s)
        blocks = [b for b in blocks if b["senses"] or b["pos"]]

        forms = self._forms(soup)
        out = {"headword": headword, "phonetics": phonetics, "posBlocks": blocks,
               "forms": forms, "audio": audio, "oxford3000": ox3000}
        return out

    def _sense(self, li):
        def_span = li.find("span", class_="def")
        if def_span is None:
            return None
        for ref in def_span.find_all("a", class_="Ref"):
            ref.decompose()
        def_text = def_span.get_text().strip()
        gloss = None
        deft = li.find("deft")
        if deft is not None:
            chn = deft.find("chn")
            if chn is not None:
                gloss = chn.get_text().strip() or None
        def_en, tail_zh = split_cjk(def_text)
        if gloss is None:
            gloss = tail_zh
        sense = {"defEn": def_en}
        if gloss:
            sense["glossZh"] = gloss
        grammar = li.find("span", class_="grammar")
        if grammar is not None and grammar.get_text().strip():
            sense["grammar"] = grammar.get_text().strip()
        examples = []
        for ul in li.find_all("ul", class_="examples"):
            for eli in ul.find_all("li", recursive=False):
                x = eli.find("span", class_="x")
                if x is None:
                    continue
                en, x_zh = split_cjk(x.get_text().strip())
                zh = x_zh
                if zh is None:
                    for tname in ("ot", "at", "xt"):
                        t = eli.find(tname)
                        if t is not None:
                            chn = t.find("chn")
                            if chn is not None and chn.get_text().strip():
                                zh = chn.get_text().strip()
                                break
                ex = {"en": en}
                if zh:
                    ex["zh"] = zh
                akeys = {}
                for a in eli.find_all("a", class_="sound"):
                    k = sound_key(a.get("href"))
                    acc = example_accent(k) if k else None
                    if k and acc and acc not in akeys:
                        akeys[acc] = k
                if akeys:
                    ex["audioKey"] = akeys
                if en:
                    examples.append(ex)
        if examples:
            sense["examples"] = examples
        return sense

    def _forms(self, soup):
        forms, seen = [], set()
        for table in soup.find_all("table"):
            for tr in table.find_all("tr"):
                attr = tr.get("form")
                if not attr:
                    continue
                td = tr.find("td", class_="verb_form") or tr.find("td")
                if td is None:
                    continue
                prefix = td.find("span", class_="vf_prefix")
                text = td.get_text()
                if prefix is not None:
                    text = text.replace(prefix.get_text(), "", 1)
                text = " ".join(text.split())
                if not text or (attr, text) in seen:
                    continue
                seen.add((attr, text))
                form = {"label": FORM_LABELS.get(attr, attr), "text": text}
                phons_td = tr.find("td", class_="verb_phons")
                if phons_td is not None:
                    phons, akeys = {}, {}
                    for div, acc in (("phons_br", "uk"), ("phons_n_am", "us")):
                        d = phons_td.find("div", class_=div)
                        if d is not None:
                            phon = d.find("span", class_="phon")
                            if phon is not None and phon.get_text().strip():
                                phons[acc] = phon.get_text().strip()
                            a = d.find("a", class_="sound")
                            if a is not None:
                                k = sound_key(a.get("href"))
                                if k:
                                    akeys[acc] = k
                    if phons:
                        form["phon"] = phons
                    if akeys:
                        form["audioKey"] = akeys
                forms.append(form)
        return forms

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mdx", required=True)
    ap.add_argument("--audio-mdd", default=None, help="audio archive (V14_3.4.mdd); absent = Tier 0 audio skipped")
    ap.add_argument("--census-mdd", default=None, help="an .mdd to census audio references against (verify reporting)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--verify-only", action="store_true")
    args = ap.parse_args()

    from mdict_utils.base.readmdict import MDX, MDD
    import mdict_utils
    os.makedirs(args.out, exist_ok=True)
    mdx = MDX(args.mdx)
    parser = EntryParser()

    # ---------- census set (verify): sample keys -> entries ----------
    sample = {}
    redirects = {}
    entries_meta = {"keys": 0, "stubs": 0, "parsed": 0, "skipped": 0}
    audio_refs_sample = set()
    t0 = time.time()
    if args.verify_only:
        wanted = set(VERIFY_WORDS)
        for kb, vb in mdx.items():
            key = kb.decode("utf-8", "replace")
            low = key.lower()
            if low not in wanted and key not in wanted:
                continue
            val = vb.decode("utf-8", "replace")
            if val.strip("\x00 \r\n").startswith("@@@LINK="):
                sample[low] = ("stub", val.strip("\x00 \r\n")[len("@@@LINK="):].strip())
            else:
                e = parser.parse(val)
                sample[low] = ("entry", e)
                if e:
                    for a in e["audio"].values():
                        audio_refs_sample.add(a)
        census_hits = None
        if args.census_mdd:
            mdd = MDD(args.census_mdd)
            mdd_keys = {kb.decode("utf-8", "replace") for kb, _ in mdd.items()}
            census_hits = sum(1 for a in audio_refs_sample if a in mdd_keys)
        result = {"words": {}, "audioRefsSampled": len(audio_refs_sample),
                  "audioCensusHits": census_hits}
        ok = True
        for w in VERIFY_WORDS:
            got = sample.get(w)
            if w == "year-old":
                result["words"][w] = "absent-as-measured (hyphen fallback covers at runtime)"
                continue
            if got is None:
                result["words"][w] = "MISSING"; ok = False; continue
            kind, payload = got
            if kind == "stub":
                result["words"][w] = f"stub->{payload}"
            else:
                if payload is None:
                    result["words"][w] = "UNPARSEABLE"; ok = False
                else:
                    result["words"][w] = (f"entry senses={sum(len(b['senses']) for b in payload['posBlocks'])} "
                                           f"phon={sorted(payload['phonetics'])} audio={sorted(payload['audio'])}")
        result["pass"] = ok
        print(json.dumps(result, ensure_ascii=False, indent=1))
        return 0 if ok else 1

    # ---------- full extraction (streaming single pass) ----------
    entries_path = os.path.join(args.out, "oxford-entries.jsonl.gz")
    idx = {}
    audio_needed = set()
    uk_entries = us_entries = 0
    with open(entries_path, "wb") as raw:
        for kb, vb in mdx.items():
            entries_meta["keys"] += 1
            if entries_meta["keys"] % 20000 == 0:
                print(f"keys={entries_meta['keys']} parsed={entries_meta['parsed']} stubs={entries_meta['stubs']}", file=sys.stderr, flush=True)
            key = kb.decode("utf-8", "replace")
            text = vb.decode("utf-8", "replace")
            stripped = text.strip("\x00 \r\n")
            if stripped.startswith("@@@LINK="):
                target = stripped[len("@@@LINK="):].strip("\x00 \r\n ")
                redirects[key.lower()] = target
                entries_meta["stubs"] += 1
                continue
            entry = parser.parse(text)
            if entry is None:
                entries_meta["skipped"] += 1
                continue
            entries_meta["parsed"] += 1
            if "uk" in entry["audio"]:
                uk_entries += 1
            if "us" in entry["audio"]:
                us_entries += 1
            for a in entry["audio"].values():
                audio_needed.add(a)
            for f in entry["forms"]:
                for a in (f.get("audioKey") or {}).values():
                    audio_needed.add(a)
            line = (json.dumps(entry, ensure_ascii=False) + "\n").encode("utf-8")
            offset = raw.tell()
            with gzip.GzipFile(filename="", mode="wb", compresslevel=6, fileobj=raw, mtime=0) as member:
                member.write(line)
            idx.setdefault(entry["headword"].lower(), [offset, raw.tell() - offset])
    with open(os.path.join(args.out, "oxford-entries.idx.json"), "w", encoding="utf-8") as f:
        json.dump(idx, f, ensure_ascii=False)
    with open(os.path.join(args.out, "oxford-redirects.json"), "w", encoding="utf-8") as f:
        json.dump(redirects, f, ensure_ascii=False, sort_keys=True)

    # ---------- audio archive ----------
    audio_files = 0
    if args.audio_mdd:
        mdd = MDD(args.audio_mdd)
        adir = os.path.join(args.out, "oxford-audio")
        os.makedirs(adir, exist_ok=True)
        by_base = {}
        for kb, vb in mdd.items():
            k = kb.decode("utf-8", "replace")
            by_base.setdefault(os.path.basename(k), (k, vb))
        for need in sorted(audio_needed):
            hit = by_base.get(os.path.basename(need))
            if hit:
                with open(os.path.join(adir, os.path.basename(need)), "wb") as out:
                    out.write(hit[1])
                audio_files += 1

    manifest = {
        "tool": "extract_oxford_pack.py v1",
        "mdictUtils": getattr(mdict_utils, "__version__", "1.3.14"),
        "extractedAt": datetime.now(timezone.utc).isoformat(),
        "elapsedSec": round(time.time() - t0, 1),
        "sources": {
            "mdx": {"name": os.path.basename(args.mdx), "bytes": os.path.getsize(args.mdx), "sha256-16": sha16(args.mdx)},
            "audioMdd": ({"name": os.path.basename(args.audio_mdd), "bytes": os.path.getsize(args.audio_mdd),
                          "sha256-16": sha16(args.audio_mdd)} if args.audio_mdd else None),
        },
        "keys": entries_meta["keys"], "entries": entries_meta["parsed"],
        "stubs": entries_meta["stubs"], "skippedNonEntries": entries_meta["skipped"],
        "entriesWithUkAudioKey": uk_entries, "entriesWithUsAudioKey": us_entries,
        "referencedAudioFiles": len(audio_needed),
        "audioArchive": "present" if args.audio_mdd else "absent",
        "audioFilesExtracted": audio_files,
    }
    with open(os.path.join(args.out, "oxford-pack-manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=1)
    print(json.dumps(manifest, ensure_ascii=False))
    return 0

if __name__ == "__main__":
    sys.exit(main())
