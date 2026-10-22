#!/usr/bin/env python3
"""Shared OCR service (ticket 59) — platform capability, spec: replica/platform-ocr-spec.md.

Stateless FastAPI service wrapping PaddleOCR (PP-OCRv6):
  GET  /health               -> {status, engine, model}
  GET  /health/live          -> same payload (gateway YARP health-check path)
  POST /v1/ocr               -> synchronous OCR of one image
  POST /v1/ocr/jobs          -> submit async job -> {jobId}
  GET  /v1/ocr/jobs/{jobId}  -> {jobId, status: running|done|failed, result?|error?}

In-service processing layer (spec-mandated, all of it):
  * original image as-is — NO upscaling/preprocessing (2x upscale measured
    slower AND worse in the A/B test, see ocr-test/AB-RESULT.md);
  * confidence noise filtering: lines below OCR_MIN_CONFIDENCE are dropped
    and counted in `droppedLines` (never silently);
  * punctuation normalization: full-width -> half-width punctuation
    (see _PUNCT_MAP; text content itself is never altered);
  * reading order: lines re-sorted by bbox coordinates (row grouping by
    vertical overlap, then left-to-right), never the engine's raw
    scatter order.

All behaviour switches come from env (see README.md); nothing is hard-coded.
The engine is loaded lazily on first use and guarded by a lock — model load
is slow (~10s) and the service is single-process by design.
"""
import base64
import binascii
import os
import threading
import time
import uuid
from concurrent.futures import ThreadPoolExecutor

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

# ---------------------------------------------------------------------------
# Configuration (env only)
# ---------------------------------------------------------------------------
PORT = int(os.environ.get("OCR_PORT", "5300"))
MIN_CONFIDENCE = float(os.environ.get("OCR_MIN_CONFIDENCE", "0.5"))
ENABLE_MKLDNN = os.environ.get("OCR_ENABLE_MKLDNN", "false").strip().lower() in (
    "1", "true", "yes", "on")
JOB_TTL_SECONDS = int(os.environ.get("OCR_JOB_TTL_SECONDS", "600"))
LANG = os.environ.get("OCR_LANG", "ch")
ENGINE_NAME = "paddleocr"
MODEL_NAME = "PP-OCRv6"

# ---------------------------------------------------------------------------
# Punctuation normalization: full-width -> half-width.
# Only punctuation/symbols are mapped; letters, digits (incl. full-width
# digits/letters, which are content-bearing in mixed text) and CJK
# characters are left untouched.
# ---------------------------------------------------------------------------
_PUNCT_MAP = {
    "～": "~",  # FULLWIDTH TILDE (the A/B case: meeting-card time range)
    "〜": "~",  # WAVE DASH
    "：": ":",
    "，": ",",
    "。": ".",
    "！": "!",
    "？": "?",
    "；": ";",
    "（": "(",
    "）": ")",
    "【": "[",
    "】": "]",
    "「": '"',
    "」": '"',
    "『": '"',
    "』": '"',
    "“": '"',
    "”": '"',
    "‘": "'",
    "’": "'",
    "、": ",",
    "｜": "|",
    "／": "/",
    "＼": "\\",
    "－": "-",
    "—": "-",
    "–": "-",
    "·": ".",
    "…": "...",
    "　": " ",  # IDEOGRAPHIC SPACE -> normal space
}
_PUNCT_TABLE = str.maketrans(_PUNCT_MAP)


def normalize_punctuation(text: str) -> str:
    """Full-width -> half-width punctuation normalization (idempotent)."""
    return text.translate(_PUNCT_TABLE)


def sort_reading_order(lines):
    """Restore reading order from bboxes.

    Lines are grouped into rows: a line joins the current row when its
    vertical span overlaps the row's span by at least half of the smaller
    height (this tolerates the few-px y jitter between columns on the
    same visual row, e.g. sender line vs. right-side icons). Rows are
    ordered top-to-bottom, lines inside a row left-to-right.
    Input/output items are dicts with bbox=[x0, y0, x1, y1].
    """
    items = sorted(lines, key=lambda l: (l["bbox"][1], l["bbox"][0]))
    rows = []  # each: [y0, y1, [items]]
    for it in items:
        y0, y1 = it["bbox"][1], it["bbox"][3]
        placed = False
        if rows:
            ry0, ry1, members = rows[-1]
            overlap = min(y1, ry1) - max(y0, ry0)
            smaller = min(y1 - y0, ry1 - ry0)
            if smaller > 0 and overlap >= 0.5 * smaller:
                members.append(it)
                rows[-1][0] = min(ry0, y0)
                rows[-1][1] = max(ry1, y1)
                placed = True
        if not placed:
            rows.append([y0, y1, [it]])
    out = []
    for _, _, members in rows:
        out.extend(sorted(members, key=lambda l: l["bbox"][0]))
    return out


# ---------------------------------------------------------------------------
# Engine (lazy singleton)
# ---------------------------------------------------------------------------
_engine = None
_engine_lock = threading.Lock()


def get_engine():
    global _engine
    if _engine is None:
        with _engine_lock:
            if _engine is None:
                from paddleocr import PaddleOCR

                _engine = PaddleOCR(
                    lang=LANG,
                    use_doc_orientation_classify=False,
                    use_doc_unwarping=False,
                    use_textline_orientation=False,
                    # paddlepaddle 3.3.1 OneDNN PIR bug on PP-OCRv6 medium
                    # models; default off, re-enable via OCR_ENABLE_MKLDNN.
                    enable_mkldnn=ENABLE_MKLDNN,
                )
    return _engine


def run_ocr(image_bytes: bytes) -> dict:
    """One OCR pass over the original image bytes. Returns the response
    shape shared by the sync endpoint and async job results."""
    import tempfile

    started = time.time()
    ocr = get_engine()
    with tempfile.NamedTemporaryFile(suffix=".png", delete=False) as fh:
        fh.write(image_bytes)
        tmp_path = fh.name
    try:
        raw_lines = _predict(ocr, tmp_path)
    finally:
        os.unlink(tmp_path)

    kept, dropped = [], 0
    for text, conf, bbox in raw_lines:
        text = normalize_punctuation(text)
        if conf < MIN_CONFIDENCE:
            dropped += 1
            continue
        kept.append({"text": text, "confidence": round(conf, 4), "bbox": bbox})
    kept = sort_reading_order(kept)
    return {
        "status": "ok",
        "engine": ENGINE_NAME,
        "model": MODEL_NAME,
        "lines": kept,
        "text": "\n".join(l["text"] for l in kept),
        "droppedLines": dropped,
        "elapsedMs": int((time.time() - started) * 1000),
    }


def _predict(ocr, img_path: str):
    """PaddleOCR 3.x predict() with the 2.x ocr() fallback (same approach
    as the verified sample ~/workspace/ocr-ab/run_ocr.py). Returns a list
    of (text, confidence, [x0, y0, x1, y1])."""
    import json as _json

    out = []
    try:
        results = ocr.predict(img_path)
        for res in results:
            data = res.json if hasattr(res, "json") else res
            if isinstance(data, str):
                data = _json.loads(data)
            res_dict = data.get("res", data) if isinstance(data, dict) else {}
            if not res_dict.get("rec_texts"):
                try:
                    res_dict = {
                        "rec_texts": res["rec_texts"],
                        "rec_scores": res["rec_scores"],
                        "rec_polys": res["rec_polys"],
                    }
                except Exception:
                    pass
            texts = res_dict.get("rec_texts") or []
            scores = res_dict.get("rec_scores") or []
            polys = res_dict.get("rec_polys") or res_dict.get("dt_polys") or []
            for txt, sc, poly in zip(texts, scores, polys):
                xs = [float(p[0]) for p in poly]
                ys = [float(p[1]) for p in poly]
                out.append((txt, float(sc),
                            [round(min(xs)), round(min(ys)),
                             round(max(xs)), round(max(ys))]))
        return out
    except Exception:
        result = ocr.ocr(img_path)
        if result and result[0]:
            for box, (txt, sc) in result[0]:
                xs = [float(p[0]) for p in box]
                ys = [float(p[1]) for p in box]
                out.append((txt, float(sc),
                            [round(min(xs)), round(min(ys)),
                             round(max(xs)), round(max(ys))]))
        return out


# ---------------------------------------------------------------------------
# Async jobs (in-memory table + TTL; the service itself stays stateless —
# no persistence, jobs vanish on restart by design)
# ---------------------------------------------------------------------------
_jobs = {}
_jobs_lock = threading.Lock()
_executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="ocr-job")


def _purge_jobs():
    now = time.time()
    with _jobs_lock:
        for jid in [j for j, v in _jobs.items()
                    if now - v["created"] > JOB_TTL_SECONDS]:
            del _jobs[jid]


def _run_job(job_id: str, image_bytes: bytes):
    try:
        result = run_ocr(image_bytes)
        with _jobs_lock:
            if job_id in _jobs:
                _jobs[job_id].update(status="done", result=result)
    except Exception as ex:  # surfaced as failed, never swallowed
        with _jobs_lock:
            if job_id in _jobs:
                _jobs[job_id].update(status="failed", error=str(ex))


# ---------------------------------------------------------------------------
# HTTP API
# ---------------------------------------------------------------------------
app = FastAPI(title="your-interview shared OCR service")


class OcrRequest(BaseModel):
    imageBase64: str
    mimeType: str | None = None


def _decode(body: OcrRequest) -> bytes:
    if not body.imageBase64:
        raise HTTPException(status_code=400, detail="imageBase64 is required")
    try:
        return base64.b64decode(body.imageBase64, validate=True)
    except (binascii.Error, ValueError):
        raise HTTPException(status_code=400,
                            detail="imageBase64 is not valid base64")


@app.get("/health")
@app.get("/health/live")
def health():
    return {"status": "ok", "engine": ENGINE_NAME, "model": MODEL_NAME}


@app.post("/v1/ocr")
def ocr_sync(body: OcrRequest):
    image = _decode(body)
    try:
        return run_ocr(image)
    except Exception as ex:
        raise HTTPException(status_code=500, detail=f"ocr failed: {ex}")


@app.post("/v1/ocr/jobs")
def ocr_submit(body: OcrRequest):
    image = _decode(body)
    _purge_jobs()
    job_id = uuid.uuid4().hex
    with _jobs_lock:
        _jobs[job_id] = {"status": "running", "created": time.time(),
                         "result": None, "error": None}
    _executor.submit(_run_job, job_id, image)
    return {"jobId": job_id}


@app.get("/v1/ocr/jobs/{job_id}")
def ocr_job_status(job_id: str):
    _purge_jobs()
    with _jobs_lock:
        job = _jobs.get(job_id)
        if job is None:
            raise HTTPException(status_code=404, detail="job not found")
        out = {"jobId": job_id, "status": job["status"]}
        if job["status"] == "done":
            out["result"] = job["result"]
        elif job["status"] == "failed":
            out["error"] = job["error"]
        return out


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="0.0.0.0", port=PORT)
