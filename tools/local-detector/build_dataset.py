#!/usr/bin/env python3
"""Turn the relay log (Quest frames + Nemotron answers) into a YOLO dataset of pseudo-labels.

  python3 -I tools/local-detector/build_dataset.py OUT_DIR RELAY_LOG_DIR [MORE_LABEL_DIRS...] [--val 0.15]

Images are symlinked (no disk copy). Frames with an error are skipped. Boxes larger than --max-side of the image are
dropped (Nemotron sometimes boxes a whole hand). A few frames with an empty answer are kept as negatives.
Split is by time (the last --val of frames go to val) so val frames are not near-duplicates of train frames.
"""
import argparse, json, os, pathlib, random

CLASSES = ["legs", "torso", "head", "hairpiece", "tire", "rim", "axle-pin"]  # order = YOLO class ids


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("logs", nargs="+", help="relay-log style dirs (jpg + json); later dirs go to train only")
    ap.add_argument("--val", type=float, default=0.15)
    ap.add_argument("--negatives", type=int, default=40)
    ap.add_argument("--max-side", type=float, default=0.35)
    ap.add_argument("--drop-frames-with", default="", help="comma list: skip frames that have any of these labels")
    ap.add_argument("--ignore", default="", help="comma list: drop these labels' boxes (frame kept)")
    a = ap.parse_args()

    out = pathlib.Path(a.out)
    pos, neg, extra = [], [], []
    jsons = []
    for k, log in enumerate(a.logs):
        jsons += [(k, j) for j in sorted(pathlib.Path(log).resolve().glob("*.json"), key=lambda p: p.stat().st_mtime)]
    for k, j in jsons:
        img = j.with_suffix(".jpg") if j.with_suffix(".jpg").exists() else j.with_suffix(".png")
        try:
            d = json.loads(j.read_text())
        except ValueError:
            continue
        if d.get("error") or not img.exists():
            continue
        drop = {p for p in a.drop_frames_with.split(",") if p}
        ignore = {p for p in a.ignore.split(",") if p}
        if d.get("via") != "self" and any(det.get("partId") in drop for det in d.get("detections", [])):
            continue
        lines = []
        for det in d.get("detections", []):
            if det.get("partId") not in CLASSES or (d.get("via") != "self" and det.get("partId") in ignore):
                continue
            x0, y0, x1, y1 = (min(1, max(0, float(det[k]))) for k in ("x0", "y0", "x1", "y1"))
            w, h = x1 - x0, y1 - y0
            if w <= 0.005 or h <= 0.005 or max(w, h) > a.max_side:
                continue
            lines.append(f"{CLASSES.index(det['partId'])} {(x0 + x1) / 2:.5f} {(y0 + y1) / 2:.5f} {w:.5f} {h:.5f}")
        if k > 0 and lines:
            extra.append((img.resolve(), lines))
        else:
            (pos if lines else neg).append((img.resolve(), lines))

    random.seed(0)
    neg = random.sample(neg, min(a.negatives, len(neg)))
    nval = max(1, int(len(pos) * a.val))
    splits = {"train": pos[:-nval] + neg + extra, "val": pos[-nval:]}
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
