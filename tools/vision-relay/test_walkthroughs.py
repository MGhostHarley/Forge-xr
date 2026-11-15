#!/usr/bin/env python3
"""Tests for generated walkthroughs: spec normalisation/validation, relay endpoints, step judge on flow "gen".
No network, no headset:  python3 -I tools/vision-relay/test_walkthroughs.py"""
import json, pathlib, sys, tempfile, threading, unittest, urllib.request
from http.server import ThreadingHTTPServer

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import walkthrough_spec as ws  # noqa: E402
import step_check  # noqa: E402


def raw(*steps, parts=None, title="T"):
    return {"title": title, "completion": "Done", "parts": parts or [],
            "steps": [dict(instruction=i, highlight=h) for i, h in steps]}


class Normalise(unittest.TestCase):
    def test_proven_rules_for_driver_pairs(self):
        spec, _ = ws.normalise(raw(("Press the torso onto the legs.", ["torso", "legs"]),
                                   ("Put the head on.", ["head", "torso"]),
                                   ("Add the hair.", ["hairpiece", "head"])), "t-driver")
        rules = [s["rule"] for s in spec["steps"]]
        self.assertTrue(all(rules))
        self.assertEqual(rules[0]["question"], step_check.STEP_RULES[("driver", 0)]["question"])

    def test_repeat_step_folded(self):
        spec, w = ws.normalise(raw(("Attach a tire to a rim.", ["tire", "rim"]),
                                   ("Repeat four more times.", ["tire", "rim"])), "t")
        self.assertEqual(len(spec["steps"]), 1)
        self.assertIn("Repeat four more times", spec["steps"][0]["instruction"])

    def test_setup_step_folded_once(self):
        spec, _ = ws.normalise(raw(("Place the legs on the table.", ["legs"]),
                                   ("Attach the torso to the legs.", ["torso", "legs"]),
                                   ("Attach the head to the torso.", ["head", "torso"])), "t")
        self.assertEqual(len(spec["steps"]), 2)  # only the first folded, no cascade
        self.assertTrue(spec["steps"][0]["instruction"].startswith("Place the legs on the table, then attach"))

    def test_identical_steps_collapsed(self):
        spec, _ = ws.normalise(raw(*[(f"Press the wheel onto the {p} axle.", ["tire", "axle-pin"])
                                     for p in ("front left", "front right", "rear left", "rear right")]), "t")
        self.assertEqual(len(spec["steps"]), 1)
        self.assertIn("each axle", spec["steps"][0]["instruction"])
        self.assertIn("repeat 4x", spec["steps"][0]["instruction"])

    def test_pair_never_reused(self):
        spec, _ = ws.normalise(raw(("Put the rim in the tire.", ["tire", "rim"]),
                                   ("Check the rim is seated in the tire.", ["tire", "rim"])), "t")
        self.assertIsNotNone(spec["steps"][0]["rule"])
        self.assertIsNone(spec["steps"][1]["rule"])  # would complete instantly otherwise

    def test_axle_keyword_uses_axle_pin(self):
        spec, _ = ws.normalise(raw(("Put the rim in the tire.", ["tire", "rim"]),
                                   ("Press the wheel onto the axle.", ["tire", "rim"])), "t")
        r = spec["steps"][1]["rule"]
        self.assertEqual({r["a"], r["b"]}, {"tire", "axle-pin"})

    def test_no_made_up_rule_for_tuned_parts(self):
        # rim+axle-pin has no tuned rule, but both parts have tuned rules elsewhere: no guess.
        spec, _ = ws.normalise(raw(("Rim in tire.", ["tire", "rim"]), ("Wheel on axle.", ["tire", "axle-pin"]),
                                   ("Hub to pin.", ["rim", "axle-pin"])), "t")
        self.assertIsNone(spec["steps"][2]["rule"])

    def test_unknown_parts_are_text_only(self):
        spec, w = ws.normalise(raw(("Clip the 2x4 brick onto the base plate.", ["brick-2x4", "base-plate"])), "t")
        self.assertIsNone(spec["steps"][0]["rule"])
        self.assertFalse(any(p["detectable"] for p in spec["parts"]))
        self.assertEqual(ws.for_quest(spec)["steps"][0]["highlight"], [])

    def test_generic_rule_for_untuned_detectable_pair(self):
        # legs+hairpiece has no tuned rule and... legs/hairpiece do have tuned rules -> still no guess.
        spec, _ = ws.normalise(raw(("Odd join.", ["legs", "hairpiece"])), "t")
        self.assertIsNone(spec["steps"][0]["rule"])

    def test_invalid(self):
        with self.assertRaises(ws.SpecError):
            ws.normalise(raw(), "t")
        with self.assertRaises(ws.SpecError):
            ws.normalise(raw(("x", [])), "Bad Id")
        with self.assertRaises(ws.SpecError):
            ws.validate({"id": "x", "parts": [], "steps": [{"instruction": "a", "highlight": ["nope"]}]})

    def test_committed_specs_valid(self):
        specs = ws.load_dir(HERE / "walkthroughs")
        self.assertIn("booklet-40650-driver", specs)
        self.assertIn("manual-driver", specs)


class FakeDetector:
    model = "fake"

    def __init__(self):
        self.dets = []

    def detect(self, jpeg, parts=None):
        return [d for d in self.dets if not parts or d["partId"] in parts], "[]"


def det(p, x0, y0, x1, y1):
    return {"partId": p, "x0": x0, "y0": y0, "x1": x1, "y1": y1, "confidence": 0.8}


class Relay(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import relay
        cls.relay = relay
        cls.tmp = tempfile.TemporaryDirectory()
        spec, _ = ws.normalise(raw(("Press the torso onto the legs.", ["torso", "legs"]),
                                   ("Put the head on the torso.", ["head", "torso"])), "test-gen")
        spec2, _ = ws.normalise(raw(("Clip the 2x4 brick on.", ["brick"])), "test-text")
        for s in (spec, spec2):
            (pathlib.Path(cls.tmp.name) / f"{s['id']}.json").write_text(json.dumps(s))
        cls.det = FakeDetector()
        clock = {"t": 0.0}
        cls.clock = clock
        cls.judge = step_check.StepJudge(vlm=None, limiter=None, clock=lambda: clock["t"], async_vlm=False)
        cls.wt = relay.Walkthroughs(cls.tmp.name, "test-gen", cls.judge)
        cls.srv = ThreadingHTTPServer(("127.0.0.1", 0), relay.make_handler(
            cls.det, None, relay.RateLimiter(0), 1, cls.judge, cls.wt))
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()
        cls.url = f"http://127.0.0.1:{cls.srv.server_address[1]}"

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()
        cls.tmp.cleanup()

    def get(self, path):
        return json.loads(urllib.request.urlopen(self.url + path, timeout=5).read())

    def post(self, path, data=b"", headers=None):
        req = urllib.request.Request(self.url + path, data=data, headers=headers or {}, method="POST")
        try:
            return json.loads(urllib.request.urlopen(req, timeout=5).read())
        except urllib.error.HTTPError as e:
            return json.loads(e.read())

    def test_1_walkthrough_for_quest(self):
        w = self.get("/walkthrough")
        self.assertEqual(w["id"], "test-gen")
        self.assertEqual(len(w["steps"]), 2)
        self.assertEqual(w["steps"][0]["highlight"], ["torso", "legs"])
        self.assertTrue(w["steps"][0]["auto"])
        self.assertIn("selectNonce", w)

    def test_2_listing_and_review(self):
        ids = [x["id"] for x in self.get("/walkthroughs")["walkthroughs"]]
        self.assertEqual(sorted(ids), ["test-gen", "test-text"])
        page = urllib.request.urlopen(self.url + "/walkthroughs/review", timeout=5).read().decode()
        self.assertIn("Press the torso onto the legs", page)

    def test_3_select_bumps_nonce(self):
        n0 = self.get("/walkthrough")["selectNonce"]
        r = self.post("/walkthrough?id=test-gen&select=1")
        self.assertEqual(r["selectNonce"], n0 + 1)
        self.assertEqual(self.post("/walkthrough?id=nope")["error"][:7], "unknown")

    def test_4_gen_parts_requested(self):
        self.assertEqual(sorted(self.relay.choose_parts("gen,step=1,complete=false", "", 1)), ["head", "legs", "torso"])

    def test_5_step_completes_on_generated_flow(self):
        self.post("/walkthrough?id=test-gen")
        state = "gen,step=0,complete=false"
        apart = [det("torso", 0.1, 0.1, 0.15, 0.15), det("legs", 0.6, 0.6, 0.65, 0.65)]
        joined = [det("torso", 0.40, 0.40, 0.45, 0.45), det("legs", 0.40, 0.45, 0.45, 0.50)]
        out = None
        for i, dets in enumerate([apart] * 3 + [joined] * 30):
            self.det.dets = dets
            self.clock["t"] = i * 0.2
            out = self.post("/detect", b"\xff\xd8\xff\xd9", {"X-State": state, "X-Frame-Id": str(i)})
            if out.get("stepComplete"):
                break
        self.assertTrue(out.get("stepComplete"), out)
        self.assertEqual(out["stepComplete"]["anchorPart"], "torso")
        self.assertEqual(out["stepComplete"]["flow"], "gen")

    def test_6_text_only_walkthrough_never_autocompletes(self):
        self.post("/walkthrough?id=test-text")
        self.assertEqual(self.get("/walkthrough")["steps"][0]["highlight"], [])
        self.assertEqual(self.relay.GEN_PARTS, [])
        self.post("/walkthrough?id=test-gen")

    def test_7_builtin_flows_untouched(self):
        self.assertEqual(step_check.STEP_COUNTS["driver"], 3)
        self.assertEqual(step_check.STEP_COUNTS["wheel"], 4)
        with self.assertRaises(ValueError):
            step_check.register_flow("driver", {}, 1)


if __name__ == "__main__":
    unittest.main(verbosity=1)
