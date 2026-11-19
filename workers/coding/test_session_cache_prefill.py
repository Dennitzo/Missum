import struct
import tempfile
import threading
import unittest
import http.client
import json
from pathlib import Path
from unittest.mock import patch

from catalog import GpuLoadManager, start_gpu_control
from session_cache import NativeSessionCache, PreparationCancelledError


class PrefillTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.tokens = []
        self.prompt = list(range(1, 15))
        self.calls = []
        self.cancel_after = None
        self.completions = 0
        self.slot_revision = None
        self.cache = NativeSessionCache(self.root, self.router, lambda _: "fingerprint", free_reserve=0,
                                        estimate_bytes=lambda *_: 1024, prepare_completion=self.completion)
        self.body = dict(model="model", messages=[dict(role="user", content="text")],
                         tools=[dict(type="function", function=dict(name="tool"))], reasoning_effort="high")
        self.cache.prepare("model", "key")
        self.patch = patch("session_cache.PREFILL_CHECKPOINT_TOKENS", 4)
        self.patch.start()
        self.addCleanup(self.patch.stop)

    def router(self, path, body=None, timeout=15):
        self.calls.append((path, body))
        if path.startswith("slots?"):
            return [dict(id=0, is_processing=False, n_prompt_tokens=len(self.tokens),
                         missum_prefix_revision=self.slot_revision)]
        if path == "apply-template":
            self.assertEqual(self.body, body)
            return dict(prompt="rendered")
        if path == "tokenize":
            self.assertTrue(body["add_special"] and body["parse_special"])
            return dict(tokens=self.prompt.copy())
        if "action=save" in path:
            vector = self.tokens
            encoded = struct.pack("<III", 0x67677371, 3, len(vector))
            encoded += struct.pack("<" + "i" * len(vector), *vector) + b"KV"
            (self.root / body["filename"]).write_bytes(encoded)
            return dict(n_saved=len(vector), missum_prefix_revision=self.slot_revision)
        if "action=restore" in path:
            self.tokens = NativeSessionCache._snapshot_tokens(self.root / body["filename"])
            return dict(n_restored=len(self.tokens))
        raise AssertionError(path)

    def completion(self, body, cancelled):
        self.completions += 1
        if self.cancel_after is not None and self.completions > self.cancel_after:
            raise PreparationCancelledError("cancelled")
        prefix = body["prompt"]
        reused = 0
        for a, b in zip(prefix, self.tokens):
            if a != b:
                break
            reused += 1
        self.tokens = prefix.copy()
        return dict(tokens_predicted=1, timings=dict(cache_n=reused, prompt_n=len(prefix) - reused))

    def test_prefix_snapshots_preserve_completed_and_restore_matching_partial(self):
        self.tokens = [1, 2]
        self.cache.save("model", "key")
        full = self.root / (self.cache._identity("model", "key") + ".bin")
        before = full.read_bytes()
        result = self.cache.prefill("model", "key", self.body)
        self.assertEqual(("prepared", 2, 14), (result["status"], result["checkpoints"], result["promptTokens"]))
        self.assertTrue(result["started"])
        self.assertEqual((2, 8, 10), (result["cachedTokens"], result["evaluatedTokens"], result["checkpointTokens"]))
        self.assertEqual(before, full.read_bytes())
        partial = self.root / (self.cache._identity("model", "key") + ".prefill.bin")
        self.assertEqual(self.prompt[:10], NativeSessionCache._snapshot_tokens(partial))
        # Recreate the manager and ownership ledger, as after a restart.
        self.cache = NativeSessionCache(self.root, self.router, lambda _: "fingerprint", free_reserve=0,
                                        estimate_bytes=lambda *_: 1024, prepare_completion=self.completion)
        self.tokens = []
        self.cache.prepare("model", "key")
        self.calls.clear()
        resumed = self.cache.prefill("model", "key", self.body)
        self.assertEqual("skipped", resumed["status"])
        self.assertTrue(resumed["started"])
        self.assertTrue(any("action=restore" in path and body["filename"].endswith(".prefill.bin")
                            for path, body in self.calls))
        self.assertEqual(before, full.read_bytes())
        self.assertEqual("incompletePrefill", self.cache.save("model", "key")["reason"])
        self.assertEqual(before, full.read_bytes())
        self.assertEqual("saved", self.cache.save("model", "key", new_tokens=1)["status"])
        self.assertFalse(partial.exists())

    def test_changed_resident_prefix_is_compared_by_tokens_not_length(self):
        self.tokens = [999] * len(self.prompt)
        result = self.cache.prefill("model", "key", self.body)
        self.assertEqual("prepared", result["status"])
        self.assertEqual(3, result["checkpoints"])

    def test_verified_regular_save_avoids_comparison_snapshot_and_preserves_checkpoint(self):
        self.tokens = self.prompt[:-2]
        self.slot_revision = "child-1:save-1"
        self.assertEqual("saved", self.cache.save("model", "key")["status"])
        full = self.root / (self.cache._identity("model", "key") + ".bin")
        before = full.read_bytes()
        self.calls.clear()

        self.assertEqual("shortPrefill", self.cache.prefill("model", "key", self.body)["reason"])
        self.assertFalse(any("action=save" in path for path, _ in self.calls))
        self.assertEqual(0, self.completions)
        self.assertEqual(before, full.read_bytes())

    def test_foreign_use_even_at_same_length_requires_native_snapshot(self):
        self.tokens = self.prompt[:-2]
        self.slot_revision = "child-1:save-1"
        self.cache.save("model", "key")
        self.slot_revision = "child-1:restore"
        self.tokens = [999] * len(self.tokens)
        self.calls.clear()

        result = self.cache.prefill("model", "key", self.body)
        self.assertEqual(0, result["cachedTokens"])
        self.assertTrue(any("action=save" in path for path, _ in self.calls))

    def test_missing_revision_after_restart_uses_full_comparison(self):
        self.tokens = self.prompt[:-2]
        self.slot_revision = "child-1:save-1"
        self.cache.save("model", "key")
        self.slot_revision = None
        self.calls.clear()
        self.assertEqual("shortPrefill", self.cache.prefill("model", "key", self.body)["reason"])
        self.assertTrue(any("action=save" in path for path, _ in self.calls))

    def test_identical_long_resident_is_not_reprocessed(self):
        self.tokens = self.prompt.copy()
        self.assertEqual("shortPrefill", self.cache.prefill("model", "key", self.body)["reason"])
        self.assertEqual(0, self.completions)

    def test_invalid_snapshot_never_replaces_completed_state(self):
        self.tokens = [1, 2]
        self.cache.save("model", "key")
        full = self.root / (self.cache._identity("model", "key") + ".bin")
        old = full.read_bytes()
        original = self.completion
        def corrupted(body, cancelled):
            result = original(body, cancelled)
            self.tokens[-1] = 999
            return result
        self.cache.prepare_completion = corrupted
        result = self.cache.prefill("model", "key", self.body)
        self.assertEqual(("unavailable", 2, 4, 0), (result["status"], result["cachedTokens"],
                         result["evaluatedTokens"], result["checkpointTokens"]))
        self.assertEqual(old, full.read_bytes())
        self.assertFalse((self.root / (self.cache._identity("model", "key") + ".prefill.bin")).exists())

    def test_cancel_keeps_full_and_last_atomic_partial(self):
        self.tokens = [1, 2]
        self.cache.save("model", "key")
        full = self.root / (self.cache._identity("model", "key") + ".bin")
        old = full.read_bytes()
        self.cancel_after = 1
        result = self.cache.prefill("model", "key", self.body)
        self.assertEqual("cancelled", result["status"])
        self.assertEqual(2, result["cachedTokens"])
        self.assertIsNone(result["evaluatedTokens"])
        self.assertEqual("incompletePrefill", self.cache.save("model", "key")["reason"])
        self.assertEqual(old, full.read_bytes())
        partial = self.root / (self.cache._identity("model", "key") + ".prefill.bin")
        self.assertEqual(self.prompt[:6], NativeSessionCache._snapshot_tokens(partial))

    def test_media_and_missing_ownership_do_not_touch_slot(self):
        image = dict(model="model", messages=[dict(role="user", content=[dict(type="image_url", image_url={})])])
        self.calls.clear()
        skipped = self.cache.prefill("model", "key", image)
        self.assertEqual("skipped", skipped["status"])
        self.assertFalse(skipped["started"])
        self.assertEqual("unownedSession", self.cache.prefill("model", "other", self.body)["reason"])
        self.assertEqual([], self.calls)

    def test_deletion_removes_partial(self):
        self.cache.prefill("model", "key", self.body)
        partial = self.root / (self.cache._identity("model", "key") + ".prefill.bin")
        self.assertTrue(partial.exists())
        # The fake router only needs the slot erase action for this deletion.
        self.tokens = []
        old_router = self.cache.router
        self.cache.router = lambda path, body=None, timeout=15: ({"success": True} if "action=erase" in path else old_router(path, body, timeout))
        self.cache.delete(session_keys=["key"])
        self.assertFalse(partial.exists())

    def test_pending_delete_clears_protected_prefill_on_next_save(self):
        self.cache.prefill("model", "key", self.body)
        old_router = self.cache.router
        self.cache.router = lambda path, body=None, timeout=15: ({"success": True} if "action=erase" in path else old_router(path, body, timeout))
        with patch.object(self.cache, "_idle_slot", side_effect=[RuntimeError("busy"), {}]):
            result = self.cache.delete(session_keys=["key"])
            self.assertEqual("pending", result["status"])
            self.assertEqual("deleted", self.cache.save("model", "key")["status"])
        self.assertNotIn("model", self.cache.resident)


class BarrierTests(unittest.TestCase):
    def test_barrier_acquires_manager_lock_and_waits_for_idle(self):
        manager = GpuLoadManager.__new__(GpuLoadManager)
        manager.lock = threading.Lock()
        class Sessions:
            def _idle_slot(inner, model, timeout):
                self.assertTrue(manager.lock.locked())
                self.assertEqual("model", model)
                self.assertGreaterEqual(timeout, 60)
        manager.sessions = Sessions()
        self.assertEqual({"status": "idle"}, manager.session_barrier("model"))

    def test_control_endpoints_forward_request_and_barrier(self):
        class Manager:
            def session_prefill(self, model, key, body, cancelled):
                self.body = body
                self.cancelled = cancelled()
                return dict(status="prepared", promptTokens=9, checkpointTokens=4,
                            cachedTokens=1, evaluatedTokens=3, checkpoints=1)
            def session_barrier(self, model):
                return dict(status="idle")
        manager = Manager()
        server = start_gpu_control(manager, "127.0.0.1", 0)
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        connection = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=3)
        self.addCleanup(connection.close)
        body = dict(model="model", sessionKey="key", requestBody=dict(model="model", messages=[]))
        connection.request("POST", "/sessions/prefill", json.dumps(body), {"Content-Type": "application/json"})
        response = connection.getresponse()
        self.assertEqual(200, response.status)
        self.assertEqual("prepared", json.loads(response.read())["status"])
        self.assertEqual(body["requestBody"], manager.body)
        self.assertFalse(manager.cancelled)
        connection.request("POST", "/sessions/barrier", json.dumps(dict(model="model")), {"Content-Type": "application/json"})
        response = connection.getresponse()
        self.assertEqual("idle", json.loads(response.read())["status"])


if __name__ == "__main__":
    unittest.main()
