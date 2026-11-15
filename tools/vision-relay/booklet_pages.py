#!/usr/bin/env python3
"""Contact sheet of an instruction-booklet PDF with page numbers, to pick the pages to generate from (no API calls).

  python3 -I tools/vision-relay/booklet_pages.py BOOKLET.pdf [--out booklet-pages.jpg] [--cols 8]

Then: generate_walkthrough.py --pdf BOOKLET.pdf --pages N --id ...  (keep the booklet and the sheet out of the repo).
"""
import argparse, pathlib, subprocess, tempfile

from PIL import Image, ImageDraw, ImageFont


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("pdf")
    ap.add_argument("--out", default="booklet-pages.jpg")
    ap.add_argument("--cols", type=int, default=8)
    a = ap.parse_args()
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run(["pdftoppm", "-r", "20", "-png", a.pdf, str(pathlib.Path(tmp) / "p")], check=True)
        files = sorted(pathlib.Path(tmp).glob("p-*.png"))
        ims = [Image.open(f).convert("RGB") for f in files]
        w, h = max(i.width for i in ims), max(i.height for i in ims)
        rows = (len(ims) + a.cols - 1) // a.cols
        sheet = Image.new("RGB", (a.cols * w, rows * h), "white")
        d = ImageDraw.Draw(sheet)
        try:
            font = ImageFont.truetype("DejaVuSans-Bold.ttf", 16)
        except OSError:
            font = ImageFont.load_default()
        for k, im in enumerate(ims):
            x, y = (k % a.cols) * w, (k // a.cols) * h
            sheet.paste(im, (x, y))
            d.rectangle((x, y, x + 30, y + 20), fill="white")
            d.text((x + 3, y + 1), str(k + 1), fill="red", font=font)
        sheet.save(a.out, quality=85)
    print(f"{len(ims)} pages -> {a.out}")


if __name__ == "__main__":
    main()
