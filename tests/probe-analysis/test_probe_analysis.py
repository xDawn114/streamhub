import importlib.util
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("probe", Path(__file__).resolve().parents[2] / "scripts/analyze_probe.py")
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class ProbeAnalysisTests(unittest.TestCase):
    def test_midnight_and_live_encoder_are_separate(self):
        result = probe.analyze("""[2026-11-15 23:59:59.800]: begin stage=startup-encoder-probe
[2026-11-15 23:59:59.900]: Creating encoder [hevc_qsv]
[2026-11-16 00:00:00.100]: Streaming bitrate is 1000000
[2026-11-16 00:00:00.150]: end stage=startup-encoder-probe elapsed_ms=350
[2026-11-16 00:00:01.000]: Creating encoder [hevc_qsv]
[2026-11-16 00:00:02.000]: Streaming bitrate is 38000000""")
        self.assertEqual(result["probes"][0]["encoders"], [dict(codec="hevc_qsv", creationWindowMs=200)])

    def test_failed_codec_and_truncation_do_not_invent_zeroes(self):
        result = probe.analyze("""[2026-11-16 01:00:00.000]: begin stage=startup-encoder-probe
[2026-11-16 01:00:00.100]: Creating encoder [h264_nvenc]
[2026-11-16 01:00:00.200]: Creating encoder [hevc_qsv]
[2026-11-16 01:00:00.800]: Streaming bitrate is 1000000""")
        self.assertFalse(result["probes"][0]["complete"])
        self.assertEqual([row["creationWindowMs"] for row in result["probes"][0]["encoders"]], [None, 600])

    def test_startup_and_cached_launch_are_independent(self):
        result = probe.analyze("""[2026-11-16 01:00:00.000]: begin stage=startup-encoder-probe
[2026-11-16 01:00:01.000]: end stage=startup-encoder-probe elapsed_ms=1000
[2026-11-16 01:00:02.000]: begin stage=launch-encoder-probe
[2026-11-16 01:00:02.001]: encoder-probe=reused
[2026-11-16 01:00:02.002]: end stage=launch-encoder-probe elapsed_ms=2""")
        self.assertEqual([p["elapsedMs"] for p in result["probes"]], [1000, 2])
        self.assertTrue(result["probes"][1]["cacheReused"])
        self.assertEqual(result["probes"][1]["encoders"], [])


if __name__ == "__main__":
    unittest.main()
