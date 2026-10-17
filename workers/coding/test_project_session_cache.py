import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

from session_cache import NativeSessionCache, canonical_project_token_prefix


class ProjectSessionCacheTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.tokens = []
        self.calls = []
        self.version = 1
        self.busy = False
        self.rewrite = False
        self.legacy_tokens = False
        self.cache = NativeSessionCache(self.root, self.router, lambda _: {"version": self.version})
        self.prefill = dict(messages=[dict(role="system", content="policy"), dict(role="user", content="project")])
        self.inference = dict(messages=[*self.prefill["messages"], dict(role="user", content="new task")])

    def router(self, path, body=None, timeout=15):
        self.calls.append((path, body))
        if path.startswith("slots?"):
            return [dict(id=0, is_processing=self.busy, n_prompt_tokens=len(self.tokens))]
        if path == "apply-template":
            if len(body["messages"]) > 2:
                return dict(prompt="actual")
            return dict(prompt="generation" if body.get("add_generation_prompt") else "prefix")
        if path == "tokenize":
            vector = list(range(1, 11))
            if body["content"] != "prefix":
                vector += [90, 91]
                if self.rewrite and body["content"] == "actual":
                    vector[0] = 999
            return dict(tokens=vector)
        if path == "completion":
            self.tokens = body["prompt"].copy()
            return dict(timings=dict(prompt_n=len(self.tokens), cache_n=0), tokens_predicted=1)
        if "action=save" in path:
            vector = self.tokens
            encoded = (struct.pack("<III", 0x67677371, 3, len(vector)) if self.legacy_tokens else
                       struct.pack("<IIIiII", 0x67677371, 3, len(vector) + 4, -1, 1, len(vector)))
            encoded += struct.pack("<" + "i" * len(vector), *vector) + b"mock-kv"
            (self.root / body["filename"]).write_bytes(encoded)
            return dict(n_saved=len(vector), n_written=len(encoded))
        if "action=restore" in path:
            self.tokens = NativeSessionCache._snapshot_tokens(self.root / body["filename"])
            return dict(n_restored=len(self.tokens))
        if "action=erase" in path:
            self.tokens = []
            return dict(success=True)
        raise AssertionError(path)

    def prepare(self, session, project="project-a", epoch="epoch-a", cancelled=None):
        return self.cache.prepare_project("model", session, project, epoch, "a" * 64,
            self.prefill, self.inference, session_id=session, cancelled=cancelled)

    def test_new_sessions_share_one_exact_base_but_keep_independent_dialogue(self):
        first = self.prepare("session-a")
        self.assertEqual("forked", first["status"])
        self.assertEqual(10, first["projectPrefixTokens"])
        self.tokens += [101, 102, 103]
        self.cache.prepare("model", "session-a")
        self.cache.save("model", "session-a")
        self.assertEqual("forked", self.prepare("session-b")["status"])
        self.assertEqual(list(range(1, 11)), self.tokens)
        self.assertEqual(1, sum(path == "completion" for path, _ in self.calls))
        self.tokens += [201]
        self.cache.prepare("model", "session-b")
        self.assertEqual("restored", self.prepare("session-a")["status"])
        self.assertEqual([*range(1, 11), 101, 102, 103], self.tokens)
        self.assertEqual(1, sum(entry.get("kind") == "projectBase" for entry in self.cache.owners.values()))

    def test_unrelated_projects_and_model_fingerprints_cannot_reuse_a_base(self):
        self.assertEqual("forked", self.prepare("session-a")["status"])
        self.assertEqual("forked", self.prepare("session-b", project="another-project")["status"])
        self.version = 2
        self.cache.invalidate("model")
        self.tokens = []
        self.assertEqual("forked", self.prepare("session-c")["status"])
        self.assertEqual(3, sum(path == "completion" for path, _ in self.calls))

    def test_native_token_mismatch_never_prepares_or_copies_a_prefix(self):
        self.rewrite = True
        result = self.prepare("session-a")
        self.assertEqual("unavailable", result["status"])
        self.assertIn("actual inference", result["detail"])
        self.assertFalse(list(self.root.glob("*.bin")))
        self.assertFalse(any(path == "completion" for path, _ in self.calls))

    def test_only_template_framing_can_be_clipped_after_complete_project_content(self):
        content = "All equations and final number 47.\n\nMISSUM_SHARED_PROJECT_CONTEXT_END_V1\n"
        rendered = "SYSTEM\n" + content
        shared = canonical_project_token_prefix([1, 2, 3, 4, 5], [1, 2, 3, 40, 50, 60], rendered,
            rendered + "\nTASK", content, lambda _: "SYSTEM\nAll equations and final number 47.\n\nMISSUM_SHARED_PROJECT_CONTEXT_END_V1")
        self.assertEqual([1, 2, 3], shared)

    def test_clipping_that_loses_the_last_project_character_is_rejected(self):
        content = "Complete physics expression x squared.\n\nMISSUM_SHARED_PROJECT_CONTEXT_END_V1\n"
        rendered = "SYSTEM\n" + content
        with self.assertRaisesRegex(ValueError, "omit content"):
            canonical_project_token_prefix([1, 2, 3], [1, 2, 40, 50], rendered, rendered + "TASK", content,
                lambda _: "SYSTEM\nComplete physics expression x squared")

    def test_changed_tool_prefix_cannot_be_hidden_by_clipping(self):
        content = "Complete shared knowledge\n\nMISSUM_SHARED_PROJECT_CONTEXT_END_V1\n"
        with self.assertRaisesRegex(ValueError, "omit content|change tools"):
            canonical_project_token_prefix([1, 2, 3], [1, 2, 40, 50], "TOOLS_A\n" + content,
                "TOOLS_B\n" + content + "TASK", content, lambda _: "TOOLS_A\n" + content)

    def test_cancelled_preparation_does_not_create_a_branch(self):
        result = self.prepare("session-a", cancelled=lambda: True)
        self.assertEqual("unavailable", result["status"])
        self.assertFalse(list(self.root.glob("*.bin")))

    def test_legacy_plain_native_token_vectors_also_seed_exact_prefixes(self):
        self.legacy_tokens = True
        self.assertEqual("forked", self.prepare("session-a")["status"])
        self.assertEqual(list(range(1, 11)), self.tokens)
        self.assertEqual("forked", self.prepare("session-b")["status"])
        self.assertEqual(1, sum(path == "completion" for path, _ in self.calls))

    def test_truncated_immutable_base_is_rebuilt_without_changing_a_saved_session(self):
        self.assertEqual("forked", self.prepare("session-a")["status"])
        self.tokens += [111]
        self.cache.prepare("model", "session-a")
        self.cache.save("model", "session-a")
        original = self.root / (self.cache._identity("model", "session-a") + ".bin")
        original_bytes = original.read_bytes()
        base_owner = next(identity for identity, owner in self.cache.owners.items() if owner.get("kind") == "projectBase")
        (self.root / (base_owner + ".bin")).write_bytes(b"truncated")
        self.assertEqual("forked", self.prepare("session-b")["status"])
        self.assertEqual(original_bytes, original.read_bytes())
        self.assertEqual(2, sum(path == "completion" for path, _ in self.calls))

    def test_periodic_save_requires_both_elapsed_time_and_new_token_budget(self):
        with patch("session_cache.time.monotonic", return_value=0):
            self.cache.prepare("model", "session-a")
        self.tokens = [1, 2, 3]
        with patch("session_cache.time.monotonic", return_value=119):
            self.assertEqual("deferred", self.cache.save("model", "session-a", force=False, new_tokens=8192)["status"])
        self.assertFalse(list(self.root.glob("*.bin")))
        with patch("session_cache.time.monotonic", return_value=120):
            self.assertEqual("saved", self.cache.save("model", "session-a", force=False, new_tokens=0)["status"])
        self.cache.prepare("model", "session-a")
        with patch("session_cache.time.monotonic", return_value=500):
            self.assertEqual("deferred", self.cache.save("model", "session-a", force=False, new_tokens=8191)["status"])
            self.assertEqual("saved", self.cache.save("model", "session-a", force=False, new_tokens=1)["status"])

    def test_forced_stop_or_handoff_saves_before_the_periodic_threshold(self):
        self.cache.prepare("model", "session-a")
        self.tokens = [1, 2, 3]
        self.assertEqual("deferred", self.cache.save("model", "session-a", force=False, new_tokens=3)["status"])
        self.assertEqual("saved", self.cache.save("model", "session-a", force=True)["status"])
        self.tokens += [4]
        self.cache.prepare("model", "session-a")
        self.cache.prepare("model", "session-b")
        path = self.root / (self.cache._identity("model", "session-a") + ".bin")
        self.assertEqual([1, 2, 3, 4], self.cache._snapshot_tokens(path))

    def test_epoch_reset_is_idle_only_idempotent_and_keeps_session_ids_reusable(self):
        self.assertEqual("forked", self.prepare("retained-chat")["status"])
        self.busy = True
        self.assertEqual("busy", self.cache.reset_epoch("epoch-new")["status"])
        self.assertTrue(list(self.root.glob("*.bin")))
        self.busy = False
        self.assertEqual("reset", self.cache.reset_epoch("epoch-new")["status"])
        self.assertFalse(list(self.root.glob("*.bin")))
        self.assertNotIn("retained-chat", self.cache.deleted_sessions)
        self.assertEqual("unchanged", self.cache.reset_epoch("epoch-new")["status"])
        self.assertEqual("forked", self.prepare("retained-chat", epoch="epoch-new")["status"])
        ledger = json.loads((self.root / "ownership.json").read_text(encoding="utf-8"))
        self.assertEqual([], ledger["deletedSessionIds"])


if __name__ == "__main__":
    unittest.main()
