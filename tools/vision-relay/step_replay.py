#!/usr/bin/env python3
"""Replay saved relay frames (jpg + json pairs, read only) through step_check.StepJudge, laptop only.
Shows when a step would complete, with what evidence, and saves the candidate crops the VLM would see.

  python3 -I tools/vision-relay/step_replay.py FRAMES... [--state driver,step=0,complete=false]
          [--vlm] [--max-vlm 3] [--out /tmp/forge-step-replay] [--fps 5]

FRAMES: .json/.jpg files or globs from a relay log dir. Detections come from each frame's saved .json (no detector
run). State: the json's "state" (logged since step detection was added) or --state for older logs. Time: the json's
"t", else the HHMMSS filename prefix spread at --fps within each second. Without --vlm the VLM is mocked as
unavailable (geometry fallback); --vlm-mock yes|no answers every question with yes/no; --vlm asks NVIDIA for real
(key from relay.nvidia_key(), never printed; capped by --max-vlm). Never writes next to the frames.
"""
import argparse, collections, glob, json, pathlib, sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import step_check  # noqa: E402


class MockVlm:
    def __init__(self, answer):
        self.answer, self.calls = answer, 0

    def ask(self, jpeg, question):
        self.calls += 1
        return self.answer


class CappedVlm:
    def __init__(self, inner, cap):
        self.inner, self.cap, self.calls = inner, cap, 0

    def ask(self, jpeg, question):
        if self.calls >= self.cap:
            raise RuntimeError("replay VLM cap reached")
        self.calls += 1
        return self.inner.ask(jpeg, question)


def frame_time(path, data, index_in_second, fps):
    if isinstance(data.get("t"), (int, float)):
        return float(data["t"])
    stem = path.name[:6]
    try:
        h, m, s = int(stem[:2]), int(stem[2:4]), int(stem[4:6])
    except ValueError:
        return None
    return h * 3600 + m * 60 + s + index_in_second / max(1.0, fps)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("frames", nargs="+")
    ap.add_argument("--state", default="", help="X-State to assume when a frame's json has none")
    ap.add_argument("--vlm", action="store_true", help="ask NVIDIA for real (rate limited, capped)")
    ap.add_argument("--vlm-mock", choices=["yes", "no"], default="")
    ap.add_argument("--max-vlm", type=int, default=3)
    ap.add_argument("--fps", type=float, default=5.0, help="answers per second assumed for 1 s filename times")
    ap.add_argument("--out", default="/tmp/forge-step-replay")
    ap.add_argument("--config", default=str(step_check.CONFIG_PATH))
    ap.add_argument("--quiet", action="store_true", help="only print state changes and completions")
    a = ap.parse_args()

    files = []
    for f in a.frames:
        files += sorted(glob.glob(str(pathlib.Path(f).expanduser()))) or [f]
    stems = sorted({pathlib.Path(f).with_suffix("") for f in files if f.lower().endswith((".json", ".jpg", ".jpeg"))})
    pairs = [(s.with_suffix(".json"), s.with_suffix(".jpg")) for s in stems if s.with_suffix(".json").exists()]
    if not pairs:
        sys.exit("no frames with a .json")
    out = pathlib.Path(a.out).expanduser().resolve()
    frame_dirs = {p.parent.resolve() for p, _ in pairs}
    if any(out == d or d in out.parents for d in frame_dirs):
        sys.exit("--out must not be inside a frames folder (frames are read only)")
    out.mkdir(parents=True, exist_ok=True)

    vlm, limiter = None, None
    if a.vlm:
        import relay
        vlm = CappedVlm(step_check.NvidiaYesNo(relay.nvidia_key(), relay.NVIDIA_URL, relay.NVIDIA_MODEL), a.max_vlm)
        limiter = relay.RateLimiter(6)
    elif a.vlm_mock:
        vlm = MockVlm(a.vlm_mock == "yes")
    clock = {"t": 0.0}
    judge = step_check.StepJudge(vlm=vlm, limiter=limiter, config_path=a.config, clock=lambda: clock["t"],
                                 async_vlm=False, crop_dir=out / "crops",
                                 log=(lambda m: print("   ", m)))

    per_second = collections.Counter()
    results, last_status, last_state = [], None, None
    for jp, img in pairs:
        data = json.loads(jp.read_text())
        key = jp.name[:6]
        t = frame_time(jp, data, per_second[key], a.fps)
        per_second[key] += 1
        if t is None:
            continue
        clock["t"] = t
        state = data.get("state") or a.state
        jpeg = img.read_bytes() if img.exists() else b""
        dets = data.get("detections") or []
        if data.get("error"):
            payload = judge.current(state)
        else:
            payload = judge.observe(state, data.get("frameId", jp.stem), jpeg, dets, t=t)
        status = judge.status()["status"]
        labels = ",".join(sorted(d["partId"] for d in dets)) or "-"
        if state != last_state:
            print(f"== state {state or '(none)'}")
            last_state = state
        if not a.quiet or (payload and status != last_status):
            print(f"{jp.stem:28s} {labels:32s} {status}")
        last_status = status
        if payload and not any(r["state"] == state for r in results):
            results.append(payload)
            print(f"   >>> stepComplete {json.dumps(payload)}")

    summary = {"frames": len(pairs), "completions": results,
               "vlmCalls": getattr(vlm, "calls", 0), "crops": str(out / "crops")}
    (out / "step_replay.json").write_text(json.dumps(summary, indent=1))
    print(json.dumps({k: v for k, v in summary.items() if k != "completions"}), f"completions={len(results)}")


if __name__ == "__main__":
    main()
