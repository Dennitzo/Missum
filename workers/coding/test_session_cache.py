import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import session_cache
from session_cache import NativeSessionCache


class PreparationProgressTests(unittest.TestCase):
    def setUp(self):
        self.now = 0
        self.cancelled = False
        self.progress = session_cache.PreparationProgress(1_000_000,
            lambda: self.cancelled, clock=lambda: self.now)

    def test_advancing_tokens_allow_prefill_beyond_former_five_minute_deadline(self):
        for index in range(1, 20):
            self.now = index * 200
            self.progress.observe({"prompt_progress": {"processed": index * 512}})
        self.progress.check()
        self.assertGreater(self.now, 300)

    def test_heartbeat_or_repeated_count_does_not_extend_idle_deadline(self):
        self.progress.observe({"prompt_progress": {"processed": 512}})
        self.now = 250
        self.progress.observe({"prompt_progress": {"processed": 512, "time_ms": 250000}})
        self.progress.observe({"content": "", "tokens_evaluated": 1_000_000})
        self.now = 300
        with self.assertRaisesRegex(session_cache.PreparationTimeoutError, "no token progress"):
            self.progress.check()

    def test_invalid_progress_does_not_extend_idle_deadline(self):
        self.now = 250
        self.progress.observe({"prompt_progress": {"processed": 1_000_001}})
        self.progress.observe({"prompt_progress": {"processed": True}})
        self.now = 300
        with self.assertRaises(session_cache.PreparationTimeoutError):
            self.progress.check()

    def test_continuous_progress_still_has_two_hour_ceiling(self):
        for index in range(1, 36):
            self.now = index * 200
            self.progress.observe({"prompt_progress": {"processed": index * 512}})
        self.now = session_cache.PREPARATION_MAXIMUM_SECONDS
        with self.assertRaisesRegex(session_cache.PreparationTimeoutError, "two-hour"):
            self.progress.check()

    def test_caller_cancellation_is_distinct_from_timeout(self):
        self.cancelled = True
        with self.assertRaises(session_cache.PreparationCancelledError):
            self.progress.check()


class SessionCacheTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.tokens = 100
        self.busy = False
        self.calls = []
        self.fail_save = False
        self.fail_restore = False
        self.version = 1
        self.cache = self.create_cache()

    def create_cache(self):
        return NativeSessionCache(self.root, self.router, lambda _: self.version)

    def router(self, path, body=None, timeout=15):
        self.calls.append((path, body))
        if path.startswith("slots?"):
            return [dict(id=0, is_processing=self.busy, n_prompt_tokens=self.tokens)]
        if "action=save" in path:
            (self.root / body["filename"]).write_bytes(b"snapshot" * 128)
            if self.fail_save:
                raise OSError("simulated disk error")
            return dict(n_saved=self.tokens, n_written=1024)
        if "action=restore" in path:
            if self.fail_restore:
                raise ValueError("incompatible state")
            self.tokens = 100
            return dict(n_restored=100, n_read=1024)
        raise AssertionError(path)

    def actions(self, action):
        return [call for call in self.calls if "action=" + action in call[0]]

    def test_contiguous_session_stays_resident_without_disk_io(self):
        self.assertEqual("miss", self.cache.prepare("model", "session")["status"])
        self.assertEqual("resident", self.cache.prepare("model", "session")["status"])
        self.assertFalse(self.actions("restore"))
        self.assertFalse(self.actions("save"))
        events = [json.loads(line) for line in (self.root / "events.jsonl").read_text().splitlines()]
        self.assertEqual(["miss", "resident"], [event["status"] for event in events])

    def test_completed_session_restores_after_manager_restart(self):
        self.cache.prepare("model", "session")
        self.assertEqual("saved", self.cache.save("model", "session")["status"])
        self.tokens = 0
        reopened = self.create_cache()
        self.assertEqual(dict(status="restored", restoredTokens=100), reopened.prepare("model", "session"))

    def test_renamed_session_key_reuses_legacy_disk_and_resident_cache(self):
        suffix = "a" * 64
        legacy_key = "go-session-v2-" + suffix
        current_key = "missum-session-v2-" + suffix
        self.cache.prepare("model", legacy_key)
        self.assertEqual("saved", self.cache.save("model", legacy_key)["status"])
        snapshot_names = {path.name for path in self.root.glob("*.bin")}
        self.tokens = 0
        reopened = self.create_cache()
        self.assertEqual(dict(status="restored", restoredTokens=100), reopened.prepare("model", current_key))
        self.assertEqual(snapshot_names, {path.name for path in self.root.glob("*.bin")})
        self.assertEqual("resident", reopened.prepare("model", legacy_key)["status"])

    def test_legacy_alias_does_not_collapse_arbitrary_session_keys(self):
        self.assertNotEqual(self.cache._identity("model", "go-session-v2-user-key"),
                            self.cache._identity("model", "missum-session-v2-user-key"))

    def test_auxiliary_request_saves_then_resume_restores_original(self):
        self.cache.prepare("model", "session")
        self.assertEqual("bypass", self.cache.prepare("model", None)["status"])
        self.assertEqual(1, len(self.actions("save")))
        self.assertEqual("restored", self.cache.prepare("model", "session")["status"])

    def test_null_key_saves_tracked_session_before_unload(self):
        self.cache.prepare("model", "session")
        self.assertEqual("saved", self.cache.save("model", None)["status"])
        self.cache.invalidate("model")
        self.assertEqual("restored", self.cache.prepare("model", "session")["status"])

    def test_busy_slot_is_not_saved_or_restored(self):
        self.cache.prepare("model", "session")
        self.busy = True
        with patch("session_cache.time.monotonic", side_effect=[0, 3, 4, 7]):
            self.assertEqual("unavailable", self.cache.save("model", "session")["status"])
            self.assertEqual("unavailable", self.cache.prepare("model", "another")["status"])
        self.assertFalse(self.actions("save"))
        self.assertFalse(self.actions("restore"))

    def test_stop_race_keeps_latest_resident_prefix_instead_of_old_snapshot(self):
        self.cache.prepare("model", "session")
        self.cache.save("model", "session")
        self.cache.prepare("model", "session")
        self.tokens = 500  # Interrupted follow-up evaluated beyond disk snapshot.
        self.busy = True
        with patch("session_cache.time.monotonic", side_effect=[0, 3]):
            self.assertEqual("unavailable", self.cache.prepare("model", "session")["status"])
        self.busy = False
        self.assertEqual("resident", self.cache.prepare("model", "session")["status"])
        self.assertEqual(500, self.tokens)
        self.assertFalse(self.actions("restore"))

    def test_stop_drain_saves_new_tokens_before_follow_up_or_restart(self):
        self.cache.prepare("model", "session")
        self.tokens = 500
        self.busy = True
        def finish_cancellation(_):
            self.busy = False
        with patch("session_cache.time.sleep", side_effect=finish_cancellation):
            saved = self.cache.save("model", "session")
        self.assertEqual("saved", saved["status"])
        self.assertEqual(500, saved["savedTokens"])
        metadata = json.loads(next(self.root.glob("*.bin")).with_suffix(".json").read_text())
        self.assertEqual(500, metadata["tokens"])

    def test_interrupted_save_waits_for_a_long_native_batch_to_drain(self):
        # Observed: a cancelled 164k-token prompt released its slot 55 s after the
        # Stop. The interrupted save must outlast such a batch instead of giving
        # up after ten seconds and leaving only the pre-stop snapshot on disk.
        self.cache.prepare("model", "session")
        self.tokens = 500
        self.busy = True
        polls = []
        def poll(_):
            polls.append(1)
            if len(polls) == 3:
                self.busy = False
        with patch("session_cache.time.sleep", side_effect=poll),                 patch("session_cache.time.monotonic", side_effect=[0, 20, 45, 70, 89]):
            saved = self.cache.save("model", "session", 100)
        self.assertEqual("saved", saved["status"])
        self.assertEqual(500, saved["savedTokens"])
        self.assertGreaterEqual(session_cache.INTERRUPTED_SAVE_IDLE_SECONDS, 60)
        self.busy = False
        self.cache.prepare("model", "session")  # A new turn marks the session dirty again.
        self.busy = True
        with patch("session_cache.time.sleep"),                 patch("session_cache.time.monotonic", side_effect=[0, 30, 91]):
            self.assertEqual("unavailable", self.cache.save("model", "session", 100)["status"])

    def test_failed_save_does_not_replace_valid_snapshot(self):
        self.cache.prepare("model", "session")
        self.cache.save("model", "session")
        target = next(self.root.glob("*.bin"))
        original = target.read_bytes()
        self.cache.prepare("model", "session")
        self.fail_save = True
        self.assertEqual("unavailable", self.cache.save("model", "session")["status"])
        self.assertEqual(original, target.read_bytes())
        self.assertFalse(list(self.root.glob("*.pending")))

    def test_cancelled_replacement_prefill_preserves_the_larger_durable_cache(self):
        self.cache.prepare("model", "session")
        self.tokens = 134957
        self.cache.save("model", "session")
        target = next(self.root.glob("*.bin"))
        original, metadata = target.read_bytes(), target.with_suffix(".json").read_bytes()
        self.cache.prepare("model", "session")
        self.tokens = 6912
        writes = len(self.actions("save"))

        result = self.cache.save("model", "session", prompt_tokens=135000)

        self.assertEqual("unchanged", result["status"])
        self.assertEqual("interruptedPrefill", result["reason"])
        self.assertEqual(134957, result["retainedTokens"])
        self.assertEqual(6912, result["residentTokens"])
        self.assertNotIn("generatedTail", result)
        self.assertEqual(writes, len(self.actions("save")))
        self.assertEqual(original, target.read_bytes())
        self.assertEqual(metadata, target.with_suffix(".json").read_bytes())
        self.assertTrue(self.cache.resident["model"]["dirty"])

        # Stop is followed by a run-end flush, unload or process shutdown. None
        # of these maintenance calls may silently undo the interruption guard.
        for save in (lambda: self.cache.save("model", "session"),
                     lambda: self.cache.save("model", None), self.cache.save_all):
            save()
            self.assertEqual(metadata, target.with_suffix(".json").read_bytes())
        self.cache.prepare("model", "another-session")
        self.assertEqual(metadata, target.with_suffix(".json").read_bytes())
        self.assertEqual(writes, len(self.actions("save")))

    def test_actual_native_saved_count_protects_cache_when_slot_reports_the_full_target(self):
        self.cache.prepare("model", "session")
        self.tokens = 134957
        self.cache.save("model", "session")
        target = next(self.root.glob("*.bin"))
        original, metadata = target.read_bytes(), target.with_suffix(".json").read_bytes()
        self.cache.prepare("model", "session")
        self.tokens = 135000  # Slot metadata can describe the requested prompt.
        native_router = self.cache.router

        def save_only_processed_tokens(path, body=None, timeout=15):
            result = native_router(path, body, timeout)
            if "action=save" in path:
                (self.root / body["filename"]).write_bytes(b"partial-new-prefill")
                result["n_saved"] = 6912
            return result

        self.cache.router = save_only_processed_tokens
        result = self.cache.save("model", "session", prompt_tokens=135000)

        self.assertEqual("unchanged", result["status"])
        self.assertEqual("interruptedPrefill", result["reason"])
        self.assertEqual(6912, result["residentTokens"])
        self.assertEqual(original, target.read_bytes())
        self.assertEqual(metadata, target.with_suffix(".json").read_bytes())
        self.assertFalse(list(self.root.glob("*.pending")))

        # No model unload/restart is required. Despite misleading slot metadata,
        # the next same-session request must load the larger preserved file.
        restored = self.cache.prepare("model", "session")
        self.assertEqual("restored", restored["status"])
        self.assertEqual(1, len(self.actions("restore")))
        self.assertNotIn("restore_preserved_snapshot", self.cache.resident["model"])

    def test_completed_shorter_compaction_replaces_previously_protected_snapshot(self):
        self.cache.prepare("model", "session")
        self.tokens = 1000
        self.cache.save("model", "session")
        self.cache.prepare("model", "session")
        self.tokens = 50
        self.assertEqual("unchanged", self.cache.save("model", "session", prompt_tokens=1100)["status"])

        self.cache.prepare("model", "session")
        self.tokens = 400
        result = self.cache.save("model", "session", new_tokens=350)

        self.assertEqual("saved", result["status"])
        self.assertEqual(400, result["savedTokens"])
        self.assertNotIn("interrupted_prompt_tokens", self.cache.resident["model"])
        metadata = json.loads(next(self.root.glob("*.bin")).with_suffix(".json").read_text())
        self.assertEqual(400, metadata["tokens"])

    def test_failed_direct_resume_cannot_remove_the_interrupted_prefill_guard(self):
        self.cache.prepare("model", "session")
        self.tokens = 1000
        self.cache.save("model", "session")
        target = next(self.root.glob("*.bin"))
        original_metadata = target.with_suffix(".json").read_bytes()
        self.cache.prepare("model", "session")
        self.tokens = 50
        self.assertEqual("unchanged", self.cache.save("model", "session", prompt_tokens=1100)["status"])
        self.fail_restore = True
        self.assertEqual("unavailable", self.cache.prepare("model", "session")["status"])
        self.cache.save_all()
        self.assertEqual(original_metadata, target.with_suffix(".json").read_bytes())
        self.assertTrue(self.cache.resident["model"]["restore_preserved_snapshot"])

    def test_unknown_native_saved_count_never_replaces_a_valid_snapshot(self):
        self.cache.prepare("model", "session")
        self.tokens = 1000
        self.cache.save("model", "session")
        target = next(self.root.glob("*.bin"))
        original, metadata = target.read_bytes(), target.with_suffix(".json").read_bytes()
        self.cache.prepare("model", "session")
        self.tokens = 1100
        native_router = self.cache.router
        for count in (None, True, "100", 1.5, -1, 0):
            with self.subTest(count=count):
                def invalid_saved_count(path, body=None, timeout=15):
                    result = native_router(path, body, timeout)
                    if "action=save" in path:
                        result["n_saved"] = count
                    return result

                self.cache.router = invalid_saved_count
                result = self.cache.save("model", "session", prompt_tokens=1100)

                self.assertEqual("unavailable", result["status"])
                self.assertEqual(original, target.read_bytes())
                self.assertEqual(metadata, target.with_suffix(".json").read_bytes())
                self.assertFalse(list(self.root.glob("*.pending")))

    def test_interrupted_initial_prefill_without_a_better_snapshot_is_still_saved(self):
        self.cache.prepare("model", "session")
        self.tokens = 50
        result = self.cache.save("model", "session", prompt_tokens=1000)
        self.assertEqual("saved", result["status"])
        self.assertEqual(50, result["savedTokens"])

    def test_interrupted_fully_evaluated_shorter_prompt_is_allowed_to_replace(self):
        self.cache.prepare("model", "session")
        self.tokens = 1000
        self.cache.save("model", "session")
        self.cache.prepare("model", "session")
        self.tokens = 400
        result = self.cache.save("model", "session", prompt_tokens=350)
        self.assertEqual("saved", result["status"])
        self.assertEqual(400, result["savedTokens"])

    def test_model_fingerprint_change_cannot_preserve_another_configurations_snapshot(self):
        self.cache.prepare("model", "session")
        self.tokens = 1000
        self.cache.save("model", "session")
        original = next(self.root.glob("*.bin"))
        original_metadata = original.with_suffix(".json").read_bytes()
        self.version = 2
        self.cache.invalidate("model")
        self.tokens = 0
        self.cache.prepare("model", "session")
        self.tokens = 50
        result = self.cache.save("model", "session", prompt_tokens=1000)
        self.assertEqual("saved", result["status"])
        self.assertEqual(50, result["savedTokens"])
        self.assertEqual(2, len(list(self.root.glob("*.bin"))))
        self.assertEqual(original_metadata, original.with_suffix(".json").read_bytes())

    def test_model_version_change_does_not_restore_incompatible_snapshot(self):
        self.cache.prepare("model", "session")
        self.cache.save("model", "session")
        self.version = 2
        self.tokens = 0
        self.assertEqual("miss", self.create_cache().prepare("model", "session")["status"])
        self.assertFalse(self.actions("restore"))

    def test_failed_restore_returns_unavailable_and_can_save_new_state(self):
        self.cache.prepare("model", "session")
        self.cache.save("model", "session")
        self.cache.invalidate("model")
        self.fail_restore = True
        self.assertEqual("unavailable", self.cache.prepare("model", "session")["status"])
        self.assertEqual("saved", self.cache.save("model", "session")["status"])

    def test_file_names_never_contain_external_session_key(self):
        self.cache.prepare("model", "../../outside")
        self.cache.save("model", "../../outside")
        self.assertEqual(1, len(list(self.root.glob("*.bin"))))
        self.assertEqual(64, len(next(self.root.glob("*.bin")).stem))

    def test_disk_reserve_prevents_native_write(self):
        self.cache.prepare("model", "session")
        with patch("session_cache.shutil.disk_usage", return_value=type("Usage", (), {"free": 1})()):
            self.assertEqual("unavailable", self.cache.save("model", "session")["status"])
        self.assertFalse(self.actions("save"))

    def test_snapshot_replacement_counts_only_final_size_toward_budget(self):
        self.cache.prepare("model", "session")
        self.cache.save("model", "session")
        self.cache.maximum_bytes = 1536
        self.cache.prepare("model", "session")
        self.assertEqual("saved", self.cache.save("model", "session")["status"])
        self.assertEqual(1, len(list(self.root.glob("*.bin"))))

    def test_atomic_replacement_still_requires_free_space_for_both_files(self):
        self.cache.prepare("model", "session")
        self.cache.save("model", "session")
        self.cache.prepare("model", "session")
        saves = len(self.actions("save"))
        with patch("session_cache.shutil.disk_usage", return_value=type("Usage", (), {"free": self.cache.free_reserve + 1000})()):
            self.assertEqual("unavailable", self.cache.save("model", "session")["status"])
        self.assertEqual(saves, len(self.actions("save")))

    def test_first_snapshot_uses_model_estimate_when_available(self):
        self.cache.estimate_bytes = lambda model, tokens: 1200
        self.cache.maximum_bytes = 1536
        self.cache.prepare("model", "session")
        self.assertEqual("saved", self.cache.save("model", "session")["status"])

    def test_eviction_removes_only_inactive_owned_snapshots(self):
        self.cache.prepare("model", "session")
        stale = self.root / ("a" * 64 + ".bin")
        stale.write_bytes(b"old" * 512)
        (self.root / "do-not-delete.txt").write_text("user")
        self.cache.maximum_bytes = 100 * 256 * 1024
        self.assertEqual("saved", self.cache.save("model", "session")["status"])
        self.assertFalse(stale.exists())
        self.assertTrue((self.root / "do-not-delete.txt").exists())

    def test_shutdown_saves_each_dirty_session_only_once(self):
        self.cache.prepare("one", "session")
        self.cache.prepare("two", "session")
        self.cache.save_all()
        self.cache.save_all()
        self.assertEqual(2, len(self.actions("save")))

    def test_general_and_coding_session_switches_restore_only_their_own_snapshot(self):
        for scope in ("general/session-a", "coding/session-a", "general/session-b"):
            self.assertEqual("miss", self.cache.prepare("model", scope)["status"])
            self.cache.save("model", scope)
        for scope in ("general/session-a", "coding/session-a", "general/session-b"):
            self.assertEqual("restored", self.cache.prepare("model", scope)["status"])
        self.assertEqual(3, len(list(self.root.glob("*.bin"))))

    def test_model_switch_never_restores_another_models_kv_but_return_restores_its_own(self):
        self.cache.prepare("model-one", "session")
        self.cache.save("model-one", "session")
        self.cache.invalidate("model-one")
        self.assertEqual("miss", self.cache.prepare("model-two", "session")["status"])
        self.cache.save("model-two", "session")
        self.cache.invalidate("model-two")
        self.assertEqual("restored", self.cache.prepare("model-one", "session")["status"])
        self.assertEqual(2, len(list(self.root.glob("*.bin"))))

    def test_reopened_app_and_native_empty_slot_restore_saved_snapshot(self):
        self.cache.prepare("model", "session")
        self.cache.save("model", "session")
        self.tokens = 0
        self.assertEqual("restored", self.cache.prepare("model", "session")["status"])
        self.assertEqual("restored", self.create_cache().prepare("model", "session")["status"])

    def test_slot_restore_without_positive_token_measurement_is_not_reported_as_restored(self):
        self.cache.prepare("model", "session")
        self.cache.save("model", "session")
        self.cache.invalidate("model")
        original = self.cache.router
        self.cache.router = lambda path, body=None, timeout=15: dict(n_restored=0) if "action=restore" in path else original(path, body, timeout)
        self.assertEqual("unavailable", self.cache.prepare("model", "session")["status"])

    def test_foreign_session_save_cannot_overwrite_current_session_snapshot(self):
        self.cache.prepare("model", "session-a")
        self.assertEqual("unchanged", self.cache.save("model", "session-b")["status"])
        self.assertFalse(self.actions("save"))


if __name__ == "__main__":
    unittest.main()
