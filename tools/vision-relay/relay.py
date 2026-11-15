#!/usr/bin/env python3
"""Forge XR vision relay: the Quest POSTs a camera frame, the relay asks a hosted vision model where the LEGO
parts are, and returns normalised boxes. The API key stays on this laptop.

  python3 relay.py                 # NVIDIA Nemotron VL, port 8787, key from NVIDIA_API_KEY or ~/nvapi.env
  python3 relay.py --port 8787 --log-dir ~/forge-relay-log --no-log

Quest side: Forge.CloudVisionClassifier POSTs image/jpeg to http://<this-laptop>:8787/detect
(over USB: `adb reverse tcp:8787 tcp:8787`, then the default http://127.0.0.1:8787 works).

Response: {"detections": [{"partId", "x0", "y0", "x1", "y1", "confidence"}], "seconds", "model", "error"}
with coordinates normalised 0-1 relative to the posted image (x from the left, y from the top).
GET /health returns {"ok": true, "model": ...}.
"""
import argparse, base64, io, json, os, pathlib, sys, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import requests
from PIL import Image

NVIDIA_URL = "https://integrate.api.nvidia.com/v1/chat/completions"
NVIDIA_MODEL = "nvidia/nemotron-3.5-super-vl-120b-a12b"

# Part ids must match Forge.PartIds on the Quest side.
PARTS = {
    "legs": "tan/beige LEGO minifigure legs (hip piece with two legs)",
    "torso": "light-blue printed LEGO minifigure torso; its two yellow arms belong to the torso",
    "head": "yellow LEGO minifigure head: a small round yellow cylinder with a printed face (not the torso's arms, "
            "not the yellow pin on the car)",
    "hairpiece": "orange LEGO minifigure hair piece",
    "tire": "black rubber LEGO tire (a black ring), with or without a rim inside",
    "rim": "grey or white plastic LEGO wheel hub on its own, without a tire",
    "axle-pin": "small yellow axle pin sticking out of the left side of the LEGO car chassis",
}

PROMPT = (
    "Find these LEGO parts on the table if they are visible:\n"
    + "\n".join(f"- {k}: {v}" for k, v in PARTS.items())
    + "\nReport every part you can see, including small or distant ones, but do not guess. Each part appears at most once. Reply with only JSON: "
      '[{"label": one of the ids above, "x0": left, "y0": top, "x1": right, "y1": bottom}] '
      "with coordinates normalized 0-1000 (x across the width from the left, y down the height from the top). "
      "Reply [] if none are visible."
)


def nvidia_key():
    k = os.environ.get("NVIDIA_API_KEY", "").strip()
    if not k:
        p = pathlib.Path.home() / "nvapi.env"
        if p.exists():
            line = next((l for l in p.read_text().splitlines() if l.strip() and not l.startswith("#")), "")
            k = (line.split("=", 1)[1] if "=" in line else line).strip().strip("'\"")
    if not k:
        sys.exit("No NVIDIA key: set NVIDIA_API_KEY or put the key in ~/nvapi.env")
    return k


def parse_json_array(text):
    start, end = text.find("["), text.rfind("]")
    if start < 0 or end < start:
        return []
    try:
        data = json.loads(text[start:end + 1])
    except ValueError:
        return []
    return data if isinstance(data, list) else []


class NvidiaDetector:
    model = NVIDIA_MODEL

    def __init__(self, key, upscale_to=640):
        self.key = key
        self.upscale_to = upscale_to
        self._local = threading.local()  # one HTTP session per relay thread (requests in parallel)

    def detect(self, jpeg_bytes):
        im = Image.open(io.BytesIO(jpeg_bytes)).convert("RGB")
        if max(im.size) < self.upscale_to:  # small Quest frames read better upscaled
            s = self.upscale_to / max(im.size)
            im = im.resize((round(im.width * s), round(im.height * s)), Image.BICUBIC)
        buf = io.BytesIO()
        im.save(buf, "JPEG", quality=90)
        body = {
            "model": self.model, "max_tokens": 1500, "temperature": 0,
            # Reasoning off: ~2 s instead of 10-30 s, and the answer is never starved by reasoning tokens
            # (with reasoning on, the reply was often empty: finish_reason=length).
            "chat_template_kwargs": {"enable_thinking": False},
            "messages": [
                {"role": "user", "content": [
                    {"type": "text", "text": PROMPT},
                    {"type": "image_url", "image_url": {"url": "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode()}},
                ]},
            ],
        }
        if not hasattr(self._local, "session"):
            self._local.session = requests.Session()
        r = self._local.session.post(NVIDIA_URL, json=body, timeout=120,
                              headers={"Authorization": f"Bearer {self.key}", "Accept": "application/json"})
        if r.status_code != 200:
            raise RuntimeError(f"NVIDIA HTTP {r.status_code}: {r.text[:200]}")
        content = r.json()["choices"][0]["message"].get("content") or ""
        out = []
        for d in parse_json_array(content):
            if not isinstance(d, dict) or d.get("label") not in PARTS:
                continue
            try:
                x0, y0, x1, y1 = (float(d[k]) / 1000.0 for k in ("x0", "y0", "x1", "y1"))
            except (KeyError, TypeError, ValueError):
                continue
            clamp = lambda v: min(1.0, max(0.0, v))
            out.append({"partId": d["label"], "x0": clamp(min(x0, x1)), "y0": clamp(min(y0, y1)),
                        "x1": clamp(max(x0, x1)), "y1": clamp(max(y0, y1)), "confidence": 0.8})
        return out, content


class RateLimiter:
    """Sliding-window limit on upstream API calls (NVIDIA free tier: 40 requests/minute)."""

    def __init__(self, per_minute):
        self.per_minute = per_minute
        self.calls = []
        self.lock = threading.Lock()

    def try_acquire(self):
        now = time.time()
        with self.lock:
            self.calls = [t for t in self.calls if now - t < 60.0]
            if self.per_minute > 0 and len(self.calls) >= self.per_minute:
                return False
            self.calls.append(now)
            return True


def make_handler(detector, log_dir, limiter):
    lock = threading.Lock()
    counter = {"n": 0}

    class Handler(BaseHTTPRequestHandler):
        def _send(self, code, obj):
            data = json.dumps(obj).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            if self.path.startswith("/health"):
                self._send(200, {"ok": True, "model": detector.model})
            else:
                self._send(404, {"error": "not found"})

        def do_POST(self):
            if not self.path.startswith("/detect"):
                self._send(404, {"error": "not found"})
                return
            length = int(self.headers.get("Content-Length", "0"))
            jpeg = self.rfile.read(length)
            frame_id = self.headers.get("X-Frame-Id", "?")
            t0 = time.time()
            if not limiter.try_acquire():
                # Over the upstream cap: answer at once with an error (the Quest backs off, boxes are kept).
                self._send(200, {"detections": [], "seconds": 0, "model": detector.model, "error": "rate limited (relay)"})
                print(f"[relay] frame {frame_id}: skipped, over {limiter.per_minute}/min", flush=True)
                return
            try:
                dets, raw = detector.detect(jpeg)
                err = None
            except Exception as e:  # noqa: BLE001 - report to the Quest, keep serving
                dets, raw, err = [], "", str(e)
            dt = time.time() - t0
            self._send(200, {"detections": dets, "seconds": round(dt, 2), "model": detector.model, "error": err})
            labels = ", ".join(d["partId"] for d in dets) or "-"
            print(f"[relay] frame {frame_id}: {dt:.1f}s {err or labels}", flush=True)
            if log_dir:
                with lock:
                    counter["n"] += 1
                    n = counter["n"]
                stem = log_dir / f"{time.strftime('%H%M%S')}_{n:05d}_f{frame_id}"
                stem.with_suffix(".jpg").write_bytes(jpeg)
                stem.with_suffix(".json").write_text(json.dumps(
                    {"frameId": frame_id, "seconds": dt, "detections": dets, "error": err, "raw": raw}, indent=1))

        def log_message(self, *args):  # quiet default access log
            pass

    return Handler


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=8787)
    ap.add_argument("--log-dir", default=str(pathlib.Path.home() / "forge-relay-log"),
                    help="save each frame + answer here (review / training data)")
    ap.add_argument("--no-log", action="store_true")
    ap.add_argument("--max-per-minute", type=int, default=38, help="upstream API calls per minute (0 = no limit)")
    a = ap.parse_args()

    detector = NvidiaDetector(nvidia_key())
    log_dir = None if a.no_log else pathlib.Path(a.log_dir).expanduser()
    if log_dir:
        log_dir.mkdir(parents=True, exist_ok=True)
    server = ThreadingHTTPServer((a.host, a.port), make_handler(detector, log_dir, RateLimiter(a.max_per_minute)))
    print(f"[relay] {detector.model} on http://{a.host}:{a.port}  (log: {log_dir or 'off'})", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
