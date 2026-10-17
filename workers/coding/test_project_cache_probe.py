import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from verify_project_context_cache import ProjectCacheProbe, check_cache_headroom, model_state


class ProjectCacheProbeTests(unittest.TestCase):
    def test_busy_gate_is_read_only_and_never_loads_a_model(self):
        report = dict(requests=[], scenarios=[])
        probe = ProjectCacheProbe("http://unused", "http://unused-control", "model", 1, report)
        calls = []

        def request(path, body=None, control=False):
            calls.append((path, body, control))
            return dict(data=[dict(id="model", status=dict(value="loaded"))]) if path == "v1/models" else [dict(is_processing=True)]

        probe.request = request
        with self.assertRaisesRegex(RuntimeError, "busy"):
            probe.assert_idle()
        self.assertTrue(calls)
        self.assertTrue(all(body is None and not control for _, body, control in calls))

    def test_forked_preparation_does_not_replace_measured_cached_tokens(self):
        report = dict(requests=[], scenarios=[], contextLimit=8192)
        probe = ProjectCacheProbe("http://unused", "http://unused-control", "model", 1, report)
        probe.assert_idle = lambda: None

        def request(path, body=None, control=False):
            if path == "sessions/project-prepare":
                return dict(status="forked", projectPrefixTokens=101)
            if path == "v1/chat/completions":
                return dict(usage=dict(prompt_tokens=150, prompt_tokens_details=dict(cached_tokens=12)),
                    choices=[dict(message=dict(role="assistant", content="ALPHA"))])
            self.fail("Unexpected mutation: " + path)

        probe.request = request
        with self.assertRaisesRegex(AssertionError, "actual cached_tokens=12"):
            probe.turn("new branch", "unique-session", dict(messages=[]), 100, prepare_body=dict())
        self.assertFalse(report["scenarios"][0]["passed"])

    def test_headroom_refuses_to_risk_eviction_of_user_snapshots(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            original = root / "existing.bin"
            original.write_bytes(b"preserved")
            usage = type("Usage", (), dict(free=2 * 1024**3))()
            with patch("verify_project_context_cache.shutil.disk_usage", return_value=usage):
                with self.assertRaisesRegex(RuntimeError, "headroom"):
                    check_cache_headroom(root, 1)
            self.assertEqual(b"preserved", original.read_bytes())

    def test_model_state_accepts_both_native_status_shapes(self):
        self.assertEqual("loaded", model_state(dict(status=dict(value="loaded"))))
        self.assertEqual("sleeping", model_state(dict(status="sleeping")))


if __name__ == "__main__":
    unittest.main()
