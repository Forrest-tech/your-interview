# Shared OCR service

Platform capability (spec: `replica/platform-ocr-spec.md`): one shared,
stateless OCR service behind the YARP gateway for every module — Playbook
intake (first consumer, ticket 59), Tracker Smart paste, Resume, Tech Stack.
Consumers must not grow their own OCR implementations.

- **Engine:** PaddleOCR, PP-OCRv6 (medium det+rec), original image as-is.
  **No upscaling preprocessing** — 2× upscale was measured slower *and*
  worse in the same-image A/B test (`fix-verify/system-audit-20261021/ocr-test/AB-RESULT.md`).
- **Processing layer (in-service):** confidence noise filtering (dropped
  lines are counted in `droppedLines`, never silent), full-width →
  half-width punctuation normalization, reading order restored from bbox
  coordinates (row grouping, then left-to-right).
- **Data discipline:** images stay local on this path. Only consumers'
  fallback (vision model via AiGateway) ever sends an image out, and the
  consumer preview labels the source accordingly.

## API

| Endpoint | Description |
|---|---|
| `GET /health` (also `/health/live`) | `{status:"ok", engine, model}` |
| `POST /v1/ocr` | Body `{imageBase64, mimeType}` → `{status, engine, model, lines:[{text, confidence, bbox}], text, droppedLines, elapsedMs}`. `text` is the processed full text, one line per OCR line, in reading order. |
| `POST /v1/ocr/jobs` | Same body → `{jobId}` (async; in-memory job table with TTL, no persistence). |
| `GET /v1/ocr/jobs/{jobId}` | `{jobId, status: running\|done\|failed, result?, error?}` — `result` has the same shape as the sync response. |

Via the gateway the service is reachable under `/api/ocr/**`, which maps
to `/**` on this service (`/api/ocr/health`, `/api/ocr/v1/ocr`, …).

## Configuration (env only)

| Variable | Default | Meaning |
|---|---|---|
| `OCR_PORT` | `5300` | Listen port |
| `OCR_MIN_CONFIDENCE` | `0.5` | Lines below this confidence are dropped (and counted) |
| `OCR_ENABLE_MKLDNN` | `false` | Re-enable OneDNN. Keep `false` on paddlepaddle 3.3.1 (PIR bug with PP-OCRv6 medium); revisit when the paddle version is bumped |
| `OCR_JOB_TTL_SECONDS` | `600` | Async job retention |
| `OCR_LANG` | `ch` | PaddleOCR language (mixed Chinese/English) |

Sync vs async is a *consumer* choice — latency must be re-measured on the
target deployment machine before deciding (the sandbox figure, ~77 s per
image on 2 vCPU with MKLDNN off, is an environment outlier, not a
production number). Neither mode is hard-coded anywhere.

## Run

Local (reusing an existing venv with the pinned versions):

```bash
OCR_PORT=5300 python app.py
```

Container (wired in the repo-root `docker-compose.yml` as `ocr-service`):

```bash
docker compose up -d --build ocr-service
```
