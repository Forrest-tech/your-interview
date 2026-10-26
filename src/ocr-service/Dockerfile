# Shared OCR service (ticket 59). Build context = this directory
# (docker-compose.yml: ocr-service.build.context = ./src/ocr-service).
FROM python:3.11-slim

# PaddleOCR runtime deps (OpenCV needs libGL on slim images).
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgl1 libglib2.0-0 \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt
COPY app.py ./

ENV OCR_PORT=5300
EXPOSE 5300
CMD ["python", "app.py"]
