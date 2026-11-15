#!/usr/bin/env python3
"""Self-label frames with a trained model's confident boxes (anchor set so a fine-tune doesn't forget), skipping
near-duplicate frames. Writes relay-log style jpg symlink + json into OUT_DIR (read by build_dataset.py).

  ~/.venvs/forge-detector/bin/python -I tools/local-detector/self_label.py MODEL SRC_DIR OUT_DIR [--conf 0.5] [--min-parts 2]

Only frames where at least --min-parts parts are confidently found are kept (fewer missed pieces = fewer false
negatives in training). Duplicates: frames whose 32x32 grey thumbnail differs from the last kept by < --dup.
"""
import argparse, json, os, pathlib

import numpy as np
from PIL import Image


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("model")
    ap.add_argument("src")
    ap.add_argument("out")
    ap.add_argument("--conf", type=float, default=0.5)
    ap.add_argument("--min-parts", type=int, default=2)
    ap.add_argument("--dup", type=float, default=6.0, help="mean abs grey diff (0-255) below which a frame is a dup")
    a = ap.parse_args()
    from ultralytics import YOLO
    m = YOLO(a.model)
    src, out = pathlib.Path(a.src).resolve(), pathlib.Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    last, kept, dups, weak = None, 0, 0, 0
    for f in sorted(src.glob("*.jpg")):
        im = Image.open(f).convert("RGB")
        thumb = np.asarray(im.convert("L").resize((32, 32)), dtype=np.float32)
        if last is not None and np.abs(thumb - last).mean() < a.dup:
            dups += 1
            continue
        r = m.predict(im, conf=a.conf, verbose=False)[0]
        best = {}
        for b in r.boxes:
            p, c = m.names[int(b.cls)], float(b.conf)
            if c > best.get(p, (0,))[0]:
                best[p] = (c, b.xyxyn[0].tolist())
        if len(best) < a.min_parts:
            weak += 1
            continue
        last = thumb
        dets = [{"partId": p, "x0": x0, "y0": y0, "x1": x1, "y1": y1, "confidence": round(c, 3)}
                for p, (c, (x0, y0, x1, y1)) in best.items()]
        link = out / f.name
        if not link.exists():
            os.symlink(f, link)
        (out / f.with_suffix(".json").name).write_text(json.dumps({"detections": dets, "error": None, "via": "self"}))
        kept += 1
    print(f"kept {kept}, duplicates {dups}, too few confident parts {weak}")


if __name__ == "__main__":
    main()
