#!/usr/bin/env python3
"""Compare a generated walkthrough with the hand-written one (same spec format), by what each step JOINS.

  python3 -I tools/vision-relay/compare_walkthroughs.py walkthroughs/booklet-40650-driver.json walkthroughs/manual-driver.json

Reports: joins the hand-written walkthrough has that the generated one also has (coverage), extra joins, whether the
shared joins come in the same order, how many steps complete automatically, and the steps side by side.
Wording is not compared (it is expected to differ); what matters is the same parts, the same joins, the same order.
"""
import argparse, json, pathlib, sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import walkthrough_spec as ws  # noqa: E402


def joins(spec):
    """Ordered list of frozenset part pairs joined by the steps (from step rules)."""
    out = []
    for s in spec["steps"]:
        r = s.get("rule")
        if r:
            pair = frozenset((r["a"], r["b"]))
            if pair not in out:
                out.append(pair)
    return out


def compare(gen, ref):
    gj, rj = joins(gen), joins(ref)
    shared = [p for p in rj if p in gj]
    order_ok = [p for p in gj if p in shared] == shared
    auto = lambda s: sum(1 for x in s["steps"] if x.get("rule"))  # noqa: E731
    return {
        "generated": gen["id"], "reference": ref["id"],
        "steps": [len(gen["steps"]), len(ref["steps"])],
        "auto_checked": [auto(gen), auto(ref)],
        "join_coverage": f"{len(shared)}/{len(rj)}",
        "missing_joins": [" + ".join(sorted(p)) for p in rj if p not in gj],
        "extra_joins": [" + ".join(sorted(p)) for p in gj if p not in rj],
        "same_order": order_ok,
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("generated")
    ap.add_argument("reference")
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args()
    gen, ref = ws.load(a.generated), ws.load(a.reference)
    r = compare(gen, ref)
    if a.json:
        print(json.dumps(r, indent=1))
        return
    print(f"{gen['title']}  vs  {ref['title']}")
    print(f"  steps            generated {r['steps'][0]}   hand-written {r['steps'][1]}")
    print(f"  auto-checked     generated {r['auto_checked'][0]}   hand-written {r['auto_checked'][1]}")
    print(f"  joins covered    {r['join_coverage']}"
          + (f"   missing: {', '.join(r['missing_joins'])}" if r["missing_joins"] else ""))
    if r["extra_joins"]:
        print(f"  extra joins      {', '.join(r['extra_joins'])}")
    print(f"  same join order  {'yes' if r['same_order'] else 'NO'}")
    n = max(len(gen["steps"]), len(ref["steps"]))
    print("  side by side:")
    for i in range(n):
        g = gen["steps"][i]["instruction"] if i < len(gen["steps"]) else ""
        h = ref["steps"][i]["instruction"] if i < len(ref["steps"]) else ""
        print(f"   {i + 1}. GEN  {g}\n      HAND {h}")


if __name__ == "__main__":
    main()
