#!/usr/bin/env python3
"""Turn the relay log (Quest frames + Nemotron answers) into a YOLO dataset of pseudo-labels.

  python3 -I tools/local-detector/build_dataset.py RELAY_LOG_DIR OUT_DIR [--val 0.15] [--negatives 40]

Images are symlinked (no disk copy). Frames with an error are skipped. Boxes larger than --max-side of the image are
dropped (Nemotron sometimes boxes a whole hand). A few frames with an empty answer are kept as negatives.
Split is by time (the last --val of frames go to val) so val frames are not near-duplicates of train frames.
"""
import argparse, json, os, pathlib, random

CLASSES = ["legs", "torso", "head", "hairpiece", "tire", "rim", "axle-pin"]  # order = YOLO class ids


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("log")
    ap.add_argument("out")
    ap.add_argument("--val", type=float, default=0.15)
    ap.add_argument("--negatives", type=int, default=40)
    ap.add_argument("--max-side", type=float, default=0.35)
    a = ap.parse_args()

    log, out = pathlib.Path(a.log).resolve(), pathlib.Path(a.out)
    pos, neg = [], []
    for j in sorted(log.glob("*.json"), key=lambda p: p.stat().st_mtime):
        img = j.with_suffix(".jpg")
        try:
            d = json.loads(j.read_text())
        except ValueError:
            continue
        if d.get("error") or not img.exists():
            continue
        lines = []
        for det in d.get("detections", []):
            if det.get("partId") not in CLASSES:
                continue
            x0, y0, x1, y1 = (min(1, max(0, float(det[k]))) for k in ("x0", "y0", "x1", "y1"))
            w, h = x1 - x0, y1 - y0
            if w <= 0.005 or h <= 0.005 or max(w, h) > a.max_side:
                continue
            lines.append(f"{CLASSES.index(det['partId'])} {(x0 + x1) / 2:.5f} {(y0 + y1) / 2:.5f} {w:.5f} {h:.5f}")
        (pos if lines else neg).append((img, lines))

    random.seed(0)
    neg = random.sample(neg, min(a.negatives, len(neg)))
    nval = max(1, int(len(pos) * a.val))
    splits = {"train": pos[:-nval] + neg, "val": pos[-nval:]}
    for split, items in splits.items():
        (out / "images" / split).mkdir(parents=True, exist_ok=True)
        (out / "labels" / split).mkdir(parents=True, exist_ok=True)
        for img, lines in items:
            link = out / "images" / split / img.name
            if not link.exists():
                os.symlink(img, link)
            (out / "labels" / split / img.with_suffix(".txt").name).write_text("\n".join(lines) + ("\n" if lines else ""))
    names = "\n".join(f"  {i}: {c}" for i, c in enumerate(CLASSES))
    (out / "data.yaml").write_text(f"path: {out.resolve()}\ntrain: images/train\nval: images/val\nnames:\n{names}\n")
    print(f"train {len(splits['train'])} ({len(neg)} negatives), val {len(splits['val'])} -> {out / 'data.yaml'}")


if __name__ == "__main__":
    main()
