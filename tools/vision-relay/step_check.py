#!/usr/bin/env python3
"""Automatic step-completion detection for the Forge XR relay (laptop side).

relay.py hands every /detect answer to StepJudge.observe(state, frame_id, jpeg, detections). The Quest's X-State
("driver,step=0,complete=false") says which step is running. For that step:

  1. Geometry candidate: the step's two parts touch / overlap (any direction: the camera looks down at the table, so
     "above" in the image means nothing), the rim sits inside the tire, or one part vanished into the other right
     where the pair was last seen together. The candidate needs `min_hits` of the last `window` answers over at least
     `hold_seconds`.
  2. AI confirmation: a yes/no question to the NVIDIA VLM about a crop of the pair. It runs in a background thread
     (never blocks /detect), at most `vlm_max_calls` per step, `vlm_min_gap_seconds` apart, through a RateLimiter
     (and its 429 cooldown).
  3. Fallback when the VLM is unavailable (no key, disabled, throttled, failing, budget spent): geometry alone after
     `fallback_hold_seconds` with `fallback_ratio` of answers agreeing, and (optionally) only if the pair was seen
     apart earlier in this step, so pieces that start out lying together don't count.

Once confirmed, every /detect answer for the same X-State carries
  "stepComplete": {"state", "flow", "step", "anchorPart", "parts", "box", "frameId", "via", "advance", "final"}
until the state changes (repeated so a lost response doesn't lose it; the Quest dedupes by state).

Tuning: step_config.json next to this file, re-read when it changes (no restart). Rules: STEP_RULES below.
"""
import base64, collections, io, json, pathlib, threading, time

HERE = pathlib.Path(__file__).resolve().parent
CONFIG_PATH = HERE / "step_config.json"

DEFAULTS = {
    "enabled": True,
    "expand": 0.3,                 # "touching": each box grown by this fraction of its larger side, per edge
    "inside_margin": 0.15,         # "inside": rim centre within the tire box grown by this fraction
    "window": 5,                   # candidate: min_hits of the last `window` answers ...
    "min_hits": 3,
    "hold_seconds": 1.0,           # ... for at least this long
    "merge_seconds": 3.0,          # a part vanishing this soon after the pair touched, near that spot, still counts
    "vlm": True,
    "vlm_max_calls": 3,            # per step
    "vlm_min_gap_seconds": 4.0,
    "vlm_timeout_seconds": 20.0,
    "vlm_down_seconds": 30.0,      # after a VLM error (not a "no"), treat it as unavailable this long
    "fallback_hold_seconds": 3.0,
    "fallback_ratio": 0.8,
    "fallback_require_apart": True,
    "fallback_after_vlm_no": False,  # geometry-only may override an earlier VLM "no" in this step
    "crop_pad": 1.0,               # crop = pair box grown by this many box sizes per side
    "crop_min": 0.12,              # minimum crop side (fraction of the frame)
    "crop_size": 512,              # crop upscaled to this longest side for the VLM
}

STEP_COUNTS = {"driver": 3, "wheel": 4}

# (flow, step index) -> rule. mode "touch": a and b touch/overlap; "inside": a's centre inside b.
# anchor = part the Quest puts the confetti on. alias: use another step's rule and complete both steps at once.
STEP_RULES = {
    ("driver", 0): {"a": "torso", "b": "legs", "mode": "touch", "anchor": "torso",
                    "question": "Is the blue LEGO minifigure torso attached to the tan legs, forming one connected "
                                "minifigure body?"},
    ("driver", 1): {"a": "head", "b": "torso", "mode": "touch", "anchor": "head",
                    "question": "Is the yellow LEGO minifigure head attached on top of the blue torso?"},
    ("driver", 2): {"a": "hairpiece", "b": "head", "mode": "touch", "anchor": "hairpiece",
                    "question": "Is the orange LEGO hair piece attached on top of the yellow minifigure head?"},
    # Wheel step 0 (pick up tire and rim) is manual, but if the rim is already in the tire, steps 0 and 1 are done.
    ("wheel", 0): {"alias": ("wheel", 1)},
    ("wheel", 1): {"a": "rim", "b": "tire", "mode": "inside", "anchor": "tire",
                   "question": "Is the grey or white LEGO wheel hub pressed inside the black rubber tire, forming "
                               "one wheel?"},
    ("wheel", 2): {"a": "tire", "b": "axle-pin", "mode": "touch", "anchor": "tire",
                   "question": "Is a black LEGO wheel mounted on the yellow axle pin on the side of the LEGO car?"},
    # ("wheel", 3): spin check, manual Done only.
}


def parse_state(state):
    """'driver,step=0,complete=false' -> ('driver', 0, False); None if missing/garbled."""
    if not state:
        return None
    head, *rest = [s.strip() for s in state.split(",")]
    fields = dict(f.split("=", 1) for f in rest if "=" in f)
    try:
        step = int(fields.get("step", ""))
    except ValueError:
        return None
    if head not in STEP_COUNTS:
        return None
    return head, step, fields.get("complete") == "true"


def rule_for(flow, step):
    """(rule, advance) for a step, following an alias; (None, 0) if the step is manual."""
    r = STEP_RULES.get((flow, step))
    if r is None:
        return None, 0
    if "alias" in r:
        target = r["alias"]
        return STEP_RULES[target], target[1] - step + 1
    return r, 1


# ---------------------------------------------------------------- box geometry (normalised x0, y0, x1, y1)

def box_of(d):
    return (float(d["x0"]), float(d["y0"]), float(d["x1"]), float(d["y1"]))


def grow(b, f):
    m = f * max(b[2] - b[0], b[3] - b[1])
    return (b[0] - m, b[1] - m, b[2] + m, b[3] + m)


def intersects(a, b):
    return a[0] <= b[2] and b[0] <= a[2] and a[1] <= b[3] and b[1] <= a[3]


def touching(a, b, f):
    return intersects(grow(a, f), grow(b, f))


def centre(b):
    return ((b[0] + b[2]) / 2, (b[1] + b[3]) / 2)


def contains_point(b, p):
    return b[0] <= p[0] <= b[2] and b[1] <= p[1] <= b[3]


def union(a, b):
    return (min(a[0], b[0]), min(a[1], b[1]), max(a[2], b[2]), max(a[3], b[3]))


def best_by_part(dets):
    out = {}
    for d in dets or []:
        p = d.get("partId")
        if p and (p not in out or d.get("confidence", 0) > out[p].get("confidence", 0)):
            out[p] = d
    return out


def evaluate(rule, dets, cfg, memory, t):
    """One answer against a rule -> (hit, apart, box). `memory` keeps the last time/place the pair was together
    (for the vanish-into-each-other case). apart = both parts seen and clearly separate."""
    parts = best_by_part(dets)
    a, b = parts.get(rule["a"]), parts.get(rule["b"])
    if a and b:
        ba, bb = box_of(a), box_of(b)
        if rule["mode"] == "inside":
            hit = contains_point(grow(bb, cfg["inside_margin"]), centre(ba))
        else:
            hit = touching(ba, bb, cfg["expand"])
        box = union(ba, bb)
        if hit:
            memory["pair_box"], memory["pair_t"] = box, t
        return hit, not touching(ba, bb, cfg["expand"]), box
    # One part hidden by the other (assembled pieces are often reported as one): still a hit if the remaining part
    # sits where the pair was just seen together.
    one = a or b
    pb, pt = memory.get("pair_box"), memory.get("pair_t", -1e9)
    if one and pb and t - pt <= cfg["merge_seconds"] and contains_point(grow(pb, cfg["expand"]), centre(box_of(one))):
        return True, False, pb
    return False, False, None


# ---------------------------------------------------------------- VLM yes/no

def crop_jpeg(jpeg, box, cfg):
    """JPEG crop of the frame around a normalised box (padded, min size, upscaled)."""
    from PIL import Image
    im = Image.open(io.BytesIO(jpeg)).convert("RGB")
    w, h = im.size
    cx, cy = centre(box)
    side = max(box[2] - box[0], box[3] - box[1]) * (1 + 2 * cfg["crop_pad"])
    side = max(side, cfg["crop_min"])
    x0, x1 = max(0.0, cx - side / 2), min(1.0, cx + side / 2)
    y0, y1 = max(0.0, cy - side / 2), min(1.0, cy + side / 2)
    c = im.crop((round(x0 * w), round(y0 * h), max(round(x1 * w), round(x0 * w) + 1), max(round(y1 * h), round(y0 * h) + 1)))
    s = cfg["crop_size"] / max(c.size)
    if s > 1:
        c = c.resize((round(c.width * s), round(c.height * s)), Image.BICUBIC)
    buf = io.BytesIO()
    c.save(buf, "JPEG", quality=90)
    return buf.getvalue()


class NvidiaYesNo:
    """Asks the hosted VLM a yes/no question about an image. ask() -> True/False, raises on HTTP errors
    (the message contains '429' when rate limited)."""

    def __init__(self, key, url, model, timeout=20.0):
        self.key, self.url, self.model, self.timeout = key, url, model, timeout
        self._local = threading.local()

    def ask(self, jpeg, question):
        import requests
        body = {
            "model": self.model, "max_tokens": 8, "temperature": 0,
            "chat_template_kwargs": {"enable_thinking": False},
            "messages": [{"role": "user", "content": [
                {"type": "text", "text": question + " Look closely at the LEGO pieces in the picture. "
                                                    "Answer with only one word: yes or no."},
                {"type": "image_url", "image_url": {"url": "data:image/jpeg;base64," + base64.b64encode(jpeg).decode()}},
            ]}],
        }
        if not hasattr(self._local, "session"):
            self._local.session = requests.Session()
        r = self._local.session.post(self.url, json=body, timeout=self.timeout,
                                     headers={"Authorization": f"Bearer {self.key}", "Accept": "application/json"})
        if r.status_code != 200:
            raise RuntimeError(f"NVIDIA HTTP {r.status_code}: {r.text[:200]}")
        content = (r.json()["choices"][0]["message"].get("content") or "").strip().lower()
        return content.lstrip(" \"'*.").startswith("yes")


# ---------------------------------------------------------------- judge

class StepJudge:
    """One global judge (one Quest per relay). Thread-safe; relay handler threads call observe()/current()."""

    def __init__(self, vlm=None, limiter=None, config_path=CONFIG_PATH, clock=time.time, async_vlm=True,
                 log=print, crop_dir=None):
        self.vlm, self.limiter, self.config_path = vlm, limiter, pathlib.Path(config_path) if config_path else None
        self.clock, self.async_vlm, self.log = clock, async_vlm, log
        self.crop_dir = pathlib.Path(crop_dir) if crop_dir else None
        self.lock = threading.RLock()
        self._cfg, self._cfg_mtime = dict(DEFAULTS), None
        self._reset(None)

    # -- config
    def cfg(self):
        p = self.config_path
        try:
            m = p.stat().st_mtime if p and p.exists() else None
        except OSError:
            m = None
        if m != self._cfg_mtime:
            self._cfg_mtime = m
            cfg = dict(DEFAULTS)
            if m is not None:
                try:
                    data = json.loads(p.read_text())
                    cfg.update({k: v for k, v in data.items() if k in DEFAULTS})
                except (OSError, ValueError) as e:
                    self.log(f"[step] bad {p.name}: {e}; using defaults")
            self._cfg = cfg
        return self._cfg

    # -- state
    def _reset(self, state):
        self.state = state
        self.gen = getattr(self, "gen", 0) + 1     # VLM answers for an older generation are ignored
        self.hist = collections.deque(maxlen=200)  # (t, hit)
        self.memory = {}
        self.seen_apart = False
        self.cand_since = None
        self.last_cand = None                      # (frame_id, jpeg, box)
        self.vlm_calls, self.vlm_last, self.vlm_busy_since = 0, -1e9, None
        self.vlm_answers = []                      # (t, bool)
        self.vlm_down_until = getattr(self, "vlm_down_until", 0.0)
        self.vlm_throttled = False
        self.confirmed = None
        self.status_text = "idle"

    def reset(self):
        with self.lock:
            self._reset(None)

    def current(self, state):
        """stepComplete payload if this exact state is confirmed (for answers that carry no new evidence)."""
        with self.lock:
            return dict(self.confirmed) if self.confirmed and state == self.state else None

    def status(self):
        with self.lock:
            return {"state": self.state, "status": self.status_text, "confirmed": self.confirmed,
                    "seenApart": self.seen_apart, "vlmCalls": self.vlm_calls,
                    "vlmAnswers": [a for _, a in self.vlm_answers], "hits": sum(h for _, h in list(self.hist)[-10:]),
                    "answers": min(10, len(self.hist))}

    def observe(self, state, frame_id, jpeg, dets, t=None):
        """Feed one detection answer; returns the stepComplete payload (dict) or None."""
        t = self.clock() if t is None else t
        cfg = self.cfg()
        with self.lock:
            if state != self.state:
                self._reset(state)
            if not cfg["enabled"]:
                self.status_text = "disabled"
                return None
            if self.confirmed:
                return dict(self.confirmed)
            parsed = parse_state(state)
            if parsed is None or parsed[2]:
                self.status_text = "no step" if parsed is None else "walkthrough complete"
                return None
            flow, step, _ = parsed
            rule, advance = rule_for(flow, step)
            if rule is None:
                self.status_text = f"{flow} {step + 1}: manual step"
                return None

            hit, apart, box = evaluate(rule, dets, cfg, self.memory, t)
            self.hist.append((t, hit))
            self.seen_apart |= apart
            recent = list(self.hist)[-cfg["window"]:]
            if hit and sum(h for _, h in recent) >= cfg["min_hits"]:
                if self.cand_since is None:
                    # The streak started at the oldest hit of the window that made it a candidate.
                    self.cand_since = next(tt for tt, h in recent if h)
                self.last_cand = (frame_id, jpeg, box)
            elif sum(h for _, h in recent) < cfg["min_hits"]:
                self.cand_since = None
            held = 0.0 if self.cand_since is None else t - self.cand_since

            if self.cand_since is None:
                self.status_text = f"{flow} {step + 1}: waiting ({'hit' if hit else 'no'}; apart={self.seen_apart})"
                return None
            self.status_text = f"{flow} {step + 1}: candidate {held:.1f}s"

            # VLM confirmation
            busy = self.vlm_busy_since is not None and t - self.vlm_busy_since < cfg["vlm_timeout_seconds"] + 5
            if self.vlm_busy_since is not None and not busy:
                self.vlm_busy_since = None  # a lost call: give up on it
            if held >= cfg["hold_seconds"] and not busy and self._vlm_usable(cfg, t) \
                    and t - self.vlm_last >= cfg["vlm_min_gap_seconds"] and self.last_cand[1]:
                if self.limiter is None or self.limiter.try_acquire():
                    self.vlm_throttled = False
                    self._start_vlm(rule, advance, flow, step, cfg, t)
                    if self.confirmed:  # inline (test) mode answered already
                        return dict(self.confirmed)
                    busy = self.vlm_busy_since is not None
                else:
                    self.vlm_throttled = True
            if busy:
                self.status_text += " | asking VLM"
                return None

            # Geometry-only fallback
            if held >= cfg["fallback_hold_seconds"] and self._fallback_allowed(cfg, t):
                window = [h for tt, h in self.hist if tt >= t - cfg["fallback_hold_seconds"]]
                ratio = sum(window) / max(1, len(window))
                if ratio >= cfg["fallback_ratio"] and (self.seen_apart or not cfg["fallback_require_apart"]):
                    self._confirm(rule, advance, flow, step, "geometry")
                    return dict(self.confirmed)
                self.status_text += f" | fallback waits (ratio {ratio:.2f}, apart={self.seen_apart})"
            return None

    def _vlm_usable(self, cfg, t):
        return (self.vlm is not None and cfg["vlm"] and self.vlm_calls < cfg["vlm_max_calls"]
                and t >= self.vlm_down_until)

    def _fallback_allowed(self, cfg, t):
        if any(not a for _, a in self.vlm_answers) and not cfg["fallback_after_vlm_no"]:
            return False
        return not self._vlm_usable(cfg, t) or self.vlm_throttled

    def _confirm(self, rule, advance, flow, step, via):
        frame_id, _, box = self.last_cand
        final_step = step + advance - 1
        self.confirmed = {
            "state": self.state, "flow": flow, "step": step, "anchorPart": rule["anchor"],
            "parts": [rule["anchor"]] + [p for p in (rule["a"], rule["b"]) if p != rule["anchor"]],
            "box": [round(v, 4) for v in box] if box else [], "frameId": _int(frame_id), "via": via,
            "advance": advance, "final": final_step == STEP_COUNTS[flow] - 1,
        }
        self.status_text = f"{flow} {step + 1}: COMPLETE via {via}"
        self.log(f"[step] {self.state}: complete via {via} (advance {advance}, anchor {rule['anchor']})")

    def _start_vlm(self, rule, advance, flow, step, cfg, t):
        self.vlm_calls += 1
        self.vlm_last = t
        self.vlm_busy_since = t
        gen = self.gen
        frame_id, jpeg, box = self.last_cand

        def run():
            try:
                crop = crop_jpeg(jpeg, box, cfg)
                if self.crop_dir:
                    self.crop_dir.mkdir(parents=True, exist_ok=True)
                    (self.crop_dir / f"{flow}{step}_f{frame_id}_{self.vlm_calls}.jpg").write_bytes(crop)
                answer, err = self.vlm.ask(crop, rule["question"]), None
            except Exception as e:  # noqa: BLE001 - never break serving
                answer, err = None, str(e)
            with self.lock:
                now = self.clock()
                if gen != self.gen:
                    return  # the step changed meanwhile
                self.vlm_busy_since = None
                if err:
                    self.log(f"[step] VLM error: {err[:120]}")
                    if "429" in err:
                        self.vlm_calls -= 1  # a refused call doesn't spend this step's budget
                        if self.limiter is not None and hasattr(self.limiter, "note_rate_limited"):
                            self.limiter.note_rate_limited()
                        self.vlm_down_until = now + getattr(self.limiter, "cooldown_seconds", cfg["vlm_down_seconds"])
                    else:
                        self.vlm_down_until = now + cfg["vlm_down_seconds"]
                    return
                self.vlm_answers.append((now, answer))
                self.log(f"[step] VLM {self.state} f{frame_id}: {'yes' if answer else 'no'}")
                if answer and not self.confirmed:
                    self._confirm(rule, advance, flow, step, "vlm")

        if self.async_vlm:
            threading.Thread(target=run, daemon=True).start()
        else:
            self.lock.release()  # run() takes the lock itself; keep the RLock count right for inline tests
            try:
                run()
            finally:
                self.lock.acquire()


def _int(v):
    try:
        return int(v)
    except (TypeError, ValueError):
        return -1
