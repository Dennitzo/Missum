import importlib.util
import io
import json
import struct
from pathlib import Path
import tempfile
import unittest
from unittest.mock import MagicMock, patch
from test_catalog import gguf
from session_cache import NativeSessionCache, PreparationCancelledError, PreparationTimeoutError

spec = importlib.util.spec_from_file_location("agent_catalog", Path(__file__).with_name("catalog.py"))
catalog = importlib.util.module_from_spec(spec)
spec.loader.exec_module(catalog)


class SubagentRuntimeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        gguf(self.root / "FutureLocalModel.gguf", architecture="llama", context=32768)
        self.model = catalog.discover_models(self.root)[0]
        self.base = self.model["id"]
        self.child = self.base + "@subagent"
        self.manager = catalog.GpuLoadManager(self.root, self.root / "models.ini", self.root, 8081)
        self.devices = [dict(index=0, device="CUDA0", free=40 * 1024**3),
                        dict(index=1, device="CUDA1", free=40 * 1024**3)]
        self.primary = dict(id=self.base, status=dict(value="loaded", args=["--device", "CUDA0", "--split-mode", "none"]))
        self.active = [self.primary]
        self.loads = []
        self.fail = False
        self.failed_models = set()
        self.failure_detail = "CUDA error: out of memory"
        self.contexts = {}
        self.manager.router = self.router

    def router(self, path, body=None, timeout=15):
        if path.startswith("v1/models"):
            return dict(data=self.active)
        if path.startswith("props?"):
            import urllib.parse
            model = urllib.parse.parse_qs(path.split("?", 1)[1])["model"][0]
            return dict(default_generation_settings=dict(n_ctx=self.contexts.get(model, 32768)))
        if path == "models/load":
            self.loads.append(body["model"])
            failed = self.fail or body["model"] in self.failed_models
            if failed:
                (self.root / "llama.stderr.log").write_text(self.failure_detail)
            self.active.append(dict(id=body["model"], status=dict(value="failed" if failed else "loaded", failed=failed)))
            return dict(success=True)
        raise AssertionError(path)

    def test_ordinary_cold_load_completes_exact_main_and_replica_before_returning(self):
        self.active.clear()
        with patch.object(catalog, "gpu_inventory", return_value=self.devices):
            result = self.manager.load(self.base)
        self.assertTrue(result["success"])
        self.assertEqual([self.base, self.child], self.loads)
        self.assertEqual("CUDA0", self.manager.placements[self.base])
        self.assertEqual("CUDA1", self.manager.placements[self.child])
        self.assertEqual(32768, self.manager.policies[self.base]["context"])
        self.assertEqual(32768, self.manager.policies[self.child]["context"])
        self.assertTrue(result["subagent"]["allowed"])
        self.assertTrue(result["subagent"]["loaded"])
        self.assertFalse(result["subagent"]["reused"])
        self.assertEqual(self.child, result["subagent"]["instanceId"])
        self.assertEqual(["loaded", "loaded"], [item["status"]["value"] for item in self.active])

    def test_reused_primary_eagerly_loads_missing_replica_then_preserves_both_slots(self):
        with patch.object(catalog, "gpu_inventory", return_value=self.devices):
            first = self.manager.load(self.base)
            self.assertTrue(first["reused"])
            self.assertTrue(first["subagent"]["loaded"])
            self.assertFalse(first["subagent"]["reused"])
            with patch.object(self.manager.sessions, "invalidate") as invalidate:
                second = self.manager.load(self.base)
            invalidate.assert_not_called()
        self.assertTrue(second["reused"])
        self.assertTrue(second["subagent"]["loaded"])
        self.assertTrue(second["subagent"]["reused"])
        self.assertEqual([self.child], self.loads)

    def test_multigpu_primary_stays_successful_without_any_replica_load(self):
        self.primary["status"]["args"] = ["--device", "CUDA0,CUDA1", "--split-mode", "layer"]
        with patch.object(catalog, "gpu_inventory", return_value=self.devices):
            result = self.manager.load(self.base)
        self.assertTrue(result["success"])
        self.assertFalse(result["subagent"]["loaded"])
        self.assertEqual("subagent.primary_uses_multiple_gpus", result["subagent"]["reason"])
        self.assertEqual([], self.loads)

    def test_low_vram_admission_is_rechecked_after_memory_becomes_available(self):
        self.devices[1]["free"] = 1024**3
        with patch.object(catalog, "gpu_inventory", return_value=self.devices):
            blocked = self.manager.load(self.base)
            self.assertEqual("subagent.vram_insufficient", blocked["subagent"]["reason"])
            self.assertEqual([], self.loads)
            self.assertNotIn(self.base, self.manager.agent_failures)
            self.devices[1]["free"] = 40 * 1024**3
            ready = self.manager.load(self.base)
        self.assertTrue(ready["subagent"]["loaded"])
        self.assertEqual([self.child], self.loads)

    def test_replica_native_failure_keeps_primary_and_is_sticky_until_actual_reload(self):
        self.active.clear()
        self.failed_models.add(self.child)
        with patch.object(catalog, "gpu_inventory", return_value=self.devices):
            first = self.manager.load(self.base)
            self.assertTrue(first["success"])
            self.assertEqual("subagent.vram_allocation_failed", first["subagent"]["reason"])
            self.assertFalse(first["subagent"]["loaded"])
            self.assertEqual("loaded", self.active[0]["status"]["value"])
            self.assertEqual("CUDA0", self.manager.placements[self.base])
            self.assertEqual("CUDA1", self.manager.placements[self.child])
            self.failed_models.clear()
            reused = self.manager.load(self.base)
            self.assertEqual("subagent.vram_allocation_failed", reused["subagent"]["reason"])
            self.assertEqual([self.base, self.child], self.loads)
            self.active.clear()  # A real unload/reload is the retry boundary.
            reloaded = self.manager.load(self.base)
        self.assertTrue(reloaded["subagent"]["loaded"])
        self.assertEqual([self.base, self.child, self.base, self.child], self.loads)
        self.assertNotIn(self.base, self.manager.agent_failures)

    def test_non_memory_replica_failure_is_reported_without_retry_or_primary_failure(self):
        self.active.clear()
        self.failed_models.add(self.child)
        self.failure_detail = "invalid model tensor"
        with patch.object(catalog, "gpu_inventory", return_value=self.devices):
            result = self.manager.load(self.base)
            reused = self.manager.load(self.base)
        self.assertTrue(result["success"])
        self.assertTrue(reused["success"])
        self.assertEqual("subagent.runtime_load_failed", result["subagent"]["reason"])
        self.assertEqual("subagent.runtime_load_failed", reused["subagent"]["reason"])
        self.assertEqual([self.base, self.child], self.loads)
        self.assertEqual("loaded", self.active[0]["status"]["value"])

    def test_replica_preload_uses_measured_main_allocation_instead_of_small_fixture_estimate(self):
        self.active.clear()
        before = [dict(index=0, device="CUDA0", free=40 * 1024**3),
                  dict(index=1, device="CUDA1", free=36 * 1024**3)]
        after = [dict(index=0, device="CUDA0", free=5 * 1024**3), before[1]]
        with patch.object(catalog, "gpu_inventory", side_effect=[before, after, after]):
            result = self.manager.load(self.base)
        self.assertTrue(result["success"])
        self.assertEqual(35 * 1024**3, self.manager.allocations[self.base])
        self.assertEqual("subagent.vram_insufficient", result["subagent"]["reason"])
        self.assertEqual(37 * 1024**3, result["subagent"]["requiredBytes"])
        self.assertEqual([self.base], self.loads)

    def test_one_gpu_primary_load_succeeds_without_replica_request(self):
        self.active.clear()
        with patch.object(catalog, "gpu_inventory", return_value=self.devices[:1]):
            result = self.manager.load(self.base)
        self.assertTrue(result["success"])
        self.assertEqual("subagent.second_gpu_unavailable", result["subagent"]["reason"])
        self.assertEqual([self.base], self.loads)

    def test_resident_replica_with_different_actual_context_is_not_reconfigured(self):
        self.active.append(dict(id=self.child, status=dict(value="loaded", args=["--device", "CUDA1", "--split-mode", "none"])))
        self.contexts[self.child] = 16384
        with patch.object(catalog, "gpu_inventory", return_value=self.devices), patch.object(
                self.manager.sessions, "invalidate") as invalidate:
            result = self.manager.load(self.base)
        self.assertTrue(result["success"])
        self.assertEqual("subagent.context_mismatch", result["subagent"]["reason"])
        self.assertEqual(16384, result["subagent"]["replicaContextLength"])
        self.assertEqual([], self.loads)
        invalidate.assert_not_called()

    def test_loading_primary_finishes_before_replica_admission_without_rewriting_placement(self):
        self.primary["status"]["value"] = "loading"
        def finish(_):
            self.primary["status"]["value"] = "loaded"
        with patch.object(catalog, "gpu_inventory", return_value=self.devices), patch.object(catalog.time, "sleep", side_effect=finish):
            result = self.manager.load(self.base)
        self.assertTrue(result["reused"])
        self.assertTrue(result["subagent"]["loaded"])
        self.assertNotIn(self.base, self.manager.policies)
        self.assertEqual([self.child], self.loads)

    def test_embedding_load_does_not_create_agent_replica(self):
        gguf(self.root / "embedding.gguf", architecture="bert", context=8192)
        embedding = next(model for model in catalog.discover_models(self.root) if model["role"] == "embedding")
        self.active.clear()
        with patch.object(catalog, "gpu_inventory", return_value=self.devices):
            result = self.manager.load(embedding["id"])
        self.assertTrue(result["success"])
        self.assertEqual("subagent.model_not_language", result["subagent"]["reason"])
        self.assertEqual([embedding["id"]], self.loads)

    def test_future_model_creates_two_exact_file_instances_with_independent_single_gpu_slots(self):
        with patch.object(catalog, "gpu_inventory", return_value=self.devices):
            self.assertTrue(self.manager.agent_status(self.base)["allowed"])
            self.manager.load(self.child)
        self.assertEqual([self.child], self.loads)
        self.assertEqual("CUDA1", self.manager.placements[self.child])
        self.assertEqual(32768, self.manager.policies[self.child]["context"])
        preset = self.manager.preset.read_text()
        block = preset.split("[" + self.child + "]", 1)[1].split("\n[")[0]
        self.assertIn("device = CUDA1", block)
        self.assertIn("missum-base-model:" + self.base, block)
        self.assertIn("split-mode = none", block)
        self.assertEqual(2, preset.count("model = " + self.model["path"].as_posix()))
        self.assertEqual(1, preset.count("parallel = 1"))

    def test_matching_projector_is_available_to_both_future_model_instances_and_cache_identity(self):
        gguf(self.root / "mmproj-F16.gguf", architecture="clip")
        self.manager.placements.update({self.base: "CUDA0", self.child: "CUDA1"})
        models = self.manager.refresh()
        for instance in (self.base, self.child):
            model = next(model for model in models if model["id"] == instance)
            self.assertEqual(self.root / "mmproj-F16.gguf", model["projector"])
            block = self.manager.preset.read_text().split("[" + instance + "]", 1)[1].split("\n[")[0]
            self.assertIn("missum-vision:projector", block)
            self.assertIn("mmproj-device = " + self.manager.placements[instance], block)
            self.assertTrue(any("mmproj-F16.gguf" in path for path, _, _ in self.manager.session_fingerprint(instance)["files"]))

    def test_vision_replica_inherits_primary_safe_batch_and_fingerprint_without_growing_gpu_buffers(self):
        gguf(self.root / "mmproj-F16.gguf", architecture="clip")
        model = next(model for model in catalog.discover_models(self.root) if model["id"] == self.base)
        self.manager.policies[self.base] = dict(catalog.model_runtime_policy(model, "CUDA0"),
                                               batchSize=256, ubatchSize=256)
        self.manager.placements[self.base] = "CUDA0"
        with patch.object(catalog, "gpu_inventory", return_value=self.devices):
            result = self.manager.load(self.base)
        self.assertTrue(result["subagent"]["loaded"])
        self.assertEqual([self.child], self.loads)
        for instance in (self.base, self.child):
            policy = self.manager.policies[instance]
            self.assertEqual((policy["batchSize"], policy["ubatchSize"]), (256, 256))
            fingerprint = self.manager.session_fingerprint(instance)
            self.assertEqual(fingerprint["visionBatch"], dict(batchSize=256, ubatchSize=256))
            block = self.manager.preset.read_text().split("[" + instance + "]", 1)[1].split("\n[")[0]
            self.assertIn("batch-size = 256\nubatch-size = 256", block)
        self.assertEqual(self.manager.placements, {self.base: "CUDA0", self.child: "CUDA1"})

    def test_actual_multi_gpu_placement_delegates_on_the_existing_primary_without_replica(self):
        self.primary["status"]["args"] = ["--device", "CUDA0,CUDA1", "--split-mode", "layer"]
        with patch.object(catalog, "gpu_inventory", return_value=self.devices):
            result = self.manager.agent_status(self.base)
        self.assertTrue(result["allowed"])
        self.assertEqual(self.base, result["instanceId"])
        self.assertEqual(0, result["gpuIndex"])
        self.assertEqual("shared_primary", result["executionMode"])
        self.assertEqual("subagent.primary_uses_multiple_gpus", result["parallelUnavailableReason"])
        self.assertFalse(self.loads)

    def test_actual_free_vram_blocks_new_replica_but_resident_replica_remains_usable(self):
        self.devices[1]["free"] = 1024**3
        with patch.object(catalog, "gpu_inventory", return_value=self.devices):
            shared = self.manager.agent_status(self.base)
            self.assertTrue(shared["allowed"])
            self.assertEqual(self.base, shared["instanceId"])
            self.assertEqual("subagent.vram_insufficient", shared["parallelUnavailableReason"])
            self.active.append(dict(id=self.child, status=dict(value="loaded")))
            parallel = self.manager.agent_status(self.base)
            self.assertTrue(parallel["allowed"])
            self.assertEqual(self.child, parallel["instanceId"])
            self.assertEqual("parallel", parallel["executionMode"])

    def test_failed_replica_never_retries_using_primary_gpu(self):
        self.fail = True
        with patch.object(catalog, "gpu_inventory", return_value=self.devices):
            with self.assertRaisesRegex(RuntimeError, "subagent.vram_allocation_failed"):
                self.manager.load(self.child)
            available = self.manager.agent_status(self.base)
            self.assertTrue(available["allowed"])
            self.assertEqual(self.base, available["instanceId"])
            self.assertEqual("subagent.vram_allocation_failed", available["parallelUnavailableReason"])
        self.assertEqual([self.child], self.loads)
        self.assertEqual("CUDA1", self.manager.placements[self.child])

    def test_shared_preparation_uses_independent_keys_without_loading_or_reconfiguring_model(self):
        self.devices[1]["free"] = 1024**3
        with patch.object(catalog, "gpu_inventory", return_value=self.devices), patch.object(
                self.manager.sessions, "fork", return_value=dict(status="forked", restoredTokens=230)) as fork:
            result = self.manager.agent_prepare(self.base, "parent", "child", {}, "child-session", "parent-session")
        self.assertTrue(result["allowed"])
        self.assertEqual((self.base, 0, "forked", 230),
                         (result["instanceId"], result["gpuIndex"], result["cacheStatus"], result["cachedTokens"]))
        self.assertEqual([], self.loads)
        self.assertEqual({}, self.manager.policies)
        fork.assert_called_once_with(self.base, "parent", self.base, "child", {}, "child-session", "parent-session", None)

    def test_failed_secondary_allocation_prepares_shared_branch_without_loading_another_primary(self):
        self.failed_models.add(self.child)
        with patch.object(catalog, "gpu_inventory", return_value=self.devices), patch.object(
                self.manager.sessions, "fork", return_value=dict(status="forked", restoredTokens=230)) as fork:
            result = self.manager.agent_prepare(self.base, "parent", "child", {})
        self.assertTrue(result["allowed"])
        self.assertEqual((self.base, 0), (result["instanceId"], result["gpuIndex"]))
        self.assertEqual("subagent.vram_allocation_failed", result["parallelUnavailableReason"])
        self.assertEqual([self.child], self.loads)
        self.assertEqual("loaded", self.primary["status"]["value"])
        fork.assert_called_once_with(self.base, "parent", self.base, "child", {}, None, None, None)

    def test_shared_admission_rejects_another_models_resident_slot_and_unfinished_primary_load(self):
        for status in ("loading", "failed"):
            with self.subTest(status=status), patch.object(catalog, "gpu_inventory", return_value=self.devices[:1]):
                self.primary["status"]["value"] = status
                result = self.manager.agent_status(self.base)
                self.assertFalse(result["allowed"])
                self.assertEqual("subagent.primary_not_loaded", result["reason"])
        self.active[:] = [dict(id="other-model", status=dict(value="loaded"))]
        self.assertEqual("subagent.primary_not_loaded", self.manager.agent_status(self.base)["reason"])

    def test_one_gpu_shared_admission_preserves_the_actual_primary_context_limit(self):
        with patch.object(catalog, "gpu_inventory", return_value=self.devices[:1]):
            result = self.manager.agent_status(self.base, 32768)
            self.assertTrue(result["allowed"])
            self.assertEqual((self.base, 0, 32768), (result["instanceId"], result["gpuIndex"], result["contextLength"]))
            rejected = self.manager.agent_status(self.base, 32769)
            self.assertFalse(rejected["allowed"])
            self.assertEqual("subagent.context_too_large", rejected["reason"])

    def test_shared_canonical_fork_preserves_parent_and_child_snapshots_across_owner_switches(self):
        cache, state, _, canonical, source, prefill = self.canonical_cache()
        result = cache.fork(self.base, "parent", self.base, "child", prefill,
                            session_id="child-session", parent_session_id="parent-session")
        self.assertEqual("forked", result["status"])
        self.assertEqual(canonical, state[self.base])
        self.assertEqual("child", cache.resident[self.base]["key"])
        parent_file = self.root / (cache._identity(self.base, "parent") + ".bin")
        child_file = self.root / (cache._identity(self.base, "child") + ".bin")
        self.assertEqual(source, cache._snapshot_tokens(parent_file))
        self.assertEqual(canonical, cache._snapshot_tokens(child_file))
        child_progress = [*canonical, 5000, 5001]
        state[self.base] = child_progress
        self.assertEqual("restored", cache.prepare(self.base, "parent")["status"])
        self.assertEqual(source, state[self.base])
        self.assertEqual(child_progress, cache._snapshot_tokens(child_file))
        self.assertEqual("restored", cache.prepare(self.base, "child")["status"])
        self.assertEqual(child_progress, state[self.base])

    def test_physical_primary_must_be_gpu_zero_even_when_cuda_device_order_differs(self):
        self.devices[0]["device"], self.devices[1]["device"] = "CUDA1", "CUDA0"
        with patch.object(catalog, "gpu_inventory", return_value=self.devices):
            self.assertEqual("subagent.primary_not_gpu0", self.manager.agent_status(self.base)["reason"])
            self.primary["status"]["args"] = ["--device", "CUDA1", "--split-mode", "none"]
            self.assertTrue(self.manager.agent_status(self.base)["allowed"])

    def test_fork_cache_copies_exact_prefix_into_independent_snapshot_and_preserves_child_progress(self):
        tokens = {self.base: 250, self.child: 0}
        def router(path, body=None, timeout=15):
            if path.startswith("slots?"):
                import urllib.parse
                model = urllib.parse.parse_qs(path.split("?", 1)[1])["model"][0]
                return [dict(id=0, is_processing=False, n_prompt_tokens=tokens[model])]
            if "action=save" in path:
                (self.root / body["filename"]).write_bytes((str(tokens[body["model"]]) + "-KV").encode())
                return dict(n_saved=tokens[body["model"]])
            if "action=restore" in path:
                tokens[body["model"]] = int((self.root / body["filename"]).read_text().split("-", 1)[0])
                return dict(n_restored=tokens[body["model"]])
            raise AssertionError(path)
        cache = NativeSessionCache(self.root, router, lambda model: dict(files="same", context=32768,
            placement="CUDA1" if model == self.child else "CUDA0"), free_reserve=0, estimate_bytes=lambda *_: 1024)
        cache.prepare(self.base, "parent")
        result = cache.fork(self.base, "parent", self.child, "child")
        self.assertEqual(("forked", 250), (result["status"], result["restoredTokens"]))
        self.assertEqual(250, tokens[self.child])
        parent_file = self.root / (cache._identity(self.base, "parent") + ".bin")
        child_file = self.root / (cache._identity(self.child, "child") + ".bin")
        self.assertNotEqual(parent_file, child_file)
        self.assertEqual(parent_file.read_bytes(), child_file.read_bytes())
        tokens[self.child] = 400
        cache.save(self.child, "child")
        self.assertEqual(b"250-KV", parent_file.read_bytes())
        self.assertEqual(b"400-KV", child_file.read_bytes())
        self.assertEqual("resident", cache.fork(self.base, "parent", self.child, "child")["status"])

    def test_cache_fork_rejects_different_geometry_without_snapshot_write(self):
        cache = NativeSessionCache(self.root, self.router, lambda model: dict(context=32768 if model == self.base else 16384))
        result = cache.fork(self.base, "parent", self.child, "child")
        self.assertEqual("unavailable", result["status"])
        self.assertIn("incompatible", result["detail"])
        self.assertFalse(list(self.root.glob("*.bin")))

    def canonical_cache(self, *, cold=False, sampled=1, extra_token=False, corrupt_token=False, rewrite_prefix=False):
        source_tokens = list(range(1, 251))
        canonical = [*range(1, 201), *range(900, 921)]
        state = {self.base: source_tokens.copy(), self.child: []}
        calls = []

        def router(path, body=None, timeout=15):
            calls.append((path, body))
            if path.startswith("slots?"):
                import urllib.parse
                model = urllib.parse.parse_qs(path.split("?", 1)[1])["model"][0]
                return [dict(id=0, is_processing=False, n_prompt_tokens=len(state[model]))]
            if path == "apply-template":
                self.assertIn(body["model"], (self.base, self.child))
                self.assertEqual("none", body["reasoning_effort"])
                if body["add_generation_prompt"]:
                    return dict(prompt="first-native-inference")
                return dict(prompt="canonical-history-with-closed-parent-tools-and-child-task")
            if path == "tokenize":
                self.assertTrue(body["add_special"])
                self.assertTrue(body["parse_special"])
                if body["content"] == "first-native-inference":
                    return dict(tokens=[*([5000] if rewrite_prefix else []), *canonical, 4000, 4001])
                return dict(tokens=canonical.copy())
            if path == "completion":
                self.assertIn(body["model"], (self.base, self.child))
                self.assertEqual(1, body["n_predict"])
                self.assertEqual(canonical, body["prompt"])
                state[body["model"]] = canonical.copy()
                if extra_token:
                    state[body["model"]].append(3000)
                if corrupt_token:
                    state[body["model"]][-1] = 3000
                reused = 0 if cold else 200
                return dict(tokens_predicted=sampled, tokens_cached=len(state[body["model"]]),
                    timings=dict(cache_n=reused, prompt_n=len(canonical) - reused))
            if "action=erase" in path:
                state[body["model"]] = []
                return dict(success=True)
            if "action=save" in path:
                vector = state[body["model"]]
                encoded = struct.pack("<IIIiII", 0x67677371, 3, len(vector) + 4, -1, 1, len(vector))
                encoded += struct.pack("<" + "i" * len(vector), *vector) + struct.pack("<I", 0) + b"mock-KV"
                (self.root / body["filename"]).write_bytes(encoded)
                return dict(n_saved=len(vector), n_written=len(encoded))
            if "action=restore" in path:
                state[body["model"]] = NativeSessionCache._snapshot_tokens(self.root / body["filename"])
                return dict(n_restored=len(state[body["model"]]))
            raise AssertionError(path)

        cache = NativeSessionCache(self.root, router, lambda model: dict(files="same", context=32768,
            placement="CUDA1" if model == self.child else "CUDA0"), free_reserve=0, estimate_bytes=lambda *_: 1024)
        cache.prepare(self.base, "parent")
        calls.clear()
        prefill = dict(messages=[dict(role="user", content="delegated task")], tools=[], reasoning_effort="none")
        return cache, state, calls, canonical, source_tokens, prefill

    def test_first_child_before_any_parent_inference_prefills_canonical_history_directly_on_secondary(self):
        cache, state, calls, canonical, _, prefill = self.canonical_cache(cold=True)
        state[self.base] = []
        cache.resident.clear()
        result = cache.fork(self.base, "parent", self.child, "child", prefill)
        self.assertEqual("forked", result["status"])
        self.assertEqual("coldCanonical", result["preparationMode"])
        self.assertEqual((0, len(canonical)), (result["sourceCachedTokens"], result["sourceEvaluatedTokens"]))
        self.assertEqual([], state[self.base])
        self.assertEqual(canonical, state[self.child])
        self.assertEqual("child", cache.resident[self.child]["key"])
        self.assertNotIn(self.base, cache.resident)
        self.assertFalse((self.root / (cache._identity(self.base, "parent") + ".bin")).exists())
        self.assertEqual([self.child], [body["model"] for path, body in calls if path == "completion"])
        self.assertFalse(any("action=restore" in path for path, _ in calls))
        count = len(calls)
        self.assertEqual("resident", cache.fork(self.base, "parent", self.child, "child", prefill)["status"])
        self.assertFalse(any(path == "completion" for path, _ in calls[count:]))

    def test_first_shared_child_preserves_another_owner_without_creating_a_fake_parent_snapshot(self):
        cache, state, calls, canonical, _, prefill = self.canonical_cache(cold=True)
        cache.resident.clear()
        state[self.base] = [700, 701, 702]
        cache.prepare(self.base, "foreign")
        calls.clear()
        result = cache.fork(self.base, "parent", self.base, "child", prefill)
        self.assertEqual("forked", result["status"])
        self.assertEqual("coldCanonical", result["preparationMode"])
        self.assertEqual(0, result["sourceCachedTokens"])
        self.assertEqual(canonical, state[self.base])
        self.assertEqual("child", cache.resident[self.base]["key"])
        foreign_file = self.root / (cache._identity(self.base, "foreign") + ".bin")
        self.assertEqual([700, 701, 702], cache._snapshot_tokens(foreign_file))
        self.assertFalse((self.root / (cache._identity(self.base, "parent") + ".bin")).exists())
        self.assertEqual("restored", cache.prepare(self.base, "foreign")["status"])
        self.assertEqual([700, 701, 702], state[self.base])
        self.assertEqual("restored", cache.prepare(self.base, "child")["status"])
        self.assertEqual(canonical, state[self.base])

    def test_first_secondary_child_preserves_the_secondary_slots_previous_owner_and_leaves_primary_alone(self):
        cache, state, _, canonical, source, prefill = self.canonical_cache(cold=True)
        cache.resident.clear()
        state[self.child] = [600, 601]
        cache.prepare(self.child, "other-child")
        result = cache.fork(self.base, "parent", self.child, "child", prefill)
        self.assertEqual("forked", result["status"])
        self.assertEqual(source, state[self.base])
        self.assertEqual(canonical, state[self.child])
        self.assertEqual([600, 601], cache._snapshot_tokens(
            self.root / (cache._identity(self.child, "other-child") + ".bin")))
        self.assertFalse((self.root / (cache._identity(self.base, "parent") + ".bin")).exists())

    def test_cancelled_first_shared_child_restores_actual_previous_owner_and_never_creates_parent(self):
        cache, state, calls, canonical, _, prefill = self.canonical_cache(cold=True)
        cache.resident.clear()
        previous = [700, 701, 702]
        state[self.base] = previous.copy()
        cache.prepare(self.base, "foreign")

        def interrupted(body, cancelled):
            state[body["model"]] = canonical[:50]
            raise PreparationCancelledError("caller stopped")

        cache.prepare_completion = interrupted
        result = cache.fork(self.base, "parent", self.base, "child", prefill)
        self.assertEqual("subagent.preparation_cancelled", result["reason"])
        self.assertEqual(previous, state[self.base])
        self.assertEqual("foreign", cache.resident[self.base]["key"])
        self.assertTrue(any("action=restore" in path for path, _ in calls))
        cache.save_all()
        self.assertEqual(previous, cache._snapshot_tokens(self.root / (cache._identity(self.base, "foreign") + ".bin")))
        self.assertFalse((self.root / (cache._identity(self.base, "parent") + ".bin")).exists())
        self.assertFalse((self.root / (cache._identity(self.base, "child") + ".bin")).exists())

    def test_cancelled_first_child_with_no_previous_owner_drains_and_erases_partial_unowned_state(self):
        cache, state, calls, canonical, _, prefill = self.canonical_cache(cold=True)
        cache.resident.clear()
        state[self.base] = []

        def interrupted(body, cancelled):
            state[body["model"]] = canonical[:50]
            raise PreparationTimeoutError("no progress")

        cache.prepare_completion = interrupted
        result = cache.fork(self.base, "parent", self.base, "child", prefill)
        self.assertEqual("subagent.preparation_timeout", result["reason"])
        self.assertEqual([], state[self.base])
        self.assertNotIn(self.base, cache.resident)
        self.assertTrue(any("action=erase" in path for path, _ in calls))
        cache.save_all()
        self.assertFalse(list(self.root.glob("*.bin")))

    def test_first_child_cannot_overwrite_a_foreign_owner_when_its_safety_save_fails(self):
        cache, state, calls, _, _, prefill = self.canonical_cache(cold=True)
        cache.resident.clear()
        foreign = [700, 701, 702]
        state[self.base] = foreign.copy()
        cache.prepare(self.base, "foreign")
        original = cache.router

        def router(path, body=None, timeout=15):
            if "action=save" in path:
                raise OSError("foreign owner cannot be saved")
            return original(path, body, timeout)

        cache.router = router
        calls.clear()
        result = cache.fork(self.base, "parent", self.base, "child", prefill)
        self.assertEqual("unavailable", result["status"])
        self.assertIn("foreign owner cannot be saved", result["detail"])
        self.assertEqual(foreign, state[self.base])
        self.assertEqual("foreign", cache.resident[self.base]["key"])
        self.assertFalse(any(path == "completion" for path, _ in calls))

    def test_canonical_fork_reuses_source_checkpoints_saves_no_generated_tokens_and_retains_parent_file(self):
        cache, state, calls, canonical, source, prefill = self.canonical_cache()
        result = cache.fork(self.base, "parent", self.child, "child", prefill)
        self.assertEqual("forked", result["status"])
        self.assertEqual(len(canonical), result["restoredTokens"])
        self.assertEqual(200, result["sourceCachedTokens"])
        self.assertEqual(1, result["preparationSampledTokens"])
        self.assertEqual(0, result["evaluatedGeneratedTokens"])
        parent_file = self.root / (cache._identity(self.base, "parent") + ".bin")
        child_file = self.root / (cache._identity(self.child, "child") + ".bin")
        self.assertEqual(source, cache._snapshot_tokens(parent_file))
        self.assertEqual(canonical, cache._snapshot_tokens(child_file))
        self.assertEqual(canonical, state[self.child])
        self.assertEqual([self.child], [body["model"] for path, body in calls if "action=restore" in path])
        self.assertEqual([self.base], [body["model"] for path, body in calls if path == "completion"])
        self.assertFalse(cache.resident[self.base]["dirty"])
        cache.save_all()
        self.assertEqual(source, cache._snapshot_tokens(parent_file))
        # The child's actual first generation appends its native assistant header
        # after the complete saved vector. It needs no historical rollback.
        first_child_prompt = [*canonical, 4000, 4001]
        shared = next((index for index, (old, new) in enumerate(zip(state[self.child], first_child_prompt))
                       if old != new), len(state[self.child]))
        self.assertEqual(len(canonical), shared)
        count = len([path for path, _ in calls if path == "completion"])
        self.assertEqual("resident", cache.fork(self.base, "parent", self.child, "child", prefill)["status"])
        self.assertEqual(count, len([path for path, _ in calls if path == "completion"]))

    def test_canonical_fork_accepts_exact_cold_replay_and_reports_no_cache_reuse(self):
        cache, state, calls, canonical, source, prefill = self.canonical_cache(cold=True)
        result = cache.fork(self.base, "parent", self.child, "child", prefill)
        self.assertEqual("forked", result["status"])
        self.assertEqual(0, result["sourceCachedTokens"])
        self.assertEqual(len(canonical), result["sourceEvaluatedTokens"])
        self.assertEqual("replayed", result["preparationMode"])
        self.assertEqual(canonical, state[self.child])
        self.assertEqual([self.child], [body["model"] for path, body in calls if "action=restore" in path])
        self.assertEqual(source, cache._snapshot_tokens(self.root / (cache._identity(self.base, "parent") + ".bin")))

    def test_canonical_fork_rejects_template_that_rewrites_prefix_before_any_preparation_inference(self):
        cache, _, calls, _, source, prefill = self.canonical_cache(rewrite_prefix=True)
        result = cache.fork(self.base, "parent", self.child, "child", prefill)
        self.assertEqual("unavailable", result["status"])
        self.assertIn("does not append", result["detail"])
        self.assertFalse(any(path == "completion" for path, _ in calls))
        self.assertEqual(source, cache._snapshot_tokens(self.root / (cache._identity(self.base, "parent") + ".bin")))

    def test_canonical_fork_rejects_evaluated_generated_token_and_same_length_corruption(self):
        for options in [dict(extra_token=True), dict(corrupt_token=True), dict(sampled=2)]:
            with self.subTest(options=options):
                cache, _, calls, _, source, prefill = self.canonical_cache(**options)
                result = cache.fork(self.base, "parent", self.child, "child", prefill)
                self.assertEqual("unavailable", result["status"])
                self.assertEqual([self.base], [body["model"] for path, body in calls if "action=restore" in path])
                self.assertEqual(source, cache._snapshot_tokens(self.root / (cache._identity(self.base, "parent") + ".bin")))
                self.assertFalse((self.root / (cache._identity(self.child, "child") + ".bin")).exists())
                self.assertFalse(list(self.root.glob("*.pending")))

    def test_failed_or_cancelled_prefill_drains_then_restores_exact_parent_and_never_saves_branch(self):
        for failure in (PreparationCancelledError("caller stopped"), PreparationTimeoutError("no progress"),
                        RuntimeError("native stream lost")):
            with self.subTest(failure=type(failure).__name__):
                cache, state, calls, canonical, source, prefill = self.canonical_cache()

                def interrupted(body, cancelled):
                    state[self.base] = canonical[:50]
                    raise failure

                cache.prepare_completion = interrupted
                result = cache.fork(self.base, "parent", self.child, "child", prefill)
                self.assertEqual("unavailable", result["status"])
                self.assertEqual(source, state[self.base])
                restore_index = next(index for index, (path, _) in enumerate(calls) if "action=restore" in path)
                self.assertTrue(calls[restore_index - 1][0].startswith("slots?"))
                self.assertEqual([self.base], [body["model"] for path, body in calls if "action=restore" in path])
                cache.save_all()
                self.assertEqual(source, cache._snapshot_tokens(self.root / (cache._identity(self.base, "parent") + ".bin")))
                self.assertFalse((self.root / (cache._identity(self.child, "child") + ".bin")).exists())
                self.assertFalse(list(self.root.glob("*.pending")))

    def test_failed_parent_recovery_removes_resident_owner_to_prevent_branch_save(self):
        cache, state, calls, canonical, source, prefill = self.canonical_cache()
        original_router = cache.router

        def router(path, body=None, timeout=15):
            if "action=restore" in path:
                raise RuntimeError("restore unavailable")
            return original_router(path, body, timeout)

        def interrupted(body, cancelled):
            state[self.base] = canonical[:50]
            raise PreparationCancelledError("caller stopped")

        cache.router, cache.prepare_completion = router, interrupted
        result = cache.fork(self.base, "parent", self.child, "child", prefill)
        self.assertIn("parent cache recovery required", result["detail"])
        self.assertNotIn(self.base, cache.resident)
        cache.save_all()
        self.assertEqual(source, cache._snapshot_tokens(self.root / (cache._identity(self.base, "parent") + ".bin")))

    def canonical_cache_with_foreign_owner(self):
        cache, state, calls, canonical, source, prefill = self.canonical_cache()
        cache.save(self.base, "parent")
        cache.prepare(self.base, "foreign-session")
        foreign = [700, 701, 702, 703]
        state[self.base] = foreign.copy()
        calls.clear()
        return cache, state, calls, canonical, source, prefill, foreign

    def test_fork_saves_foreign_owner_prepares_parent_and_recovers_correct_source_identity(self):
        cache, state, calls, canonical, source, prefill, foreign = self.canonical_cache_with_foreign_owner()

        def interrupted(body, cancelled):
            self.assertEqual(source, state[self.base])
            state[self.base] = canonical[:50]
            raise PreparationCancelledError("caller stopped")

        cache.prepare_completion = interrupted
        result = cache.fork(self.base, "parent", self.child, "child", prefill)
        self.assertEqual("unavailable", result["status"])
        self.assertEqual(source, state[self.base])
        self.assertEqual(cache._identity(self.base, "parent"), cache.resident[self.base]["identity"])
        self.assertEqual("parent", cache.resident[self.base]["key"])
        self.assertFalse(cache.resident[self.base]["dirty"])
        cache.save_all()
        self.assertEqual(foreign, cache._snapshot_tokens(self.root / (cache._identity(self.base, "foreign-session") + ".bin")))
        self.assertEqual(source, cache._snapshot_tokens(self.root / (cache._identity(self.base, "parent") + ".bin")))
        first_restore = next(index for index, (path, _) in enumerate(calls) if "action=restore" in path)
        self.assertTrue(any("action=save" in path for path, _ in calls[:first_restore]))

    def test_foreign_owner_save_failure_prevents_parent_restore_and_inference(self):
        cache, state, calls, _, source, prefill, foreign = self.canonical_cache_with_foreign_owner()
        original_router = cache.router

        def router(path, body=None, timeout=15):
            if "action=save" in path:
                raise OSError("foreign session cannot be saved")
            return original_router(path, body, timeout)

        cache.router = router
        result = cache.fork(self.base, "parent", self.child, "child", prefill)
        self.assertEqual("unavailable", result["status"])
        self.assertIn("foreign session cannot be saved", result["detail"])
        self.assertEqual(foreign, state[self.base])
        self.assertEqual("foreign-session", cache.resident[self.base]["key"])
        self.assertFalse(any("action=restore" in path or path in ("apply-template", "completion") for path, _ in calls))
        self.assertEqual(source, cache._snapshot_tokens(self.root / (cache._identity(self.base, "parent") + ".bin")))

    def test_parent_prepare_failure_keeps_foreign_snapshot_and_does_not_start_inference(self):
        cache, state, calls, _, source, prefill, foreign = self.canonical_cache_with_foreign_owner()
        original_router = cache.router

        def router(path, body=None, timeout=15):
            if "action=restore" in path:
                state[self.base] = [999]
                raise RuntimeError("parent restore failed")
            return original_router(path, body, timeout)

        cache.router = router
        result = cache.fork(self.base, "parent", self.child, "child", prefill)
        self.assertEqual("unavailable", result["status"])
        self.assertIn("parent restore failed", result["detail"])
        self.assertNotIn(self.base, cache.resident)
        self.assertFalse(any(path in ("apply-template", "completion") for path, _ in calls))
        cache.save_all()
        self.assertEqual(foreign, cache._snapshot_tokens(self.root / (cache._identity(self.base, "foreign-session") + ".bin")))
        self.assertEqual(source, cache._snapshot_tokens(self.root / (cache._identity(self.base, "parent") + ".bin")))

    def test_failed_child_restore_recovers_parent_and_removes_partial_child_owner_and_files(self):
        cache, state, _, _, source, prefill = self.canonical_cache()
        original_router = cache.router

        def router(path, body=None, timeout=15):
            if "action=restore" in path and body["model"] == self.child:
                state[self.child] = [999]
                raise RuntimeError("child restore failed halfway")
            return original_router(path, body, timeout)

        cache.router = router
        result = cache.fork(self.base, "parent", self.child, "child", prefill)
        self.assertEqual("unavailable", result["status"])
        self.assertIn("child restore failed", result["detail"])
        self.assertEqual(source, state[self.base])
        self.assertNotIn(self.child, cache.resident)
        cache.save_all()
        child = self.root / (cache._identity(self.child, "child") + ".bin")
        self.assertFalse(child.exists())
        self.assertFalse(child.with_suffix(".json").exists())
        self.assertEqual(source, cache._snapshot_tokens(self.root / (cache._identity(self.base, "parent") + ".bin")))

    def test_cancel_before_prefill_does_not_mutate_or_restore_a_slot(self):
        cache, _, calls, _, _, prefill = self.canonical_cache()
        result = cache.fork(self.base, "parent", self.child, "child", prefill, cancelled=lambda: True)
        self.assertEqual("subagent.preparation_cancelled", result["reason"])
        self.assertEqual([], calls)

    def test_inconsistent_token_accounting_restores_parent_instead_of_accepting_branch(self):
        cache, state, _, canonical, source, prefill = self.canonical_cache()

        def inconsistent(body, cancelled):
            state[self.base] = canonical.copy()
            return dict(tokens_predicted=1, timings=dict(cache_n=0, prompt_n=len(canonical) - 1))

        cache.prepare_completion = inconsistent
        result = cache.fork(self.base, "parent", self.child, "child", prefill)
        self.assertEqual("unavailable", result["status"])
        self.assertIn("inconsistent token accounting", result["detail"])
        self.assertEqual(source, state[self.base])
        self.assertFalse((self.root / (cache._identity(self.child, "child") + ".bin")).exists())

    def test_control_prepare_reports_timeout_without_claiming_child_availability(self):
        with patch.object(self.manager, "agent_status", return_value=dict(allowed=True, modelId=self.base, instanceId=self.child)), \
             patch.object(self.manager, "load"), patch.object(self.manager.sessions, "fork", return_value=dict(
                 status="unavailable", reason="subagent.preparation_timeout", detail="No token progress")):
            result = self.manager.agent_prepare(self.base, "parent", "child", {})
        self.assertFalse(result["allowed"])
        self.assertEqual("subagent.preparation_timeout", result["reason"])

    def native_preparation_transport(self, events, cancelled=None):
        payload = b"".join(b"data: " + json.dumps(event).encode() + b"\n\n" for event in events)
        response = io.BytesIO(payload)
        response.status = 200
        connection = MagicMock()
        connection.getresponse.return_value = response
        with patch.object(catalog.http.client, "HTTPConnection", return_value=connection):
            try:
                return self.manager.prepare_completion(dict(model=self.base, prompt=[1, 2, 3], n_predict=1), cancelled)
            finally:
                connection.sock.shutdown.assert_called()
                connection.close.assert_called_once()
                self.assertTrue(response.closed)
                sent = json.loads(connection.request.call_args.args[2])
                self.assertTrue(sent["stream"])
                self.assertTrue(sent["return_progress"])
                self.assertEqual(1, sent["n_predict"])

    def test_native_preparation_stream_returns_final_accounting_and_closes_transport(self):
        final = dict(stop=True, tokens_predicted=1, timings=dict(cache_n=0, prompt_n=3))
        result = self.native_preparation_transport([
            dict(prompt_progress=dict(total=3, cache=0, processed=0, time_ms=0)),
            dict(prompt_progress=dict(total=3, cache=0, processed=3, time_ms=100)), final])
        self.assertEqual(final, result)

    def test_native_preparation_stream_missing_final_accounting_is_closed_and_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "without final token accounting"):
            self.native_preparation_transport([dict(prompt_progress=dict(processed=3))])

    def test_native_preparation_stream_caller_cancel_closes_inference_connection(self):
        calls = []

        def cancelled():
            calls.append(True)
            return len(calls) >= 3

        with self.assertRaises(PreparationCancelledError):
            self.native_preparation_transport([dict(prompt_progress=dict(processed=0))], cancelled)


if __name__ == "__main__":
    unittest.main()
