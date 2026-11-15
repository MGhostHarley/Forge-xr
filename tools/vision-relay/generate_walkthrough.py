#!/usr/bin/env python3
"""LEGO instruction-booklet page -> walkthrough spec, with the NVIDIA VLM. No hand-written steps.

  python3 -I tools/vision-relay/generate_walkthrough.py --pdf BOOKLET.pdf --pages 4 --id my-walkthrough
  python3 -I tools/vision-relay/generate_walkthrough.py PAGE.png [PAGE2.png ...] --id my-walkthrough
      [-o tools/vision-relay/walkthroughs/my-walkthrough.json] [--raw saved.raw.json]

Several pages (--pages 44-45 or several images) become one walkthrough, steps in page order.

Writes the validated spec (walkthrough_spec.py format) and <id>.raw.json next to it (prompt + raw answer + warnings).
The relay serves it to the Quest with --walkthrough-dir / --walkthrough. Uses ~3 API calls at most (retries on 429).
Booklet page images are LEGO's: keep them outside the repo (only the generated JSON is committed).
"""
import argparse, base64, io, json, pathlib, sys, time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import relay  # noqa: E402
import walkthrough_spec as ws  # noqa: E402
import requests  # noqa: E402
from PIL import Image  # noqa: E402

COMMON = """Return ONLY a JSON object, no prose:
{{"title": "<what is being built>",
  "parts": [{{"id": "<kebab-case id>", "name": "<short name>", "description": "<colour/shape, how to recognise it>"}}],
  "steps": [{{"instruction": "<one imperative sentence for a person building it at a table>",
              "highlight": ["<part ids this step uses>"],
              "check": "<what to verify after the step, e.g. orientation/polarity/seating; empty if none>",
              "question": "<a yes/no question a camera could answer to confirm the step is done; empty if none>"}}],
  "completion": "<short message when finished>"}}
Use these ids (and colours) when a part matches - the detector knows them: {known}.
Describe parts by their real colour as printed in the booklet.
One step per join or connection, in build order, 1-{max} steps."""

PROMPTS = {
    "booklet": """This image is one page of an official LEGO building-instruction booklet. Turn the build shown in the
numbered step(s) and callout boxes into assembly steps. Follow the arrows and the order shown. If the page says a step
is repeated (e.g. "5x"), describe ONE unit and say "(repeat 5x)" in that instruction, don't add separate repeat
steps. Pins, studs and holes that are already part of a model are not separate parts. Ignore page numbers, progress
bars and decorative icons.
""",
}


def ask(key, image_path, kind):
    im = Image.open(image_path).convert("RGB")
    if kind == "booklet":
        im = im.crop((0, 0, im.width, int(im.height * 0.9)))  # the progress bar's figure icon misleads the model
    im.thumbnail((1280, 1280))
    buf = io.BytesIO()
    im.save(buf, "JPEG", quality=90)
    known = "; ".join(f"{k} = {v}" for k, v in relay.PARTS.items())
    prompt = PROMPTS[kind] + COMMON.format(known=known, max=ws.MAX_STEPS)
    body = {"model": relay.NVIDIA_MODEL, "max_tokens": 1500, "temperature": 0,
            "chat_template_kwargs": {"enable_thinking": False},
            "messages": [{"role": "user", "content": [
                {"type": "text", "text": prompt},
                {"type": "image_url", "image_url": {"url": "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode()}},
            ]}]}
    for attempt in range(3):
        r = requests.post(relay.NVIDIA_URL, json=body, timeout=120,
                          headers={"Authorization": f"Bearer {key}", "Accept": "application/json"})
        if r.status_code == 429:
            print("[generate] rate limited (429), waiting 25 s", flush=True)
            time.sleep(25)
            continue
        r.raise_for_status()
        return prompt, r.json()["choices"][0]["message"].get("content") or ""
    raise RuntimeError("NVIDIA API kept answering 429")


def render_pdf_pages(pdf, pages, out_dir):
    """Render booklet pages (e.g. "4" or "44-45,47") to PNG with poppler's pdftoppm; returns the image paths."""
    import subprocess
    nums = []
    for part in str(pages).split(","):
        a, _, b = part.strip().partition("-")
        nums += list(range(int(a), int(b or a) + 1))
    paths = []
    for n in nums:
        stem = pathlib.Path(out_dir) / f"page{n:03d}"
        subprocess.run(["pdftoppm", "-r", "80", "-png", "-singlefile", "-f", str(n), "-l", str(n), str(pdf), str(stem)],
                       check=True)
        paths.append(stem.with_suffix(".png"))
    return paths


def merge(answers):
    """Several pages' answers -> one: parts united, steps in page order."""
    if len(answers) == 1:
        return answers[0]
    merged = {"title": answers[0].get("title", ""), "parts": [], "steps": [],
              "completion": answers[-1].get("completion", "")}
    for a in answers:
        merged["parts"] += a.get("parts") or []
        merged["steps"] += a.get("steps") or []
    return merged


def parse(text):
    s, e = text.find("{"), text.rfind("}")
    if s < 0 or e <= s:
        raise ws.SpecError("no JSON object in the answer")
    return json.loads(text[s:e + 1])


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("images", nargs="*", help="booklet page images (or use --pdf/--pages)")
    ap.add_argument("--pdf", default="", help="the instruction booklet PDF")
    ap.add_argument("--pages", default="", help="pages of --pdf, e.g. 4 or 44-45")
    ap.add_argument("--kind", choices=sorted(PROMPTS), default="booklet")
    ap.add_argument("--id", required=True, help="kebab-case walkthrough id")
    ap.add_argument("-o", "--out", default="")
    ap.add_argument("--raw", default="", help="re-use a saved <id>.raw.json answer instead of calling the API")
    a = ap.parse_args()
    out = pathlib.Path(a.out or pathlib.Path(__file__).resolve().parent / "walkthroughs" / f"{a.id}.json")
    out.parent.mkdir(parents=True, exist_ok=True)
    raw_path = out.with_name(f"{a.id}.raw.json")

    import tempfile
    tmp = tempfile.TemporaryDirectory()
    if a.raw:
        saved = json.loads(pathlib.Path(a.raw).read_text())
        prompt = saved["prompt"]
        texts = saved["answers"] if "answers" in saved else [saved["answer"]]
        names = saved.get("images", [])
    else:
        images = [pathlib.Path(i) for i in a.images]
        if a.pdf:
            if not a.pages:
                sys.exit("--pdf needs --pages (e.g. --pages 4)")
            images += render_pdf_pages(a.pdf, a.pages, tmp.name)
        if not images:
            sys.exit("give page images or --pdf BOOKLET.pdf --pages N")
        key = relay.nvidia_key()
        texts, prompt = [], ""
        for img in images:
            prompt, text = ask(key, img, a.kind)
            texts.append(text)
        names = [(f"{pathlib.Path(a.pdf).name} p{img.stem[4:].lstrip('0')}" if a.pdf and img.parent == pathlib.Path(tmp.name)
                  else img.name) for img in images]
    source = {"kind": a.kind, "image": ", ".join(names), "model": relay.NVIDIA_MODEL}
    try:
        spec, warnings = ws.normalise(merge([parse(t) for t in texts]), a.id, source)
    except (ws.SpecError, ValueError) as e:
        raw_path.write_text(json.dumps({"prompt": prompt, "answers": texts, "images": names, "error": str(e)}, indent=1))
        sys.exit(f"[generate] invalid answer: {e} (saved {raw_path})")
    raw_path.write_text(json.dumps({"prompt": prompt, "answers": texts, "images": names, "warnings": warnings},
                                   indent=1))
    out.write_text(json.dumps(spec, indent=1) + "\n")
    for w in warnings:
        print(f"[generate] warning: {w}")
    print(f"[generate] {spec['title']}: {len(spec['steps'])} steps, "
          f"{sum(1 for s in spec['steps'] if s['rule'])} auto-checked, parts: "
          + ", ".join(p['id'] + ('*' if p['detectable'] else '') for p in spec['parts']) + "  (* = detectable)")
    for i, s in enumerate(spec["steps"]):
        print(f"  {i + 1}. {s['instruction']}" + ("  [auto]" if s["rule"] else ""))
    print(f"[generate] wrote {out}")


if __name__ == "__main__":
    main()
