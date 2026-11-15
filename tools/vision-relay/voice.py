"""Optional push-to-talk Q&A. This module does not interact with the step judge."""

import base64
import io
import ipaddress
import json
import os
import re
import threading
import wave

import requests

MAX_REQUEST = 3 * 1024 * 1024
MAX_REPLY = 24000 * 2 * 30
API = "https://api.openai.com/v1"
_in_flight = threading.BoundedSemaphore(1)


def _post(path, key, **body):
    response = requests.post(
        API + path, headers={"Authorization": f"Bearer {key}"}, timeout=(2, 4), **body
    )
    response.raise_for_status()
    return response


def _answer(question, audio, image, key):
    transcript = _post(
        "/audio/transcriptions", key,
        files={"file": ("question.wav", audio, "audio/wav")},
        data={"model": "gpt-4o-mini-transcribe"},
    ).json()["text"].strip()
    if not transcript:
        raise ValueError("No speech detected")
    phrase = re.sub(r"[^a-z ]", "", transcript.lower()).strip()
    if phrase in {"whats next", "what do i do next", "what should i do next", "repeat", "repeat that"}:
        return question["instruction"]
    if phrase in {"which piece", "which part", "what piece do i need", "what part do i need"}:
        return question.get("part") or question["instruction"]

    content = [{"type": "text", "text": json.dumps({
        "question": transcript[:1000],
        "walkthrough": question["walkthrough"],
        "step_index": question["step"],
        "approved_instruction": question["instruction"],
        "part_cue": question.get("part") or "",
    })}]
    if image:
        content.append({"type": "image_url", "image_url": {
            "url": "data:image/jpeg;base64," + base64.b64encode(image).decode("ascii")
        }})
    result = _post("/chat/completions", key, json={
        "model": os.environ.get("FORGE_VOICE_MODEL", "gpt-4o-mini"),
        "messages": [
            {"role": "system", "content":
                "You are Forge, helping with the current LEGO assembly step. Answer in one or two short sentences "
                "using the approved instruction, part cue and optional image. If you cannot see a part clearly, say so. "
                "Never confirm that a step is complete or tell the builder to skip a step. "
                "Politely decline questions outside this assembly task."},
            {"role": "user", "content": content},
        ],
        "max_completion_tokens": 120,
    }).json()
    answer = result["choices"][0]["message"]["content"]
    return answer.strip()[:400] if answer and answer.strip() else question["instruction"]


def serve(handler):
    # The Quest reaches loopback through adb reverse. Do not expose cloud credentials
    # to requests from the LAN when the vision relay is bound to all interfaces.
    if not ipaddress.ip_address(handler.client_address[0]).is_loopback:
        handler._send(403, {"error": "Voice requires the USB relay"})
        return
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        handler._send(503, {"error": "Voice service is not configured"})
        return
    if not _in_flight.acquire(blocking=False):
        handler._send(429, {"error": "Voice service is busy"})
        return
    try:
        try:
            length = int(handler.headers.get("Content-Length", "0"))
            if not 0 < length <= MAX_REQUEST:
                handler._send(413, {"error": "Invalid voice request size"})
                return
            handler.connection.settimeout(5)
            question = json.loads(handler.rfile.read(length))
            if (question["walkthrough"] not in ("driver", "wheel")
                    or type(question["step"]) is not int or question["step"] < 0
                    or not isinstance(question["instruction"], str)
                    or not 0 < len(question["instruction"]) <= 300
                    or not isinstance(question.get("part", ""), str)
                    or len(question.get("part", "")) > 150):
                raise ValueError("Invalid assembly context")
            audio = base64.b64decode(question["audio"], validate=True)
            image = base64.b64decode(question.get("image", ""), validate=True)
            with wave.open(io.BytesIO(audio)) as wav:
                if (wav.getnchannels() != 1 or wav.getsampwidth() != 2
                        or not 8000 <= wav.getframerate() <= 48000
                        or not 0 < wav.getnframes() <= wav.getframerate() * 7):
                    raise ValueError("Invalid WAV format")
            if image and (not image.startswith(b"\xff\xd8") or len(image) > 1024 * 1024):
                raise ValueError("Invalid JPEG")
        except (ValueError, TypeError, KeyError, EOFError, TimeoutError, wave.Error):
            handler._send(400, {"error": "Invalid voice request"})
            return

        try:
            answer = _answer(question, audio, image, key)
            pcm = _post("/audio/speech", key, json={
                "model": "gpt-4o-mini-tts", "voice": "coral", "input": answer, "response_format": "pcm"
            }).content
            if not 0 < len(pcm) <= MAX_REPLY or len(pcm) % 2:
                raise ValueError("Invalid PCM reply")
        except (requests.RequestException, ValueError, KeyError, IndexError, TypeError):
            handler._send(502, {"error": "Could not answer voice question"})
            return
        handler._raw(200, pcm, "application/octet-stream")
    finally:
        _in_flight.release()
