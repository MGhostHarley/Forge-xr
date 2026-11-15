#!/usr/bin/env python3
"""Unit tests for step_check.py (synthetic boxes, mocked VLM; no network, light CPU).

  python3 -I tools/vision-relay/test_step_check.py
"""
import io, pathlib, sys, unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import step_check as sc  # noqa: E402

DRIVER0 = "driver,step=0,complete=false"


def det(part, x0, y0, x1, y1):
    return {"partId": part, "x0": x0, "y0": y0, "x1": x1, "y1": y1, "confidence": 0.8}


APART = [det("legs", 0.30, 0.50, 0.33, 0.53), det("torso", 0.60, 0.50, 0.63, 0.53)]
TOGETHER = [det("legs", 0.40, 0.50, 0.43, 0.53), det("torso", 0.40, 0.47, 0.43, 0.505)]


def tiny_jpeg():
    from PIL import Image
    buf = io.BytesIO()
    Image.new("RGB", (64, 64), "gray").save(buf, "JPEG")
    return buf.getvalue()


class FakeVlm:
    def __init__(self, answers):
        self.answers, self.calls = list(answers), 0

    def ask(self, jpeg, question):
        self.calls += 1
        a = self.answers.pop(0) if self.answers else False
        if isinstance(a, Exception):
            raise a
        return a


class FakeLimiter:
    def __init__(self, ok=True):
        self.ok, self.noted, self.cooldown_seconds = ok, 0, 20.0

    def try_acquire(self):
        return self.ok

    def note_rate_limited(self):
        self.noted += 1


def judge(vlm=None, limiter=None, **cfg):
    j = sc.StepJudge(vlm=vlm, limiter=limiter, config_path=None, async_vlm=False, log=lambda m: None)
    j._cfg.update(cfg)
    j.cfg = lambda: j._cfg  # no file
    return j


def feed(j, seq, state=DRIVER0, t0=0.0, dt=0.2, jpeg=None):
    """Feed a sequence of answers 0.2 s apart; returns (index, payload) of the first stepComplete or (None, None)."""
    jpeg = jpeg or tiny_jpeg()
    for i, dets in enumerate(seq):
        out = j.observe(state, 100 + i, jpeg, dets, t=t0 + i * dt)
        if out:
            return i, out
    return None, None


class Geometry(unittest.TestCase):
    def test_parse_state(self):
        self.assertEqual(sc.parse_state(DRIVER0), ("driver", 0, False))
        self.assertEqual(sc.parse_state("wheel,step=3,complete=true"), ("wheel", 3, True))
        self.assertIsNone(sc.parse_state(""))
        self.assertIsNone(sc.parse_state("car,step=1"))

    def test_touch_any_direction(self):
        cfg = dict(sc.DEFAULTS)
        rule = sc.STEP_RULES[("driver", 0)]
        side = [det("legs", 0.40, 0.50, 0.43, 0.53), det("torso", 0.435, 0.50, 0.465, 0.53)]  # left/right, small gap
        self.assertTrue(sc.evaluate(rule, side, cfg, {}, 0)[0])
        self.assertTrue(sc.evaluate(rule, TOGETHER, cfg, {}, 0)[0])
        hit, apart, _ = sc.evaluate(rule, APART, cfg, {}, 0)
        self.assertFalse(hit)
        self.assertTrue(apart)

    def test_merge_one_part_vanishes(self):
        cfg = dict(sc.DEFAULTS)
        rule = sc.STEP_RULES[("driver", 0)]
        mem = {}
        self.assertTrue(sc.evaluate(rule, TOGETHER, cfg, mem, 0)[0])
        only_torso = [TOGETHER[1]]
        self.assertTrue(sc.evaluate(rule, only_torso, cfg, mem, 1.0)[0])
        self.assertFalse(sc.evaluate(rule, only_torso, cfg, mem, 10.0)[0])  # too long ago
        self.assertFalse(sc.evaluate(rule, [det("torso", 0.8, 0.1, 0.83, 0.13)], cfg, mem, 1.0)[0])  # elsewhere

    def test_rim_inside_tire(self):
        cfg = dict(sc.DEFAULTS)
        rule = sc.STEP_RULES[("wheel", 1)]
        inside = [det("tire", 0.4, 0.4, 0.5, 0.5), det("rim", 0.43, 0.43, 0.47, 0.47)]
        beside = [det("tire", 0.4, 0.4, 0.5, 0.5), det("rim", 0.52, 0.43, 0.56, 0.47)]
        self.assertTrue(sc.evaluate(rule, inside, cfg, {}, 0)[0])
        self.assertFalse(sc.evaluate(rule, beside, cfg, {}, 0)[0])

    def test_rules(self):
        self.assertEqual(sc.rule_for("wheel", 0)[1], 2)  # wheel 0 completes 0 and 1 together
        self.assertEqual(sc.rule_for("driver", 1)[1], 1)
        self.assertEqual(sc.rule_for("wheel", 3), (None, 0))  # spin check: manual


class Judge(unittest.TestCase):
    def test_vlm_yes_confirms_after_hold(self):
        vlm = FakeVlm([True])
        j = judge(vlm, FakeLimiter())
        i, out = feed(j, [APART] * 3 + TOGETHER_SEQ(10))
        self.assertIsNotNone(out)
        self.assertEqual(out["via"], "vlm")
        self.assertEqual(out["anchorPart"], "torso")
        self.assertEqual(out["advance"], 1)
        self.assertFalse(out["final"])
        self.assertEqual(vlm.calls, 1)
        # Needs ~1 s of candidate: hits start at index 3; candidate from 3, held >= 1.0 s at index 8.
        self.assertGreaterEqual(i, 8)
        # Repeated for the same state, gone for another.
        self.assertEqual(j.observe(DRIVER0, 999, b"", [], t=10)["step"], 0)
        self.assertEqual(j.current(DRIVER0)["step"], 0)
        self.assertIsNone(j.observe("driver,step=1,complete=false", 1000, b"", [], t=11))
        self.assertIsNone(j.current(DRIVER0))

    def test_vlm_no_blocks_fallback_and_respects_budget(self):
        vlm = FakeVlm([False, False, False, False])
        j = judge(vlm, FakeLimiter())
        _, out = feed(j, [APART] * 3 + TOGETHER_SEQ(150))  # 30 s of "together"
        self.assertIsNone(out)
        self.assertEqual(vlm.calls, 3)  # vlm_max_calls

    def test_vlm_gap(self):
        vlm = FakeVlm([False, True])
        j = judge(vlm, FakeLimiter())
        i, out = feed(j, [APART] * 3 + TOGETHER_SEQ(60))
        self.assertEqual(out["via"], "vlm")
        self.assertEqual(vlm.calls, 2)
        self.assertGreaterEqual(i * 0.2 - 0.6, 1.0 + 4.0 - 0.01)  # second call >= 4 s after the first

    def test_fallback_without_vlm(self):
        j = judge(None, None)
        i, out = feed(j, [APART] * 3 + TOGETHER_SEQ(30))
        self.assertEqual(out["via"], "geometry")
        self.assertGreaterEqual((i - 3) * 0.2, 3.0 - 1e-6)

    def test_fallback_needs_seen_apart(self):
        j = judge(None, None)
        _, out = feed(j, TOGETHER_SEQ(40))
        self.assertIsNone(out)
        j2 = judge(None, None, fallback_require_apart=False)
        _, out2 = feed(j2, TOGETHER_SEQ(40))
        self.assertEqual(out2["via"], "geometry")

    def test_fallback_when_throttled(self):
        j = judge(FakeVlm([True]), FakeLimiter(ok=False))
        _, out = feed(j, [APART] * 3 + TOGETHER_SEQ(30))
        self.assertEqual(out["via"], "geometry")

    def test_vlm_429_cooldown_then_fallback(self):
        lim = FakeLimiter()
        vlm = FakeVlm([RuntimeError("NVIDIA HTTP 429: too many")])
        j = judge(vlm, lim)
        _, out = feed(j, [APART] * 3 + TOGETHER_SEQ(30))
        self.assertEqual(lim.noted, 1)
        self.assertEqual(out["via"], "geometry")
        self.assertEqual(j.vlm_calls, 0)  # a 429 doesn't spend the budget

    def test_flicker_is_not_a_candidate(self):
        j = judge(None, None)
        _, out = feed(j, [APART] * 2 + [TOGETHER, APART, APART] * 20)  # 1 in 3 answers touching
        self.assertIsNone(out)

    def test_state_change_resets(self):
        j = judge(None, None)
        feed(j, [APART] * 3 + TOGETHER_SEQ(10))
        _, out = feed(j, TOGETHER_SEQ(10), state="driver,step=1,complete=false", t0=5)
        self.assertIsNone(out)

    def test_manual_and_complete_states(self):
        j = judge(None, None, fallback_require_apart=False)
        self.assertIsNone(feed(j, TOGETHER_SEQ(40), state="wheel,step=3,complete=false")[1])
        self.assertIsNone(feed(j, TOGETHER_SEQ(40), state="driver,step=3,complete=true")[1])
        self.assertIsNone(feed(j, TOGETHER_SEQ(40), state="")[1])

    def test_wheel0_completes_two_steps(self):
        inside = [det("tire", 0.4, 0.4, 0.5, 0.5), det("rim", 0.43, 0.43, 0.47, 0.47)]
        apart = [det("tire", 0.4, 0.4, 0.5, 0.5), det("rim", 0.7, 0.43, 0.74, 0.47)]
        j = judge(FakeVlm([True]), FakeLimiter())
        _, out = feed(j, [apart] * 3 + [inside] * 10, state="wheel,step=0,complete=false")
        self.assertEqual(out["advance"], 2)
        self.assertEqual(out["anchorPart"], "tire")
        self.assertFalse(out["final"])

    def test_final_step_flag(self):
        hair = [det("head", 0.4, 0.4, 0.43, 0.43), det("hairpiece", 0.4, 0.37, 0.43, 0.405)]
        j = judge(FakeVlm([True]), FakeLimiter())
        _, out = feed(j, [hair] * 10, state="driver,step=2,complete=false")
        self.assertTrue(out["final"])

    def test_disabled(self):
        j = judge(FakeVlm([True]), FakeLimiter(), enabled=False)
        self.assertIsNone(feed(j, [APART] * 3 + TOGETHER_SEQ(30))[1])

    def test_crop(self):
        from PIL import Image
        crop = sc.crop_jpeg(tiny_jpeg(), (0.4, 0.4, 0.45, 0.45), dict(sc.DEFAULTS))
        self.assertEqual(max(Image.open(io.BytesIO(crop)).size), 512)


def TOGETHER_SEQ(n):  # noqa: N802
    return [TOGETHER] * n


if __name__ == "__main__":
    unittest.main(verbosity=1)
