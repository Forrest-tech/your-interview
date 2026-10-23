#!/usr/bin/env python3
"""票119 audio-index stage 1: stream-filter Kaikki en JSONL for subset words' sounds[].

Streams https://kaikki.org/dictionary/English/kaikki.org-dictionary-English.jsonl
(3.2 GB, never persisted) and writes kaikki-sounds.jsonl with, per matched word:
  {word, sounds: [{file, ipa?, tags, mp3Url?, oggUrl?}], ipas: {us?, uk?}}
Accent is derived later (stage 2) from tags/filename; license/artist come from
Commons extmetadata in stage 2 (SPEC §3.2 / RESEARCH §8.5).
"""
import json, sys, urllib.request

import os
BASE = os.environ.get("DICT_BUILD_DIR", os.path.dirname(os.path.abspath(__file__)))
URL = "https://kaikki.org/dictionary/English/kaikki.org-dictionary-English.jsonl"

vocab = set(json.load(open(f"{BASE}/vocab.json", encoding="utf-8")).keys())
print("vocab:", len(vocab), flush=True)

US_TAGS = ("US", "General-American", "American", "GA")
UK_TAGS = ("UK", "Received-Pronunciation", "British", "England", "RP")

def accent_of(tags, fname):
    t = " ".join(tags or [])
    if fname.startswith("En-uk-"): return "uk"
    if fname.startswith("En-us-"): return "us"
    if any(x in t for x in UK_TAGS): return "uk"
    if any(x in t for x in US_TAGS): return "us"
    return "other"

seen = {}
n_lines = 0
LOCAL = os.environ.get("KAIKKI_JSONL")
if LOCAL:
    # 本地已下载文件(可校验字节数,避免流式静默截断 —— 票119 实现期实测教训,见 VERIFY)
    resp = open(LOCAL, "rb")
else:
    req = urllib.request.Request(URL, headers={"User-Agent": "your-interview-dict-build/1.0"})
    resp = urllib.request.urlopen(req)
with resp, open(f"{BASE}/kaikki-sounds.jsonl", "w", encoding="utf-8") as out:
    for raw in resp:
        n_lines += 1
        if b'"sounds"' not in raw:
            continue
        try:
            line = raw.decode("utf-8", "replace")
            entry = json.loads(line)
        except Exception:
            continue
        word = (entry.get("word") or "").lower()
        if word not in vocab or word in seen:
            continue
        sounds = []
        ipas = {}
        for s in entry.get("sounds") or []:
            fname = s.get("audio") or ""
            if not fname:
                continue
            tags = s.get("tags") or []
            acc = accent_of(tags, fname)
            rec = {"file": fname, "accent": acc, "tags": tags}
            if s.get("ipa"): rec["ipa"] = s["ipa"]
            if s.get("mp3_url"): rec["mp3Url"] = s["mp3_url"]
            if s.get("ogg_url"): rec["oggUrl"] = s["ogg_url"]
            sounds.append(rec)
            if s.get("ipa") and acc in ("us", "uk") and acc not in ipas:
                ipas[acc] = s["ipa"]
        if sounds:
            seen[word] = 1
            out.write(json.dumps({"word": word, "sounds": sounds, "ipas": ipas}, ensure_ascii=False) + "\n")
        if n_lines % 200000 == 0:
            print(f"lines={n_lines} matched={len(seen)}", flush=True)
print(f"DONE lines={n_lines} words_with_sounds={len(seen)}", flush=True)
