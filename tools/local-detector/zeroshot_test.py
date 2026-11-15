#!/usr/bin/env python3
"""Zero-shot test of an open-vocabulary YOLO (YOLO-World / YOLOE) on saved Quest frames, laptop CPU.

  ~/.venvs/forge-detector/bin/python tools/local-detector/zeroshot_test.py FRAMES... \
      [--model yolov8s-worldv2.pt] [--conf 0.05] [--parts driver|wheel|all] [--out /tmp/zeroshot]

For each frame it draws the local model's boxes (left) next to Nemotron's saved answer from the relay log (right, if
the frame has a .json next to it) and writes grid.jpg + results.json with per-part counts and CPU time per frame.
The same model + class prompts can later be exported to ONNX for the Quest (export_onnx.py).
"""
import argparse, glob, json, pathlib, sys, time

from PIL import Image, ImageDraw, ImageFont

# Part id -> text prompt for the open-vocabulary model (part ids match Forge.PartIds on the Quest).
PROMPTS = {
    "legs": "tan lego minifigure legs",
    "torso": "light blue lego minifigure torso",
    "head": "yellow lego minifigure head",
    "hairpiece": "orange lego minifigure hair",
    "tire": "black rubber lego tire",
    "rim": "grey lego wheel rim",
    "axle-pin": "small yellow lego axle pin",
}
SETS = {"driver": ["legs", "torso", "head", "hairpiece"], "wheel": ["tire", "rim", "axle-pin"], "all": list(PROMPTS)}
COLORS = {"legs": "#d2b48c", "torso": "#1e9bff", "head": "#ffd400", "hairpiece": "#ff8c00",
          "tire": "#ff3030", "rim": "#00e0e0", "axle-pin": "#ff00ff"}


def draw(im, dets, caption, font):
    im = im.copy()
    d = ImageDraw.Draw(im)
    w, h = im.size
    for part, conf, (x0, y0, x1, y1) in dets:
        c = COLORS.get(part, "#ffffff")
        d.rectangle((x0 * w, y0 * h, x1 * w, y1 * h), outline=c, width=max(3, w // 200))
        d.text((x0 * w + 3, max(0, y0 * h - font.size - 3)), f"{part} {conf:.2f}" if conf is not None else part,
               fill=c, font=font)
    d.rectangle((0, h - font.size - 10, w, h), fill="#000")
    d.text((6, h - font.size - 6), caption, fill="#fff", font=font)
    return im


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("frames", nargs="+")
    ap.add_argument("--model", default="yolov8s-worldv2.pt")
    ap.add_argument("--conf", type=float, default=0.05)
    ap.add_argument("--parts", default="all", choices=list(SETS))
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--out", default="/tmp/zeroshot")
    ap.add_argument("--tile", type=int, default=320)
    a = ap.parse_args()

    from ultralytics import YOLO  # imported late so --help works without the venv

    ids = SETS[a.parts]
    model = YOLO(a.model)
    model.set_classes([PROMPTS[i] for i in ids])

    files = []
    for f in a.frames:
        files += sorted(glob.glob(str(pathlib.Path(f).expanduser()))) or [f]
    files = [f for f in files if f.lower().endswith((".jpg", ".jpeg", ".png"))]
    out = pathlib.Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    try:
        font = ImageFont.truetype("DejaVuSans-Bold.ttf", 18)
    except OSError:
        font = ImageFont.load_default()

    tiles, rows, per, times = [], [], {}, []
    for f in files:
        im = Image.open(f).convert("RGB")
        t0 = time.time()
        r = model.predict(im, conf=a.conf, imgsz=a.imgsz, verbose=False)[0]
        dt = time.time() - t0
        times.append(dt)
        dets = []
        for b in r.boxes:
            cls = int(b.cls.item())
            x0, y0, x1, y1 = (b.xyxyn[0].tolist())
            dets.append((ids[cls], float(b.conf.item()), (x0, y0, x1, y1)))
        best = {}
        for part, conf, box in dets:  # keep the best box per part, like the Quest does
            if part not in best or conf > best[part][1]:
                best[part] = (part, conf, box)
        dets = list(best.values())
        for part, _, _ in dets:
            per[part] = per.get(part, 0) + 1
        labels = ", ".join(f"{p} {c:.2f}" for p, c, _ in dets) or "-"
        print(f"{pathlib.Path(f).name}: {dt * 1000:.0f} ms  {labels}", flush=True)
        rows.append({"frame": f, "ms": round(dt * 1000), "detections": [[p, round(c, 3), b] for p, c, b in dets]})

        left = draw(im, dets, f"LOCAL {dt * 1000:.0f}ms: {labels}", font).resize((a.tile, a.tile))
        tile = [left]
        j = pathlib.Path(f).with_suffix(".json")
        if j.exists():
            old = json.loads(j.read_text()).get("detections", [])
            odets = [(d["partId"], None, (d["x0"], d["y0"], d["x1"], d["y1"])) for d in old]
            tile.append(draw(im, odets, "NEMOTRON: " + (", ".join(d["partId"] for d in old) or "-"), font)
                        .resize((a.tile, a.tile)))
        tiles.append(tile)

    width = 2 * a.tile
    cols = 2
    sheet = Image.new("RGB", (cols * width, ((len(tiles) + cols - 1) // cols) * a.tile), "black")
    for i, tile in enumerate(tiles):
        for k, t in enumerate(tile):
            sheet.paste(t, ((i % cols) * width + k * a.tile, (i // cols) * a.tile))
    sheet.save(out / "grid.jpg", quality=85)
    summary = {"model": a.model, "parts": ids, "frames": len(files), "per_part": per,
               "with_detections": sum(1 for r in rows if r["detections"]),
               "median_ms": sorted(round(t * 1000) for t in times)[len(times) // 2] if times else None}
    (out / "results.json").write_text(json.dumps({"summary": summary, "rows": rows}, indent=1))
    print(json.dumps(summary))
    print(f"[zeroshot] grid: {out / 'grid.jpg'}")


if __name__ == "__main__":
    sys.exit(main())
