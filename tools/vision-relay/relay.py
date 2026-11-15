#!/usr/bin/env python3
"""Forge XR vision relay: the Quest POSTs a camera frame, the relay asks a hosted vision model where the LEGO
parts are, and returns normalised boxes. The API key stays on this laptop.

  python3 relay.py                 # NVIDIA Nemotron VL, port 8787, key from NVIDIA_API_KEY or ~/nvapi.env
  python3 relay.py --port 8787 --log-dir ~/forge-relay-log --no-log

Quest side: Forge.CloudVisionClassifier POSTs image/jpeg to http://<this-laptop>:8787/detect
(over USB: `adb reverse tcp:8787 tcp:8787`, then the default http://127.0.0.1:8787 works).

Response: {"detections": [{"partId", "x0", "y0", "x1", "y1", "confidence"}], "seconds", "model", "error"}
with coordinates normalised 0-1 relative to the posted image (x from the left, y from the top), plus an optional
"stepComplete" object once step_check.StepJudge has confirmed the step in X-State (see step_check.py).
GET /health returns {"ok": true, "model": ...}. GET /step: step judge status. POST /step/reset clears it.
"""
import argparse, base64, collections, html, io, json, os, pathlib, statistics, sys, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import requests
from PIL import Image, ImageDraw, ImageFont

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))  # python -I does not put the script's folder on sys.path
import step_check  # noqa: E402  (automatic step completion: geometry + VLM yes/no)
CONFIG_PATH = HERE / "quest_config.json"  # live Quest tuning, served at /config (Forge.RemoteConfig polls it)

# Part-selection policy (laptop side). The Quest reports X-State "driver,step=N,complete=bool".
DRIVER_PARTS = ["legs", "torso", "head", "hairpiece"]
WHEEL_PARTS = ["tire", "rim", "axle-pin"]
PROBE_ALL_EVERY = 4  # at step 1 / completion, every Nth request asks for all parts so auto-select can switch


def choose_parts(state_header, parts_header, seq):
    """Parts to ask the model for: from the walkthrough state if sent, else the Quest's X-Parts, else all."""
    if state_header:
        fields = dict(f.split("=", 1) for f in state_header.split(",")[1:] if "=" in f)
        flow = state_header.split(",")[0].strip()
        at_start = fields.get("step", "1") == "0" or fields.get("complete") == "true"
        if at_start and seq % PROBE_ALL_EVERY == 0:
            return None
        return WHEEL_PARTS if flow == "wheel" else DRIVER_PARTS
    wanted = [p.strip() for p in parts_header.split(",") if p.strip()]
    return wanted or None


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

def build_prompt(parts):
    """Prompt for a subset of PARTS (the Quest sends the active walkthrough's parts in X-Parts)."""
    return (
        "Find these LEGO parts anywhere in the image, whether lying on the table or held in someone's hand "
        "(possibly already attached to other parts):\n"
        + "\n".join(f"- {k}: {PARTS[k]}" for k in parts)
        + "\nReport every part you can see, including small or distant ones, but do not guess. Each part appears at most "
          "once; anything else in the scene is not one of these parts. Reply with only JSON: "
          '[{"label": one of the ids above, "x0": left, "y0": top, "x1": right, "y1": bottom}] '
          "with coordinates normalized 0-1000 (x across the width from the left, y down the height from the top). "
          "Reply [] if none are visible."
    )


PROMPT = build_prompt(PARTS)


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

    def detect(self, jpeg_bytes, parts=None):
        parts = [p for p in (parts or PARTS) if p in PARTS] or list(PARTS)
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
                    {"type": "text", "text": build_prompt(parts)},
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
            if not isinstance(d, dict) or d.get("label") not in parts:
                continue
            try:
                x0, y0, x1, y1 = (float(d[k]) / 1000.0 for k in ("x0", "y0", "x1", "y1"))
            except (KeyError, TypeError, ValueError):
                continue
            clamp = lambda v: min(1.0, max(0.0, v))
            out.append({"partId": d["label"], "x0": clamp(min(x0, x1)), "y0": clamp(min(y0, y1)),
                        "x1": clamp(max(x0, x1)), "y1": clamp(max(y0, y1)), "confidence": 0.8})
        return out, content


EDGE_MARGIN = 0.12


MAX_BOX_SIDE = 0.45


def too_big(xyxy, side=MAX_BOX_SIDE):
    """A LEGO piece is never half the frame, even held close: such boxes (seen in a bright room: one "torso" over half
    the room) are false."""
    return xyxy[2] - xyxy[0] > side or xyxy[3] - xyxy[1] > side


def near_edge(xyxy, margin=EDGE_MARGIN):
    """Box centre in the outer margin of the frame. The pieces sit mid-view while building; the false detections
    seen in practice (bags, shelves, the laptop) were at the frame edges, and the Quest keeps a box up for several
    missed frames, so one such hit leaves a stray box in the room."""
    cx, cy = (xyxy[0] + xyxy[2]) / 2, (xyxy[1] + xyxy[3]) / 2
    return not (margin <= cx <= 1 - margin and margin <= cy <= 1 - margin)


def iou(a, b):
    ix = max(0.0, min(a[2], b[2]) - max(a[0], b[0]))
    iy = max(0.0, min(a[3], b[3]) - max(a[1], b[1]))
    inter = ix * iy
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / union if union > 0 else 0.0


def drop_hair_on_body(best, thresh=0.5):
    """The weakly learned hairpiece class also fires on the torso/legs (same box, seen in the headset footage), which
    stacks a Hairpiece box on the body while the real hair goes unboxed. Hair never sits directly on the torso or
    legs, so a hairpiece box that coincides with one is the body: drop it. Hair on the head is left alone (step 3)."""
    hair = best.get("hairpiece")
    if hair and any(p in best and iou(hair[1], best[p][1]) > thresh for p in ("torso", "legs")):
        del best["hairpiece"]
    return best


def shown_to_quest(state, dets):
    """Boxes the headset draws (the step judge still gets every detection). Before the torso is on the legs (driver
    step 1) a "legs" box on the same spot as the "torso" box is the loose torso mislabelled (headset footage: a Legs
    box on the torso while the real legs went unboxed), so it is hidden; once joined, both labels are right."""
    if not state.startswith("driver,step=0"):
        return dets
    by = {d["partId"]: d for d in dets}
    t, l = by.get("torso"), by.get("legs")
    if t and l and iou([t["x0"], t["y0"], t["x1"], t["y1"]], [l["x0"], l["y0"], l["x1"], l["y1"]]) > 0.5:
        return [d for d in dets if d is not l]
    return dets


class LocalYoloDetector:
    """Fine-tuned YOLO (tools/local-detector) on the laptop: ~50-100 ms CPU, no API, no rate limit.
    Needs the detector venv (ultralytics): run the relay with ~/.venvs/forge-detector/bin/python."""

    def __init__(self, weights, conf=0.3, imgsz=640):
        """weights: one .pt or a comma list (ensemble: every model runs, best box per part wins). The epoch-12
        model is strong in the dim room it was tuned on; the epoch-5 one generalises better to bright spaces."""
        from ultralytics import YOLO
        paths = [w for w in str(weights).split(",") if w]
        self.yolos = [YOLO(w) for w in paths]
        self.conf, self.imgsz = conf, imgsz
        # Weakly learned classes (few/odd pseudo-labels) score low; accept them at a lower cutoff.
        self.part_conf = {"hairpiece": 0.15, "torso": 0.2, "legs": 0.2}
        self.model = "local:" + "+".join(pathlib.Path(w).name for w in paths)
        self._lock = threading.Lock()  # ultralytics predict is not thread-safe

    def detect(self, jpeg_bytes, parts=None):
        parts = [p for p in (parts or PARTS) if p in PARTS] or list(PARTS)
        im = Image.open(io.BytesIO(jpeg_bytes)).convert("RGB")
        floor = min([self.conf] + list(self.part_conf.values()))
        best = {}
        for yolo in self.yolos:
            with self._lock:
                r = yolo.predict(im, conf=floor, imgsz=self.imgsz, verbose=False)[0]
            for b in r.boxes:
                part = yolo.names[int(b.cls.item())]
                c = float(b.conf.item())
                if c < self.part_conf.get(part, self.conf) or near_edge(b.xyxyn[0].tolist()) \
                        or too_big(b.xyxyn[0].tolist()):
                    continue
                if part in parts and (part not in best or c > best[part][0]):
                    best[part] = (c, b.xyxyn[0].tolist())
        drop_hair_on_body(best)
        out = [{"partId": p, "x0": x0, "y0": y0, "x1": x1, "y1": y1,
                # The relay's threshold already filtered; report as trusted like the cloud answers (Quest drops < 0.5).
                "confidence": 0.8, "score": round(c, 3)} for p, (c, (x0, y0, x1, y1)) in best.items()]
        return out, json.dumps([[p, round(c, 3)] for p, (c, _) in best.items()])


class HybridDetector:
    """Local YOLO every request + a background Nemotron check every few seconds, for unseen spaces.

    Nemotron (never trained on our scene) finds parts the local model misses. Its boxes come from an older frame, so
    each one is relocated in the current frame by template matching (OpenCV) near the old box before it is added;
    a part is kept for at most `ttl` seconds and only while the match stays good. If NVIDIA is throttled or down,
    this is just the local detector."""

    def __init__(self, local, nvidia, limiter, every=3.0, ttl=4.0, min_match=0.55):
        import cv2, numpy  # noqa: F401  (ultralytics venv has both)
        self.local, self.nvidia, self.limiter = local, nvidia, limiter
        self.every, self.ttl, self.min_match = every, ttl, min_match
        self.model = local.model + "+" + nvidia.model.split("/")[-1]
        self._lock = threading.Lock()
        self._busy = False
        self._last_call = 0.0
        self.extra = {}  # part -> {"box": [x0,y0,x1,y1], "tmpl": gray crop, "t": time found}

    @staticmethod
    def _gray(jpeg_bytes):
        import cv2, numpy as np
        return cv2.imdecode(np.frombuffer(jpeg_bytes, np.uint8), cv2.IMREAD_GRAYSCALE)

    def _cloud(self, jpeg_bytes, parts):
        try:
            dets, _ = self.nvidia.detect(jpeg_bytes, parts)
        except Exception as e:  # noqa: BLE001
            if "429" in str(e):
                self.limiter.note_rate_limited()
            dets = []
        g = self._gray(jpeg_bytes)
        h, w = g.shape
        now = time.time()
        with self._lock:
            for d in dets:
                x0, y0, x1, y1 = int(d["x0"] * w), int(d["y0"] * h), int(d["x1"] * w), int(d["y1"] * h)
                if x1 - x0 < 6 or y1 - y0 < 6:
                    continue
                self.extra[d["partId"]] = {"box": [d["x0"], d["y0"], d["x1"], d["y1"]],
                                           "tmpl": g[y0:y1, x0:x1].copy(), "t": now}
            self._busy = False

    def _relocate(self, g, e):
        import cv2
        h, w = g.shape
        x0, y0, x1, y1 = e["box"]
        bw, bh = (x1 - x0) * w, (y1 - y0) * h
        cx, cy = (x0 + x1) / 2 * w, (y0 + y1) / 2 * h
        sx0, sy0 = int(max(0, cx - 2 * bw)), int(max(0, cy - 2 * bh))
        sx1, sy1 = int(min(w, cx + 2 * bw)), int(min(h, cy + 2 * bh))
        win, t = g[sy0:sy1, sx0:sx1], e["tmpl"]
        if win.shape[0] < t.shape[0] or win.shape[1] < t.shape[1]:
            return None
        res = cv2.matchTemplate(win, t, cv2.TM_CCOEFF_NORMED)
        _, score, _, (mx, my) = cv2.minMaxLoc(res)
        if score < self.min_match:
            return None
        nx0, ny0 = (sx0 + mx) / w, (sy0 + my) / h
        return [nx0, ny0, nx0 + t.shape[1] / w, ny0 + t.shape[0] / h], score

    def detect(self, jpeg_bytes, parts=None):
        out, raw = self.local.detect(jpeg_bytes, parts)
        now = time.time()
        with self._lock:
            start = (not self._busy and now - self._last_call >= self.every and self.limiter.try_acquire())
            if start:
                self._busy, self._last_call = True, now
        if start:
            threading.Thread(target=self._cloud, args=(jpeg_bytes, parts), daemon=True).start()
        have = {d["partId"] for d in out}
        wanted = set(p for p in (parts or PARTS) if p in PARTS) or set(PARTS)
        with self._lock:
            extra = {p: e for p, e in self.extra.items() if now - e["t"] <= self.ttl}
            self.extra = extra
        if extra.keys() - have:
            g = self._gray(jpeg_bytes)
            for p, e in extra.items():
                if p in have or p not in wanted:
                    continue
                found = self._relocate(g, e)
                if found:
                    (bx0, by0, bx1, by1), s = found
                    e["box"] = [bx0, by0, bx1, by1]  # follow it frame to frame
                    out.append({"partId": p, "x0": bx0, "y0": by0, "x1": bx1, "y1": by1,
                                "confidence": 0.8, "score": round(s, 3), "via": "nemotron"})
        return out, raw


class RateLimiter:
    """Sliding-window limit on upstream API calls, plus a cooldown after the API answers 429 (its real limit is
    stricter than the advertised 40/min)."""

    def __init__(self, per_minute, cooldown_seconds=20.0):
        self.per_minute = per_minute
        self.cooldown_seconds = cooldown_seconds
        self.calls = []
        self.blocked_until = 0.0
        self.lock = threading.Lock()

    def note_rate_limited(self):
        with self.lock:
            self.blocked_until = time.time() + self.cooldown_seconds

    def try_acquire(self):
        now = time.time()
        with self.lock:
            if now < self.blocked_until:
                return False
            self.calls = [t for t in self.calls if now - t < 60.0]
            if self.per_minute > 0 and len(self.calls) >= self.per_minute:
                return False
            self.calls.append(now)
            return True


COLORS = {"legs": "#d2b48c", "torso": "#1e9bff", "head": "#ffd400", "hairpiece": "#ff8c00",
          "tire": "#ff3030", "rim": "#00e0e0", "axle-pin": "#ff00ff"}


def annotate(jpeg, dets, caption):
    """The frame with each detection's box and label drawn on, plus a caption bar (for the review page)."""
    im = Image.open(io.BytesIO(jpeg)).convert("RGB")
    d = ImageDraw.Draw(im)
    try:
        font = ImageFont.truetype("DejaVuSans-Bold.ttf", max(14, im.width // 40))
    except OSError:
        font = ImageFont.load_default()
    w, h = im.size
    for x in dets:
        c = COLORS.get(x["partId"], "#ffffff")
        box = (x["x0"] * w, x["y0"] * h, x["x1"] * w, x["y1"] * h)
        d.rectangle(box, outline=c, width=max(3, w // 200))
        d.text((box[0] + 3, max(0, box[1] - font.size - 3)), x["partId"], fill=c, font=font)
    d.rectangle((0, h - font.size - 10, w, h), fill="#000000")
    d.text((6, h - font.size - 6), caption, fill="#ffffff", font=font)
    buf = io.BytesIO()
    im.save(buf, "JPEG", quality=80)
    return buf.getvalue()


REVIEW_HTML = """<!doctype html><html><head><meta charset=utf-8><title>Forge relay review</title>
<meta http-equiv=refresh content=2>
<style>body{{background:#111;color:#ddd;font:14px sans-serif;margin:12px}}.g{{display:grid;
grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:10px}}figure{{margin:0}}img{{width:100%;border-radius:6px}}
figcaption{{font-size:12px;color:#aaa;padding:2px}}.s{{margin-bottom:10px;color:#fff}}</style></head><body>
<div class=s>{stats}</div><div class=g>{items}</div></body></html>"""


def try_nvidia_key():
    """The NVIDIA key, or None (nvidia_key() exits when there is none)."""
    try:
        return nvidia_key()
    except SystemExit:
        return None


def make_handler(detector, log_dir, limiter, log_every=1, judge=None):
    lock = threading.Lock()
    counter = {"n": 0}
    recent = collections.deque(maxlen=48)  # newest last: dicts with n, t, frame, seconds, parts, dets, err
    images = {}                            # n -> annotated jpeg (only for entries in `recent`)

    def stats_text():
        now = time.time()
        rows = [r for r in recent if now - r["t"] < 300]
        if not rows:
            return "no answers in the last 5 min"
        lat = [r["seconds"] for r in rows if not r["err"]]
        per = collections.Counter(x["partId"] for r in rows for x in r["dets"])
        hit = sum(1 for r in rows if r["dets"])
        errs = sum(1 for r in rows if r["err"])
        return (f"last 5 min: {len(rows)} answers, {hit} with detections, {errs} errors, "
                f"median {statistics.median(lat) if lat else 0:.1f}s | per part: "
                + ", ".join(f"{k} {v}" for k, v in per.most_common()))

    class Handler(BaseHTTPRequestHandler):
        def _send(self, code, obj):
            data = json.dumps(obj).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def _raw(self, code, data, ctype):
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            if self.path.startswith("/health"):
                self._send(200, {"ok": True, "model": detector.model})
            elif self.path.startswith("/review"):
                with lock:
                    rows = list(recent)[::-1]
                items = "".join(
                    f"<figure><img src='/img/{r['n']}.jpg'><figcaption>#{r['n']} frame {html.escape(str(r['frame']))} "
                    f"{r['seconds']:.1f}s asked: {html.escape(r['parts'])} &rarr; "
                    f"{html.escape(r['err'] or (', '.join(x['partId'] for x in r['dets']) or 'nothing'))}</figcaption></figure>"
                    for r in rows)
                self._raw(200, REVIEW_HTML.format(stats=html.escape(stats_text()), items=items).encode(), "text/html; charset=utf-8")
            elif self.path.startswith("/img/"):
                try:
                    n = int(self.path[5:].split(".")[0])
                except ValueError:
                    n = -1
                with lock:
                    data = images.get(n)
                if data:
                    self._raw(200, data, "image/jpeg")
                else:
                    self._send(404, {"error": "gone"})
            elif self.path.startswith("/config"):
                try:
                    data = CONFIG_PATH.read_bytes() if CONFIG_PATH.exists() else b"{}"
                    json.loads(data)  # serve only valid JSON
                except ValueError:
                    data = b"{}"
                self._raw(200, data, "application/json")
            elif self.path.startswith("/stats"):
                self._send(200, {"summary": stats_text()})
            elif self.path.startswith("/step"):
                self._send(200, judge.status() if judge else {"status": "off"})
            else:
                self._send(404, {"error": "not found"})

        def _step(self, state, frame_id=None, jpeg=None, dets=None):
            """stepComplete payload for this answer (None if not confirmed). Never raises."""
            if judge is None:
                return None
            try:
                if dets is None:
                    return judge.current(state)
                return judge.observe(state, frame_id, jpeg, dets)
            except Exception as e:  # noqa: BLE001 - step detection must never break serving
                print(f"[relay] step judge error: {e}", flush=True)
                return None

        def do_POST(self):
            if self.path.startswith("/step/reset"):
                if judge:
                    judge.reset()
                self._send(200, {"ok": True})
                return
            if self.path.startswith("/step/force"):  # operator/debug: mark the current step complete now
                self._send(200, {"ok": True, "confirmed": judge.force() if judge else None})
                return
            if not self.path.startswith("/detect"):
                self._send(404, {"error": "not found"})
                return
            length = int(self.headers.get("Content-Length", "0"))
            jpeg = self.rfile.read(length)
            frame_id = self.headers.get("X-Frame-Id", "?")
            state = self.headers.get("X-State", "")
            t0 = time.time()
            if not limiter.try_acquire():
                # Over the upstream cap: answer at once with an error (the Quest backs off, boxes are kept).
                resp = {"detections": [], "seconds": 0, "model": detector.model, "error": "rate limited (relay)"}
                step = self._step(state)
                if step:
                    resp["stepComplete"] = step
                self._send(200, resp)
                print(f"[relay] frame {frame_id}: skipped (over {limiter.per_minute}/min or cooling down after a 429)", flush=True)
                return
            try:
                seq = int(self.headers.get("X-Request-Seq", "0"))
            except ValueError:
                seq = 0
            wanted = choose_parts(self.headers.get("X-State", ""), self.headers.get("X-Parts", ""), seq) or []
            try:
                dets, raw = detector.detect(jpeg, wanted or None)
                err = None
            except Exception as e:  # noqa: BLE001 - report to the Quest, keep serving
                dets, raw, err = [], "", str(e)
                if "429" in err:
                    limiter.note_rate_limited()
            dt = time.time() - t0
            step = self._step(state) if err else self._step(state, frame_id, jpeg, dets)
            resp = {"detections": shown_to_quest(state, dets), "seconds": round(dt, 2), "model": detector.model,
                    "error": err}
            if step:
                resp["stepComplete"] = step
            self._send(200, resp)
            judge_text = judge.status()["status"] if judge else ""
            labels = ", ".join(d["partId"] for d in dets) or "-"
            asked = ",".join(wanted) if wanted else "all"
            print(f"[relay] frame {frame_id}: {dt:.1f}s asked={asked} -> {err or labels}"
                  + (f" | {judge_text}" if judge_text else ""), flush=True)
            with lock:
                counter["n"] += 1
                n = counter["n"]
            try:
                ann = annotate(jpeg, dets, f"#{n} f{frame_id} {dt:.1f}s asked={asked} -> {err or labels}"
                               + (f" | {judge_text}" if judge_text else ""))
            except Exception:  # noqa: BLE001 - diagnostics must never break serving
                ann = None
            with lock:
                if len(recent) == recent.maxlen:
                    images.pop(recent[0]["n"], None)
                recent.append({"n": n, "t": time.time(), "frame": frame_id, "seconds": dt, "parts": asked,
                               "dets": dets, "err": err})
                if ann:
                    images[n] = ann
            if log_dir and n % max(1, log_every) == 0:
                stem =log_dir / f"{time.strftime('%H%M%S')}_{n:05d}_f{frame_id}"
                stem.with_suffix(".jpg").write_bytes(jpeg)
                stem.with_suffix(".json").write_text(json.dumps(
                    {"frameId": frame_id, "t": round(t0, 3), "state": state, "seconds": dt, "parts": asked,
                     "detections": dets, "error": err, "raw": raw, "judge": judge_text, "stepComplete": step},
                    indent=1))
                if ann:
                    (log_dir / "annotated").mkdir(exist_ok=True)
                    (log_dir / "annotated" / (stem.name + ".jpg")).write_bytes(ann)

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
    ap.add_argument("--max-per-minute", type=int, default=25, help="upstream API calls per minute (0 = no limit)")
    ap.add_argument("--detector", choices=["nvidia", "local", "hybrid"], default="nvidia")
    ap.add_argument("--weights", default="", help="local: path to the fine-tuned YOLO .pt")
    ap.add_argument("--local-conf", type=float, default=0.3, help="local: min YOLO score")
    ap.add_argument("--hybrid-every", type=float, default=4.0, help="hybrid: seconds between background Nemotron checks")
    ap.add_argument("--log-every", type=int, default=1, help="save every Nth frame (local runs fast; saves disk)")
    ap.add_argument("--no-step", action="store_true", help="turn off automatic step completion (step_check.py)")
    ap.add_argument("--step-vlm-per-minute", type=int, default=6,
                    help="VLM yes/no confirmations per minute (local detector; with --detector nvidia they share its cap)")
    a = ap.parse_args()

    if a.detector in ("local", "hybrid"):
        detector = LocalYoloDetector(a.weights, conf=a.local_conf)
        key = try_nvidia_key()
        if a.detector == "hybrid" and key:
            # Own cap for the background checks (+ 429 cooldown); local answers are never rate limited.
            detector = HybridDetector(detector, NvidiaDetector(key), RateLimiter(int(60 / a.hybrid_every) + 1),
                                      every=a.hybrid_every)
        a.max_per_minute = 0  # relay-level cap off: the local model answers every request
    else:
        key = nvidia_key()
        detector = NvidiaDetector(key)
    log_dir = None if a.no_log else pathlib.Path(a.log_dir).expanduser()
    if log_dir:
        log_dir.mkdir(parents=True, exist_ok=True)
    limiter = RateLimiter(a.max_per_minute)
    judge = None
    if not a.no_step:
        vlm = step_check.NvidiaYesNo(key, NVIDIA_URL, NVIDIA_MODEL) if key else None
        # NVIDIA detector: confirmations share its limiter. Local: their own small cap (+ 429 cooldown).
        vlm_limiter = limiter if a.detector == "nvidia" else RateLimiter(a.step_vlm_per_minute)
        judge = step_check.StepJudge(vlm=vlm, limiter=vlm_limiter,
                                     crop_dir=(log_dir / "step-crops") if log_dir else None,
                                     log=lambda m: print(m, flush=True))
        print(f"[relay] step completion on (VLM confirm: {'yes' if vlm else 'no key, geometry only'})", flush=True)
    server = ThreadingHTTPServer((a.host, a.port),
                                 make_handler(detector, log_dir, limiter, a.log_every, judge))
    print(f"[relay] {detector.model} on http://{a.host}:{a.port}  (log: {log_dir or 'off'})", flush=True)
    print(f"[relay] live review page: http://127.0.0.1:{a.port}/review", flush=True)
    # Auto-reload: restart this process when relay.py or step_check.py changes (edit and it is live; quest_config.json
    # and step_config.json are read per request).
    me = pathlib.Path(__file__).resolve()
    watched = [me, HERE / "step_check.py"]

    def mtimes():
        out = []
        for f in watched:
            try:
                out.append(f.stat().st_mtime)
            except OSError:
                out.append(None)
        return out

    start = mtimes()

    def watch():
        while True:
            time.sleep(1)
            now = mtimes()
            if now != start and all(m is not None for m in now):
                changed = [f.name for f, a0, b0 in zip(watched, start, now) if a0 != b0]
                print(f"[relay] {', '.join(changed)} changed: reloading", flush=True)
                server.shutdown()
                return

    threading.Thread(target=watch, daemon=True).start()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        return
    server.server_close()
    time.sleep(0.5)
    os.execv(sys.executable, [sys.executable, "-I", str(me)] + sys.argv[1:])


if __name__ == "__main__":
    main()
