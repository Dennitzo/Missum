import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import Mock, patch
import urllib.request

from catalog import start_gpu_control
import session_cache
from session_cache import NativeSessionCache


def snapshot(tokens, *, packed=True, magic=0x67677371, version=3, server_version=1):
    values = [-1, server_version, len(tokens), *tokens, 0] if packed else tokens
    return struct.pack("<III", magic, version, len(values)) + struct.pack("<" + "i" * len(values), *values) + b"kv-state"


class SessionCacheTailTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.ids = [101, 102, 201, 202]
        self.bytes = snapshot(self.ids)
        self.calls = []
        self.decoded = "  Teilgedanke\n"
        self.cache = NativeSessionCache(self.root, self.router, lambda _: 1,
                                        estimate_bytes=lambda *_: 1024)
        self.cache.prepare("model", "session")

    def router(self, path, body=None, timeout=15):
        self.calls.append((path, body))
        if path.startswith("slots?"):
            return [dict(id=0, is_processing=False, n_prompt_tokens=len(self.ids))]
        if "action=save" in path:
            (self.root / body["filename"]).write_bytes(self.bytes)
            return dict(n_saved=len(self.ids), n_written=len(self.bytes))
        if "action=restore" in path:
            target = self.root / body["filename"]
            self.ids = NativeSessionCache._snapshot_tokens(target)
            self.bytes = target.read_bytes()
            return dict(n_restored=len(self.ids))
        if path == "detokenize":
            return dict(content=self.decoded)
        raise AssertionError(path)

    def test_exact_tail_is_bound_to_new_snapshot_and_preserves_whitespace(self):
        saved = self.cache.save("model", "session", prompt_tokens=2)
        self.assertEqual("saved", saved["status"])
        self.assertEqual(self.decoded, saved["generatedTail"])
        self.assertIn(("detokenize", dict(model="model", tokens=[201, 202])), self.calls)
        self.assertNotIn("generatedTail", (self.root / "events.jsonl").read_text())
        self.assertNotIn("generatedTail", self.cache.save("model", "session", prompt_tokens=2))

    def test_older_plain_token_list_is_supported(self):
        self.bytes = snapshot(self.ids, packed=False)
        self.assertEqual(self.decoded, self.cache.save("model", "session", 2)["generatedTail"])

    def test_b11507_checkpoint_appendix_keeps_the_existing_packed_token_offsets(self):
        # PR26004 appends checkpoint state after the complete llama sequence
        # state. The server_tokens v1 payload at the front remains unchanged.
        self.bytes = snapshot(self.ids, version=4) + struct.pack("<III", 0x504b4353, 1, 1) + b"checkpoint-state" * 64
        result = self.cache.save("model", "session", prompt_tokens=2)
        target = next(self.root.glob("*.bin"))
        self.assertEqual(self.decoded, result["generatedTail"])
        self.assertEqual(len(self.ids), NativeSessionCache._snapshot_token_count(target))
        self.assertEqual(self.ids, NativeSessionCache._snapshot_tokens(target))

    def test_b11507_packed_media_context_does_not_enter_the_generated_text_tail(self):
        # PR26640 serializes tokens before media indices/chunks, including
        # LLAMA_TOKEN_NULL placeholders for images. Tail recovery must retain
        # logical offsets without treating media payload bytes as text tokens.
        self.ids = [101, -1, -1, 102, 201, 202]
        media = b"opaque-media-chunk"
        packed = struct.pack("<iII", -1, 1, len(self.ids))
        packed += struct.pack("<" + "i" * len(self.ids), *self.ids)
        packed += struct.pack("<III", 1, 1, len(media)) + media
        packed += b"\0" * (-len(packed) % 4)
        self.bytes = struct.pack("<III", 0x67677371, 4, len(packed) // 4) + packed + b"kv-state"

        result = self.cache.save("model", "session", prompt_tokens=4)
        target = next(self.root.glob("*.bin"))

        self.assertEqual(self.decoded, result["generatedTail"])
        self.assertEqual(len(self.ids), NativeSessionCache._snapshot_token_count(target))
        self.assertEqual([201, 202], NativeSessionCache._snapshot_tail(target, 4, len(self.ids)))
        self.assertIn(("detokenize", dict(model="model", tokens=[201, 202])), self.calls)
        # Canonical text-only branch preparation intentionally fails closed for
        # media; it must not silently strip an image and claim an exact prefix.
        with self.assertRaisesRegex(ValueError, "requires text tokens"):
            NativeSessionCache._snapshot_tokens(target)

    def test_outer_sequence_versions_three_and_four_share_bounded_token_inspection_only(self):
        target = self.root / "layout-fixture.bin"
        for version in (3, 4):
            for packed in (False, True):
                with self.subTest(version=version, packed=packed):
                    target.write_bytes(snapshot(self.ids, version=version, packed=packed))
                    self.assertEqual(len(self.ids), NativeSessionCache._snapshot_token_count(target))
                    self.assertEqual(self.ids, NativeSessionCache._snapshot_tokens(target))
                    self.assertEqual([201, 202], NativeSessionCache._snapshot_tail(target, 2, len(self.ids)))

    def test_unknown_outer_sequence_versions_remain_rejected_by_all_readers(self):
        target = self.root / "unknown-layout.bin"
        for version in (0, 2, 5, 99):
            with self.subTest(version=version):
                target.write_bytes(snapshot(self.ids, version=version))
                for read in (NativeSessionCache._snapshot_token_count, NativeSessionCache._snapshot_tokens,
                             lambda path: NativeSessionCache._snapshot_tail(path, 2, len(self.ids))):
                    with self.assertRaises(ValueError):
                        read(target)

    def test_empty_generated_tail_needs_no_detokenizer(self):
        result = self.cache.save("model", "session", len(self.ids))
        self.assertEqual("", result["generatedTail"])
        self.assertFalse(any(path == "detokenize" for path, _ in self.calls))

    def test_foreign_or_unbound_session_never_exposes_tail(self):
        for key in ("other", None, ""):
            with self.subTest(key=key):
                self.assertNotIn("generatedTail", self.cache.save("model", key, 2))
        self.assertFalse(any("action=save" in path for path, _ in self.calls))

    def test_prompt_token_count_must_be_positive_integer(self):
        for count in (True, 0, -1, "2", 1.5):
            with self.subTest(count=count):
                self.assertEqual("unavailable", self.cache.save("model", "session", count)["status"])
        self.assertFalse(any("action=save" in path for path, _ in self.calls))

    def test_corrupt_or_unsupported_vectors_do_not_claim_exact_tail(self):
        fixtures = [b"short", snapshot(self.ids, magic=123), snapshot(self.ids, version=99),
                    snapshot(self.ids, server_version=99), snapshot(self.ids[:-1]),
                    struct.pack("<III", 0x67677371, 3, 0xffffffff),
                    snapshot([101, 102, -1, 202]),
                    snapshot([-2, 102, 201, 202], packed=False),
                    struct.pack("<IIIiII", 0x67677371, 3, 3, -1, 1, 4)]
        for data in fixtures:
            with self.subTest(header=data[:24]):
                self.cache.prepare("model", "session")
                self.bytes = data
                result = self.cache.save("model", "session", 2)
                self.assertEqual("saved", result["status"])
                self.assertNotIn("generatedTail", result)
                self.assertIn("generatedTailDetail", result)
        self.assertFalse(any(path == "detokenize" for path, _ in self.calls))

    def test_unfinished_prefill_has_no_generated_tail(self):
        result = self.cache.save("model", "session", 100)
        self.assertEqual("saved", result["status"])
        self.assertNotIn("generatedTail", result)

    def test_interrupted_prefill_preserves_native_file_when_metadata_is_missing_or_stale(self):
        for packed, metadata in ((True, None), (False, {"tokens": 1, "bytes": 1}), (True, "damaged")):
            with self.subTest(packed=packed, metadata=metadata):
                self.cache.prepare("model", "session")
                self.ids = [101, 102, 201, 202]
                self.bytes = snapshot(self.ids, packed=packed)
                self.cache.save("model", "session", new_tokens=4)
                target = next(self.root.glob("*.bin"))
                original = target.read_bytes()
                if metadata is None:
                    target.with_suffix(".json").unlink()
                else:
                    target.with_suffix(".json").write_text(json.dumps(metadata))
                self.cache.prepare("model", "session")
                self.ids = [901]
                self.bytes = snapshot(self.ids, packed=packed)

                result = self.cache.save("model", "session", prompt_tokens=10)

                self.assertEqual("unchanged", result["status"])
                self.assertEqual("interruptedPrefill", result["reason"])
                self.assertEqual(4, result["retainedTokens"])
                self.assertEqual(original, target.read_bytes())
                self.assertNotIn("generatedTail", result)

    def test_tail_limit_rejects_before_reading_or_detokenizing_large_vectors(self):
        target = self.root / "fixture.bin"
        target.write_bytes(snapshot([1] * 32770))
        with self.assertRaisesRegex(ValueError, "recovery limit"):
            NativeSessionCache._snapshot_tail(target, 1, 32770)

    def test_detokenizer_failure_does_not_invalidate_saved_snapshot(self):
        for invalid in (None, 7, "\ud800"):
            with self.subTest(content=repr(invalid)):
                self.cache.prepare("model", "session")
                self.decoded = invalid
                result = self.cache.save("model", "session", 2)
                self.assertEqual("saved", result["status"])
                self.assertNotIn("generatedTail", result)
                self.assertIn("generatedTailDetail", result)

    def test_interrupted_save_allows_longer_cancellation_drain(self):
        with patch.object(self.cache, "_idle_slot", return_value=dict(n_prompt_tokens=4)) as idle:
            self.cache.save("model", "session", 2)
        idle.assert_called_once_with("model", timeout=session_cache.INTERRUPTED_SAVE_IDLE_SECONDS)
        self.cache.prepare("model", "session")
        with patch.object(self.cache, "_idle_slot", return_value=dict(n_prompt_tokens=4)) as idle:
            self.cache.save("model", "session")
        idle.assert_called_once_with("model", timeout=2)

    def test_supervisor_route_forwards_explicit_prompt_count_only_to_save(self):
        manager = Mock()
        manager.session_save.return_value = dict(status="saved", generatedTail="raw")
        manager.session_prepare.return_value = dict(status="resident")
        server = start_gpu_control(manager, "127.0.0.1", 0)
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        for action in ("save", "prepare"):
            request = urllib.request.Request(f"http://127.0.0.1:{server.server_port}/sessions/{action}",
                data=json.dumps(dict(model="model", sessionKey="session", promptTokens=123)).encode(),
                headers={"Content-Type": "application/json"})
            with urllib.request.urlopen(request, timeout=5) as response:
                result = json.load(response)
            self.assertIn("status", result)
        manager.session_save.assert_called_once_with("model", "session", 123, True, None)
        manager.session_prepare.assert_called_once_with("model", "session", None, None)


if __name__ == "__main__":
    unittest.main()
