"""Walkthrough spec: one JSON file per walkthrough, so a walkthrough can be generated instead of hand-coded.

Format (tools/vision-relay/walkthroughs/<id>.json):
{
  "id": "booklet-40650-driver",              # kebab-case
  "title": "Minifigure driver",
  "completion": "Driver assembled",
  "source": {"kind": "booklet" | "manual", "image": "<file name>", "model": "<vlm>"},
  "parts": [{"id": "torso", "name": "blue torso", "description": "...", "detectable": true}, ...],
  "steps": [{"instruction": "Press the torso onto the legs.",   # shown on the Quest as "n/N: instruction"
             "highlight": ["torso", "legs"],                    # part ids boxed for this step
             "check": "Torso clicks onto the legs.",            # what the builder should verify (text)
             "rule": {"a": "torso", "b": "legs", "mode": "touch", "anchor": "torso",
                      "question": "Is the torso attached to the legs?"} | null}, ...]
}
"detectable" = the Quest/laptop detector knows the part (relay.PARTS); only detectable parts get boxes and step rules.
On the Quest the generated walkthrough runs as flow "gen" (state header "gen,step=N,complete=..").
"""
import json, pathlib, re

FLOW = "gen"
ID_RE = re.compile(r"^[a-z0-9]+(-[a-z0-9]+)*$")
MAX_STEPS = 12
REPEAT_RE = re.compile(r"^(repeat|do this again|do the same)\b", re.I)
PLACE_RE = re.compile(r"^(place|put|lay|set)\b.*\b(table|work surface|in front of you)\b", re.I)
KEYWORDS = {"axle-pin": ("axle",)}  # detectable part -> words in an instruction that mean it


class SpecError(ValueError):
    pass


def known_parts():
    try:
        import relay  # noqa: WPS433 (lazy: avoid a hard import cycle)
        return set(relay.PARTS)
    except Exception:  # noqa: BLE001
        return {"legs", "torso", "head", "hairpiece", "tire", "rim", "axle-pin"}


def slug(s):
    s = re.sub(r"[^a-z0-9]+", "-", str(s).lower()).strip("-")
    return s or "part"


def normalise(raw, spec_id, source=None):
    """Turn a VLM answer (loosely shaped) into a valid spec dict, fixing what can be fixed; raises SpecError otherwise.
    Returns (spec, warnings)."""
    warnings = []
    known = known_parts()
    if not isinstance(raw, dict):
        raise SpecError("answer is not a JSON object")
    parts, ids = [], set()
    for p in raw.get("parts") or []:
        if isinstance(p, str):
            p = {"id": p}
        pid = slug(p.get("id") or p.get("name"))
        if pid in ids:
            continue
        ids.add(pid)
        parts.append({"id": pid, "name": str(p.get("name") or pid.replace("-", " ")),
                      "description": str(p.get("description") or ""), "detectable": pid in known})
    steps = []
    for i, s in enumerate(raw.get("steps") or []):
        if not isinstance(s, dict):
            continue
        text = " ".join(str(s.get("instruction") or "").split())
        if not text:
            warnings.append(f"step {i + 1}: empty instruction dropped")
            continue
        hl = []
        for h in s.get("highlight") or s.get("parts") or []:
            hid = slug(h)
            if hid not in ids:  # a step may mention a part the parts list forgot: add it
                ids.add(hid)
                parts.append({"id": hid, "name": hid.replace("-", " "), "description": "", "detectable": hid in known})
                warnings.append(f"step {len(steps) + 1}: part '{hid}' was not in the parts list; added")
            if hid not in hl:
                hl.append(hid)
        # A "Repeat ..." step is a note on the step before it, not a step of its own.
        if steps and REPEAT_RE.match(text):
            steps[-1]["instruction"] += " " + text
            warnings.append(f"'{text[:40]}...' folded into step {len(steps)}")
            continue
        # The detector knows the car's axle pin: a step that mentions an axle uses it.
        for pid, words in KEYWORDS.items():
            if pid in known and pid not in hl and any(w in text.lower() for w in words):
                if pid not in ids:
                    ids.add(pid)
                    parts.append({"id": pid, "name": pid.replace("-", " "), "description": "", "detectable": True})
                hl.append(pid)
        steps.append({"instruction": text, "highlight": hl, "check": " ".join(str(s.get("check") or "").split()),
                      "rule": None, "_question": str(s.get("question") or "")})
    steps = _fold_setup_steps(steps, warnings)
    steps = _collapse_repeats(steps, warnings)
    if not steps:
        raise SpecError("no usable steps")
    if len(steps) > MAX_STEPS:
        warnings.append(f"{len(steps)} steps; kept the first {MAX_STEPS}")
        steps = steps[:MAX_STEPS]
    detectable = {p["id"] for p in parts if p["detectable"]}
    used = set()
    for n, s in enumerate(steps):
        det = [h for h in s["highlight"] if h in detectable]
        # Never reuse a pair an earlier step already joined (it would complete instantly): prefer an unused pair
        # with a proven rule, else an unused pair with a generic "touch" rule, else manual.
        pairs = [(a, b) for i, a in enumerate(det) for b in det[i + 1:] if frozenset((a, b)) not in used]
        proven = [pr for pr in pairs if proven_rule(*pr)]
        # A made-up "touch" rule only when a part has no tuned rule at all (a newly detectable part); between parts
        # whose real joins we know, an unfamiliar pair is a guess (e.g. rim + axle-pin after the wheel is mounted).
        guessable = [(a, b) for a, b in pairs if not (has_tuned_rule(a) and has_tuned_rule(b))]
        pick = proven[0] if proven else (guessable[0] if guessable else None)
        if pick:
            a, b = pick
            used.add(frozenset(pick))
            s["rule"] = proven_rule(a, b) or {
                "a": a, "b": b, "mode": "touch", "anchor": a,
                "question": s["_question"] or f"Is the {a.replace('-', ' ')} attached to the {b.replace('-', ' ')}?"}
        elif len(det) >= 2:
            warnings.append(f"step {n + 1}: its parts were already joined in an earlier step; manual Next")
        elif s["highlight"] and not det:
            warnings.append(f"step {n + 1}: no detectable parts; text-only, manual Next")
        s.pop("_question")
    spec = {"id": spec_id, "title": str(raw.get("title") or spec_id), "completion":
            str(raw.get("completion") or "Done"), "source": source or {}, "parts": parts, "steps": steps}
    validate(spec)
    return spec, warnings


def _fold_setup_steps(steps, warnings):
    """'Place the legs on the table.' is setup for the next step, not a step of its own: fold it in."""
    out = []
    original = [s["instruction"] for s in steps]  # match on the model's own text, not on already-folded text
    for i, s in enumerate(steps):
        nxt = steps[i + 1] if i + 1 < len(steps) else None
        if nxt and PLACE_RE.match(original[i]) and not PLACE_RE.match(original[i + 1]):
            nxt["instruction"] = s["instruction"].rstrip(". ") + ", then " + nxt["instruction"][:1].lower() + \
                nxt["instruction"][1:]
            nxt["highlight"] = s["highlight"] + [h for h in nxt["highlight"] if h not in s["highlight"]]
            warnings.append(f"setup step '{s['instruction'][:40]}' folded into the next step")
            continue
        out.append(s)
    return out


def _collapse_repeats(steps, warnings):
    """Consecutive steps doing the same thing to the same parts (e.g. one per wheel) -> one step '(repeat Nx)'."""
    def key(s):
        return (frozenset(s["highlight"]), " ".join(s["instruction"].lower().split()[:3]))
    out = []
    for s in steps:
        if out and s["highlight"] and key(s) == key(out[-1]["_last"]):
            out[-1]["_n"] += 1
            out[-1]["_last"] = s
            continue
        s["_n"], s["_last"] = 1, s
        out.append(s)
    for s in out:
        n = s.pop("_n")
        s.pop("_last")
        if n > 1:
            text = re.sub(r"\b(the\s+)?(front|rear|back)[ -](left|right)\b", "each", s["instruction"], flags=re.I)
            s["instruction"] = text.rstrip(". ") + f" (repeat {n}x)."
            warnings.append(f"{n} identical steps collapsed: '{s['instruction'][:40]}'")
    return out


def has_tuned_rule(part):
    """Does any hand-tuned step_check rule involve this part? (Then we know how it really joins.)"""
    try:
        import step_check
    except Exception:  # noqa: BLE001
        return False
    return any("alias" not in r and part in (r["a"], r["b"]) for r in step_check.STEP_RULES.values())


def proven_rule(a, b):
    """The hand-tuned step_check rule for this part pair, if any (mode, anchor and tested yes/no question)."""
    try:
        import step_check
    except Exception:  # noqa: BLE001
        return None
    for r in step_check.STEP_RULES.values():
        if "alias" not in r and {r["a"], r["b"]} == {a, b}:
            return {k: r[k] for k in ("a", "b", "mode", "anchor", "question")}
    return None


def validate(spec):
    if not ID_RE.match(spec.get("id", "")):
        raise SpecError(f"bad id {spec.get('id')!r} (kebab-case)")
    ids = {p["id"] for p in spec.get("parts", [])}
    det = {p["id"] for p in spec.get("parts", []) if p.get("detectable")}
    steps = spec.get("steps") or []
    if not 1 <= len(steps) <= MAX_STEPS:
        raise SpecError(f"{len(steps)} steps (1-{MAX_STEPS})")
    for i, s in enumerate(steps):
        if not s.get("instruction"):
            raise SpecError(f"step {i + 1}: empty instruction")
        for h in s.get("highlight", []):
            if h not in ids:
                raise SpecError(f"step {i + 1}: unknown part {h}")
        r = s.get("rule")
        if r:
            if r.get("mode") not in ("touch", "inside"):
                raise SpecError(f"step {i + 1}: rule mode {r.get('mode')}")
            for k in ("a", "b", "anchor"):
                if r.get(k) not in det:
                    raise SpecError(f"step {i + 1}: rule part {r.get(k)} is not detectable")
    return spec


def load(path):
    spec = json.loads(pathlib.Path(path).read_text())
    return validate(spec)


def load_dir(directory):
    out = {}
    for p in sorted(pathlib.Path(directory).glob("*.json")):
        if p.name.endswith(".raw.json"):
            continue
        try:
            s = load(p)
            out[s["id"]] = s
        except (SpecError, ValueError, KeyError) as e:
            print(f"[walkthrough] skipped {p.name}: {e}", flush=True)
    return out


def step_rules(spec):
    """Rules + step count for step_check, under flow 'gen'."""
    rules = {(FLOW, i): dict(s["rule"]) for i, s in enumerate(spec["steps"]) if s.get("rule")}
    return rules, len(spec["steps"])


def detectable_parts(spec):
    return [p["id"] for p in spec["parts"] if p.get("detectable")]


def for_quest(spec):
    """Flat shape for Unity's JsonUtility (no nested optional objects)."""
    return {"id": spec["id"], "title": spec["title"], "completion": spec["completion"],
            "detectable": detectable_parts(spec),
            "steps": [{"instruction": s["instruction"], "highlight": [h for h in s["highlight"]
                                                                     if h in set(detectable_parts(spec))],
                       "check": s.get("check", ""), "auto": bool(s.get("rule"))} for s in spec["steps"]]}
