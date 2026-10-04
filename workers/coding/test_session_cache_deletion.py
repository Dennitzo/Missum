import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import urllib.parse

from session_cache import NativeSessionCache


class SessionCacheDeletionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.tokens, self.busy, self.calls = {}, set(), []
        self.version = 1
        self.cache = self.reopen()

    def reopen(self):
        return NativeSessionCache(self.root, self.router, lambda _: dict(version=self.version),
                                  free_reserve=0, estimate_bytes=lambda *_: 1024)

    def router(self, path, body=None, timeout=15):
        self.calls.append((path, body))
        if path.startswith("slots?"):
            model = urllib.parse.parse_qs(path.split("?", 1)[1])["model"][0]
            return [dict(id=0, is_processing=model in self.busy, n_prompt_tokens=self.tokens.get(model, 100))]
        if "action=save" in path:
            tokens = self.tokens.get(body["model"], 100)
            (self.root / body["filename"]).write_bytes(str(tokens).encode())
            return dict(n_saved=tokens, n_written=3)
        if "action=restore" in path:
            self.tokens[body["model"]] = int((self.root / body["filename"]).read_text())
            return dict(n_restored=self.tokens[body["model"]])
        if "action=erase" in path:
            self.tokens[body["model"]] = 0
            return dict(n_erased=100)
        raise AssertionError(path)

    def snapshot(self, model, key, owner, parent=None):
        self.tokens[model] = 100
        self.assertIn(self.cache.prepare(model, key, owner, parent)["status"], ("miss", "restored"))
        self.assertEqual("saved", self.cache.save(model, key)["status"])
        return self.root / (self.cache._identity(model, key) + ".bin")

    def test_deletes_both_gpu_branches_and_preserves_unrelated_resident_session(self):
        main = self.snapshot("qwen", "parent-key", "project-session")
        child = self.snapshot("qwen@subagent", "child-key", "child-session", "project-session")
        other = self.snapshot("deepseek", "retained-key", "retained-session")
        result = self.cache.delete(["project-session"])
        self.assertEqual("deleted", result["status"])
        self.assertEqual(2, result["removedSnapshots"])
        self.assertFalse(main.exists())
        self.assertFalse(child.exists())
        self.assertTrue(other.exists())
        self.assertEqual(100, self.tokens["deepseek"])
        self.assertEqual(0, self.tokens["qwen"])
        self.assertEqual(0, self.tokens["qwen@subagent"])
        self.assertIn("deepseek", self.cache.resident)

    def test_old_model_fingerprint_and_changed_workspace_remain_owned_after_restart(self):
        first = self.snapshot("model", "workspace-before", "owner")
        self.version = 2
        second = self.snapshot("model", "workspace-after", "owner")
        other = self.snapshot("other-model", "retained", "other-owner")
        restarted = self.reopen()
        self.assertEqual(2, restarted.delete(["owner"])["removedSnapshots"])
        self.assertFalse(first.exists())
        self.assertFalse(second.exists())
        self.assertTrue(other.exists())

    def test_deleted_owner_cannot_restore_or_save_a_late_cancelled_turn_after_restart(self):
        path = self.snapshot("model", "key", "owner")
        self.cache.prepare("model", "key", "owner")
        self.cache.delete(["owner"])
        self.assertEqual("unchanged", self.cache.save("model", "key")["status"])
        restarted = self.reopen()
        result = restarted.prepare("model", "new-workspace-key", "owner")
        self.assertEqual("unavailable", result["status"])
        self.assertIn("deleted", result["detail"])
        self.assertFalse(path.exists())
        self.assertFalse(list(self.root.glob("*.bin")))

    def test_fork_records_parent_ownership_before_first_child_inference(self):
        parent = self.snapshot("qwen", "parent", "owner")
        result = self.cache.fork("qwen", "parent", "qwen@subagent", "child", session_id="child-owner", parent_session_id="owner")
        self.assertEqual("forked", result["status"])
        child = self.root / (self.cache._identity("qwen@subagent", "child") + ".bin")
        metadata = json.loads(child.with_suffix(".json").read_text())
        self.assertEqual("child-owner", metadata["sessionId"])
        self.assertEqual("owner", metadata["parentSessionId"])
        self.assertEqual(2, self.reopen().delete(["owner"])["removedSnapshots"])
        self.assertFalse(parent.exists())
        self.assertFalse(child.exists())

    def test_legacy_key_alias_and_descendant_fork_keys_are_deleted_together(self):
        current = "missum-session-v2-" + "a" * 64
        legacy = "go-session-v2-" + "a" * 64
        parent = self.snapshot("qwen", current, "owner")
        self.assertEqual("forked", self.cache.fork("qwen", legacy, "qwen@subagent", "child")["status"])
        self.assertEqual(2, self.cache.delete(session_keys=[legacy])["removedSnapshots"])
        self.assertFalse(parent.exists())
        self.assertFalse(list(self.root.glob("*.bin")))

    def test_busy_deleted_slot_cannot_resurrect_snapshot_and_retry_erases_it(self):
        path = self.snapshot("model", "key", "owner")
        self.cache.prepare("model", "key", "owner")
        self.busy.add("model")
        with patch("session_cache.time.monotonic", side_effect=[0, 3]):
            result = self.cache.delete(["owner"])
        self.assertEqual("pending", result["status"])
        self.assertTrue(self.cache.resident["model"]["deleted"])
        with patch("session_cache.time.monotonic", side_effect=[0, 3]):
            self.assertEqual("deleted", self.cache.save("model", "key")["status"])
        self.assertFalse(path.exists())
        self.busy.clear()
        self.assertEqual("deleted", self.cache.delete(["owner"])["status"])
        self.assertNotIn("model", self.cache.resident)
        self.assertEqual(0, self.tokens["model"])

    def test_repeated_deletion_is_idempotent_and_does_not_rewrite_other_snapshots(self):
        self.snapshot("model", "key", "owner")
        retained = self.snapshot("other", "other-key", "other-owner")
        before = retained.read_bytes()
        self.assertEqual(1, self.cache.delete(["owner"])["removedSnapshots"])
        self.assertEqual(0, self.cache.delete(["owner"])["removedSnapshots"])
        self.assertEqual(before, retained.read_bytes())

    def test_a_bound_key_cannot_be_reassigned_to_another_session_even_on_another_model(self):
        path = self.snapshot("model", "key", "owner")
        result = self.cache.prepare("other-model", "key", "other-owner")
        self.assertEqual("unavailable", result["status"])
        self.assertIn("different session owner", result["detail"])
        self.assertNotIn("other-model", self.cache.resident)
        self.assertEqual("owner", self.cache.owners[path.stem]["sessionId"])
        self.assertTrue(path.exists())

    def test_preledger_snapshot_can_be_deleted_by_exact_current_identity_without_directory_wipe(self):
        key = "legacy"
        path = self.root / (self.cache._identity("model", key) + ".bin")
        path.write_bytes(b"old snapshot")
        path.with_suffix(".json").write_text(json.dumps(dict(bytes=12, tokens=100)))
        unknown = self.root / ("c" * 64 + ".bin")
        unknown.write_bytes(b"unknown untouched")
        self.assertEqual(1, self.cache.delete(session_keys=[key], known_models=["model"])["removedSnapshots"])
        self.assertFalse(path.exists())
        self.assertTrue(unknown.exists())

    def test_verified_owner_only_legacy_metadata_is_deleted_without_inventing_a_session_key(self):
        legacy = self.root / ("d" * 64 + ".bin")
        retained = self.root / ("e" * 64 + ".bin")
        legacy.write_bytes(b"legacy fingerprint and key no longer reconstructable")
        retained.write_bytes(b"another session")
        legacy.with_suffix(".json").write_text(json.dumps(dict(sessionId="legacy-owner", bytes=48, tokens=100)))
        retained.with_suffix(".json").write_text(json.dumps(dict(sessionId="retained-owner", bytes=15, tokens=100)))
        result = self.reopen().delete(["legacy-owner"])
        self.assertEqual("deleted", result["status"])
        self.assertEqual(1, result["removedSnapshots"])
        self.assertFalse(legacy.exists())
        self.assertTrue(retained.exists())
        self.assertEqual([], result["sessionKeys"])
        reopened = self.reopen()
        self.assertEqual("legacy-owner", reopened.owners[legacy.stem]["sessionId"])
        self.assertNotIn("sessionKey", reopened.owners[legacy.stem])

    def test_invalid_owner_ledger_disables_cache_reuse_without_removing_files(self):
        path = self.snapshot("model", "key", "owner")
        (self.root / "ownership.json").write_text("broken")
        restarted = self.reopen()
        self.assertEqual("unavailable", restarted.prepare("model", "key", "owner")["status"])
        with self.assertRaisesRegex(RuntimeError, "ownership"):
            restarted.delete(["owner"])
        self.assertTrue(path.exists())

    def test_no_owner_requests_and_nonlists_are_rejected_before_any_deletion(self):
        path = self.snapshot("model", "key", "owner")
        for request in (([], []), ("owner", []), ([None], [])):
            with self.subTest(request=request), self.assertRaises(ValueError):
                self.cache.delete(*request)
        self.assertTrue(path.exists())


if __name__ == "__main__":
    unittest.main()
