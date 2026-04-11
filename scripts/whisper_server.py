#!/usr/bin/env python3
"""
OpenScanner Remote Whisper Transcription Server

A lightweight HTTP server that accepts audio files and returns transcriptions
using faster-whisper. Designed to run on a GPU-capable machine to offload
transcription from the Raspberry Pi.

Usage:
    pip install faster-whisper flask
    python whisper_server.py --model small.en --port 8090

Endpoints:
    POST /transcribe  - Upload a WAV file, get back {"text": "..."}
    GET  /health      - Returns {"status": "ok", "model": "..."}
"""

import argparse
import io
import logging
import sys
import tempfile
import os

from flask import Flask, request, jsonify

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s"
)
logger = logging.getLogger(__name__)

app = Flask(__name__)

# Globals set during startup
model = None
model_name = None

# Radio-context prompt (matches the prompt used by the local whisper service)
RADIO_PROMPT = (
    "Dispatch, Unit 1, 10-4, copy, over. Priority traffic, code 3 response "
    "to street intersection. Suspect description: white male, blue jeans. "
    "License plate, vehicle registration, bolo. Structure fire, medical "
    "emergency, staging area. Status check, affirmative, negative, stand by. "
    "Channel 2, tac channel, command post. "
    "Alpha Adam, Bravo Boy, Charlie Charles, David, Edward, Frank, George, "
    "Henry, Ida, John, King, Lincoln, Mary, Nora, Ocean, Paul, Queen, Robert, "
    "Sam, Tom, Union, Victor, William, X-ray, Young, Zebra. "
    "10-20 location, 10-8 in service, 10-7 out of service."
)


@app.route("/health", methods=["GET"])
def health():
    return jsonify({"status": "ok", "model": model_name})


@app.route("/transcribe", methods=["POST"])
def transcribe():
    if "file" not in request.files:
        return jsonify({"error": "No audio file provided. Send as 'file' in multipart form data."}), 400

    audio_file = request.files["file"]
    prompt = request.form.get("prompt", RADIO_PROMPT)

    # Write to a temporary file so faster-whisper can read it
    suffix = os.path.splitext(audio_file.filename)[1] if audio_file.filename else ".wav"
    with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as tmp:
        tmp_path = tmp.name
        audio_file.save(tmp)

    try:
        segments, info = model.transcribe(
            tmp_path,
            language="en",
            initial_prompt=prompt,
            vad_filter=True,
        )

        text = " ".join(segment.text.strip() for segment in segments).strip()
        logger.info("Transcribed %s (%.1fs audio): %s", audio_file.filename, info.duration, text[:100])

        # Filter blank audio markers
        if text.startswith("[") and text.endswith("]"):
            text = ""

        return jsonify({"text": text, "duration": info.duration})

    except Exception as e:
        logger.exception("Transcription failed")
        return jsonify({"error": str(e)}), 500

    finally:
        if os.path.exists(tmp_path):
            os.unlink(tmp_path)


def main():
    global model, model_name

    parser = argparse.ArgumentParser(description="OpenScanner Whisper Transcription Server")
    parser.add_argument("--model", default="small.en", help="Whisper model size (default: small.en)")
    parser.add_argument("--device", default="auto", help="Device: auto, cpu, or cuda (default: auto)")
    parser.add_argument("--port", type=int, default=8090, help="Port to listen on (default: 8090)")
    parser.add_argument("--host", default="0.0.0.0", help="Host to bind to (default: 0.0.0.0)")
    args = parser.parse_args()

    model_name = args.model
    logger.info("Loading model '%s' on device '%s'...", args.model, args.device)

    try:
        from faster_whisper import WhisperModel
        model = WhisperModel(args.model, device=args.device, compute_type="auto")
    except ImportError:
        logger.error("faster-whisper is not installed. Run: pip install faster-whisper")
        sys.exit(1)

    logger.info("Model loaded. Starting server on %s:%d", args.host, args.port)
    app.run(host=args.host, port=args.port, threaded=True)


if __name__ == "__main__":
    main()
