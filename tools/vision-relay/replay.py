#!/usr/bin/env python3
"""Re-run the relay's current prompt/detector on saved frames, laptop only, and write an annotated grid + summary.
Use it to check prompt or filter changes in ~30 s without the headset.

  python3 -I tools/vision-relay/replay.py FRAMES... [--parts legs,torso,head,hairpiece] [--per-minute 35]
          [--out /tmp/replay] [--compare]

FRAMES: image files or globs (e.g. ~/.../relay-log/*_f6*.jpg). --compare also draws what the relay answered at the
time (from the frame's saved .json) in a second column. Respects the API cap (shares it with a running relay).
"""
import argparse, glob, io, json, pathlib, sys, time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import relay  # noqa: E402
from PIL import Image  # noqa: E402


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("frames", nargs="+")
    ap.add_argument("--parts", default="", help="comma list; default = all parts")
    ap.add_argument("--per-minute", type=int, default=35)
    ap.add_argument("--out", default="/tmp/forge-replay")
    ap.add_argument("--compare", action="store_true")
    ap.add_argument("--tile", type=int, default=320)
    a = ap.parse_args()

    files = []
    for f in a.frames:
        files += sorted(glob.glob(str(pathlib.Path(f).expanduser()))) or [f]
    files = [f for f in files if f.lower().endswith((".jpg", ".jpeg", ".png"))]
    if not files:
        sys.exit("no frames")
    parts = [p for p in a.parts.split(",") if p] or None
    det = relay.NvidiaDetector(relay.nvidia_key())
    out = pathlib.Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    gap = 60.0 / max(1, a.per_minute)
    tiles, rows, per = [], [], {}

    for i, f in enumerate(files):
        t0 = time.time()
        buf = io.BytesIO()
        Image.open(f).convert("RGB").save(buf, "JPEG", quality=88)
        jpeg = buf.getvalue()
        for attempt in range(3):
            try:
                dets, raw = det.detect(jpeg, parts)
                err = None
                break
            except Exception as e:  # noqa: BLE001
                dets, raw, err = [], "", str(e)
                if "429" in err:
                    time.sleep(20)  # rate window; retry
                    continue
                break
        dt = time.time() - t0
        labels = ", ".join(d["partId"] for d in dets) or "-"
        print(f"[{i + 1}/{len(files)}] {pathlib.Path(f).name}: {dt:.1f}s {err or labels}", flush=True)
        for d in dets:
            per[d["partId"]] = per.get(d["partId"], 0) + 1
        rows.append({"frame": f, "seconds": round(dt, 2), "detections": dets, "error": err})
        now = Image.open(io.BytesIO(relay.annotate(jpeg, dets, f"NOW {pathlib.Path(f).stem}: {err or labels}")))
        tile = [now.resize((a.tile, a.tile))]
        if a.compare:
            old_json = pathlib.Path(f).with_suffix(".json")
            old = json.loads(old_json.read_text())["detections"] if old_json.exists() else []
            old_lab = ", ".join(d["partId"] for d in old) or "-"
            tile.append(Image.open(io.BytesIO(relay.annotate(jpeg, old, f"THEN: {old_lab}"))).resize((a.tile, a.tile)))
        tiles.append(tile)
        time.sleep(max(0.0, gap - (time.time() - t0)))

    cols = 4 if not a.compare else 2
    w = a.tile * (2 if a.compare else 1)
    sheet = Image.new("RGB", (cols * w, ((len(tiles) + cols - 1) // cols) * a.tile), "black")
    for i, tile in enumerate(tiles):
        for j, im in enumerate(tile):
            sheet.paste(im, ((i % cols) * w + j * a.tile, (i // cols) * a.tile))
    sheet.save(out / "grid.jpg", quality=85)
    summary = {"frames": len(files), "with_detections": sum(1 for r in rows if r["detections"]),
               "errors": sum(1 for r in rows if r["error"]), "per_part": per,
               "median_seconds": sorted(r["seconds"] for r in rows)[len(rows) // 2], "parts_asked": parts or "all"}
    (out / "results.json").write_text(json.dumps({"summary": summary, "rows": rows}, indent=1))
    print(json.dumps(summary))
    print(f"[replay] grid: {out / 'grid.jpg'}")


if __name__ == "__main__":
    main()
