#!/usr/bin/env python3
"""Booklet page -> walkthrough steps, with the NVIDIA VLM. Laptop-only proof of "author walkthroughs from the
instruction booklet": no hand-written steps, no CAD.

  python3 -I tools/vision-relay/booklet_steps.py PAGE.png [PAGE2.png ...] [--out steps.json] [--html review.html]

For each page it asks the VLM for the assembly steps shown (instruction text in the same style as the existing
walkthroughs, the parts each step joins, and the checks a builder should make, e.g. orientation), mapped to the
part ids the Quest detector knows where possible. Writes JSON and a side-by-side HTML review page.
Booklet images are not committed (see .gitignore); keep them outside the repo.
"""
import argparse, base64, html, io, json, pathlib, sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import relay  # noqa: E402
import requests  # noqa: E402
from PIL import Image  # noqa: E402

KNOWN = {
    "legs": "tan minifigure legs", "torso": "light blue minifigure torso with arms",
    "head": "yellow minifigure head with a printed face", "hairpiece": "brown/orange minifigure hair",
    "tire": "black rubber tire", "rim": "dark grey wheel rim (hub)", "axle-pin": "axle pin on the car chassis",
}

PROMPT = """This image is one page of an official LEGO building-instruction booklet. Turn what the page shows into
step-by-step assembly instructions for a person building it at a table while wearing an AR headset.

Known part ids (use these ids when a part matches; otherwise invent a short kebab-case id):
{known}

Return ONLY a JSON object, no prose:
{{"title": "<what is being built>",
  "steps": [
    {{"instruction": "<one imperative sentence, concrete, e.g. 'Press the torso onto the legs with the print facing forward.'>",
      "parts": ["<part id>", ...],
      "check": "<what to verify after the step, e.g. orientation or seating; empty if none>",
      "repeat": <how many times this step is done, 1 if once>}}
  ],
  "completion": "<short message when done>"}}
Follow the arrows and the order shown. One step per join. Keep 1-6 steps. Only describe the build shown in the
numbered step(s) and callout boxes; ignore page numbers, progress bars and decorative icons."""


def ask(key, png_bytes):
    im = Image.open(io.BytesIO(png_bytes)).convert("RGB")
    im = im.crop((0, 0, im.width, int(im.height * 0.9)))  # drop the progress bar (its little figure icon misleads)
    im.thumbnail((1280, 1280))
    buf = io.BytesIO()
    im.save(buf, "JPEG", quality=90)
    known = "\n".join(f"- {k}: {v}" for k, v in KNOWN.items())
    body = {"model": relay.NVIDIA_MODEL, "max_tokens": 1200, "temperature": 0,
            "chat_template_kwargs": {"enable_thinking": False},
            "messages": [{"role": "user", "content": [
                {"type": "text", "text": PROMPT.format(known=known)},
                {"type": "image_url", "image_url": {"url": "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode()}},
            ]}]}
    for _ in range(4):
        r = requests.post(relay.NVIDIA_URL, json=body, timeout=120,
                          headers={"Authorization": f"Bearer {key}", "Accept": "application/json"})
        if r.status_code == 429:
            import time
            time.sleep(20)
            continue
        r.raise_for_status()
        text = r.json()["choices"][0]["message"].get("content") or ""
        s, e = text.find("{"), text.rfind("}")
        return json.loads(text[s:e + 1]) if s >= 0 else {"error": text[:300]}
    return {"error": "rate limited"}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("pages", nargs="+")
    ap.add_argument("--out", default="")
    ap.add_argument("--html", default="")
    ap.add_argument("--compare", default="", help="JSON {page: [hand-written steps]} to show alongside")
    a = ap.parse_args()
    key = relay.nvidia_key()
    compare = json.loads(pathlib.Path(a.compare).read_text()) if a.compare else {}
    results = {}
    for p in a.pages:
        results[p] = ask(key, pathlib.Path(p).read_bytes())
        print(f"== {pathlib.Path(p).name}\n{json.dumps(results[p], indent=1)}", flush=True)
    if a.out:
        pathlib.Path(a.out).write_text(json.dumps(results, indent=1))
    if a.html:
        rows = []
        for p, r in results.items():
            img = base64.b64encode(pathlib.Path(p).read_bytes()).decode()
            gen = "".join(
                f"<li><b>{html.escape(s.get('instruction', ''))}</b><br><small>parts: {html.escape(', '.join(s.get('parts', [])))}"
                f"{' &middot; check: ' + html.escape(s['check']) if s.get('check') else ''}"
                f"{' &middot; x' + str(s['repeat']) if s.get('repeat', 1) not in (1, None) else ''}</small></li>"
                for s in r.get("steps", [])) or f"<li>{html.escape(str(r))}</li>"
            hand = "".join(f"<li>{html.escape(s)}</li>" for s in compare.get(pathlib.Path(p).name, []))
            rows.append(f"<section><img src='data:image/png;base64,{img}'><div><h3>Generated from the booklet: "
                        f"{html.escape(r.get('title', ''))}</h3><ol>{gen}</ol><p><i>{html.escape(r.get('completion', ''))}</i></p>"
                        + (f"<h3>Hand-written (current)</h3><ol>{hand}</ol>" if hand else "") + "</div></section>")
        pathlib.Path(a.html).write_text(
            "<!doctype html><meta charset=utf-8><title>Booklet to walkthrough</title><style>"
            "body{font:15px system-ui;margin:16px;background:#f6f6f4;color:#222}section{display:flex;gap:20px;"
            "background:#fff;padding:14px;border-radius:10px;margin-bottom:16px;flex-wrap:wrap}img{max-width:520px;"
            "width:100%;border:1px solid #ddd}div{flex:1;min-width:280px}h3{margin:.2em 0}li{margin:.4em 0}</style>"
            "<h1>Booklet page &rarr; walkthrough</h1>" + "".join(rows))
        print(f"[booklet] review page: {a.html}")


if __name__ == "__main__":
    main()
