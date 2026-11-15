#!/usr/bin/env python3
"""Pseudo-label saved frames with Nemotron (all parts), for training the local detector.

  python3 -I tools/vision-relay/label_frames.py SRC_DIR OUT_DIR [--per-minute 25]

For every SRC_DIR/*.jpg not yet labelled, writes OUT_DIR/<name>.jpg (symlink) + OUT_DIR/<name>.json in the relay-log
format, which tools/local-detector/build_dataset.py reads. Safe to re-run (skips done frames); retries after a 429.
"""
import argparse, json, os, pathlib, sys, time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import relay  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("out")
    ap.add_argument("--per-minute", type=int, default=25)
    a = ap.parse_args()
    src, out = pathlib.Path(a.src).resolve(), pathlib.Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    det = relay.NvidiaDetector(relay.nvidia_key())
    gap = 60.0 / a.per_minute
    todo = [f for f in sorted(src.glob("*.jpg")) if not (out / f.with_suffix(".json").name).exists()]
    print(f"{len(todo)} frames to label", flush=True)
    for i, f in enumerate(todo):
        t0 = time.time()
        for _ in range(4):
            try:
                dets, raw = det.detect(f.read_bytes(), None)
                break
            except Exception as e:  # noqa: BLE001
                if "429" not in str(e):
                    print(f"{f.name}: {e}", flush=True)
                    dets = None
                    break
                time.sleep(20)
        else:
            dets = None
        if dets is None:
            continue
        link = out / f.name
        if not link.exists():
            os.symlink(f, link)
        (out / f.with_suffix(".json").name).write_text(json.dumps({"frameId": f.stem, "detections": dets, "error": None}))
        print(f"[{i + 1}/{len(todo)}] {f.name}: {', '.join(d['partId'] for d in dets) or '-'}", flush=True)
        time.sleep(max(0.0, gap - (time.time() - t0)))


if __name__ == "__main__":
    main()
