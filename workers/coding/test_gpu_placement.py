import importlib.util
import json
import copy
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from test_catalog import gguf

spec = importlib.util.spec_from_file_location("gpu_catalog", Path(__file__).with_name("catalog.py"))
catalog = importlib.util.module_from_spec(spec)
spec.loader.exec_module(catalog)


class GpuPlacementTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        gguf(self.root / "Qwen3.8-27B.gguf")
        self.model = catalog.discover_models(self.root)[0]
        self.gpus = [{"index": 0, "device": "CUDA0", "free": 48 * 1024**3},
                     {"index": 1, "device": "CUDA1", "free": 40 * 1024**3}]

    def test_qwen_prefers_physical_gpu_one_and_respects_actual_free_memory(self):
        self.assertEqual(catalog.choose_single_gpu(self.model, self.gpus), "CUDA1")
        self.gpus[1]["free"] = 0
        self.assertEqual(catalog.choose_single_gpu(self.model, self.gpus), "CUDA0")
        self.gpus[0]["free"] = 0
        self.assertIsNone(catalog.choose_single_gpu(self.model, self.gpus))

    def test_other_models_choose_most_free_memory(self):
        self.model["id"] = "coding/new-future-model"
        self.assertEqual(catalog.choose_single_gpu(self.model, self.gpus), "CUDA0")

    def test_shard_sizes_include_every_file(self):
        first = self.root / "large-00001-of-00002.gguf"
        second = self.root / "large-00002-of-00002.gguf"
        first.write_bytes(b"1" * 100)
        second.write_bytes(b"2" * 200)
        self.assertEqual(catalog.model_file_bytes({"path": first}), 300)

    def scenario(self, error):
        manager = catalog.GpuLoadManager(self.root, self.root / "models.ini", self.root, 8081)
        attempts = []
        def router(path, body=None):
            if path == "models/load":
                attempts.append(manager.placements[self.model["id"]])
                if len(attempts) == 1:
                    (self.root / "llama.stderr.log").write_text(error)
                return {}
            if path.startswith("v1/models"):
                failed = len(attempts) == 1
                return {"data": [{"id": self.model["id"], "status": {
                    "value": "unloaded" if len(attempts) < 2 else "loaded", "failed": failed}}]}
            raise AssertionError(path)
        manager.router = router
        return manager, attempts

    def test_allocation_failure_retries_once_with_multi_gpu_and_preserves_full_single_context(self):
        manager, attempts = self.scenario("CUDA error: out of memory")
        with patch.object(catalog, "gpu_inventory", return_value=self.gpus):
            result = manager.load(self.model["id"])
        self.assertTrue(result["fallback"])
        self.assertEqual(attempts, ["CUDA1", None])
        self.assertNotIn("device =", manager.preset.read_text())
        self.assertNotIn("ctx-size", manager.preset.read_text())
        self.assertIn('"outcome": "failed"', (self.root / "gpu-placement.jsonl").read_text())

    def test_non_memory_failure_does_not_repeat(self):
        manager, attempts = self.scenario("invalid model tensor")
        with patch.object(catalog, "gpu_inventory", return_value=self.gpus):
            with self.assertRaises(RuntimeError):
                manager.load(self.model["id"])
        self.assertEqual(attempts, ["CUDA1"])
        text = manager.preset.read_text()
        self.assertIn("device = CUDA1", text)
        self.assertIn("split-mode = none", text)
        self.assertIn("fit = off", text)
        self.assertIn("ctx-size = 32768", text)
        manager.refresh()
        self.assertEqual(text, manager.preset.read_text())

    def test_gpu_enumeration_uses_pci_order_without_changing_physical_preference(self):
        with patch.object(catalog.subprocess, "run", return_value=type("Result", (), {"stdout": "0, 0000:80:00.0, 10000\n1, 0000:20:00.0, 20000\n"})()):
            devices = catalog.gpu_inventory()
        self.assertEqual(devices[0]["index"], 1)
        self.assertEqual(devices[0]["device"], "CUDA0")

    def successful_manager(self):
        manager = catalog.GpuLoadManager(self.root, self.root / "models.ini", self.root, 8081)
        state, calls = {"loaded": False}, []

        def router(path, body=None):
            calls.append((path, body))
            if path == "models/load":
                state["loaded"] = True
                return {}
            if path.startswith("v1/models"):
                return {"data": [{"id": self.model["id"], "status": {
                    "value": "loaded" if state["loaded"] else "unloaded"}}]}
            raise AssertionError(path)

        manager.router = router
        return manager, state, calls

    def test_active_multi_gpu_policy_and_cache_identity_ignore_environment_changes(self):
        manager, _, calls = self.successful_manager()
        with patch.object(catalog, "gpu_inventory", return_value=[]), patch.dict(
                catalog.os.environ, {"MISSUM_NATIVE_MULTI_GPU_SPLIT": "row"}):
            manager.load(self.model["id"])
            preset = manager.preset.read_text()
            fingerprint = manager.session_fingerprint(self.model["id"])
            policy = dict(manager.policies[self.model["id"]])
            self.assertEqual(fingerprint["split"], "row")
            with patch.dict(catalog.os.environ, {"MISSUM_NATIVE_MULTI_GPU_SPLIT": "tensor",
                                                "MISSUM_NATIVE_MULTI_GPU_CONTEXT_LIMIT": "invalid"}):
                manager.refresh()
                self.assertEqual(preset, manager.preset.read_text())
                self.assertEqual(fingerprint, manager.session_fingerprint(self.model["id"]))
                self.assertTrue(manager.load(self.model["id"])["reused"])
        self.assertEqual(1, sum(path == "models/load" for path, _ in calls))
        events = [json.loads(line) for line in (self.root / "gpu-placement.jsonl").read_text().splitlines()]
        self.assertEqual(["loading", "loaded"], [event["outcome"] for event in events])
        self.assertTrue(all(event["settings"] == policy for event in events))

    def test_next_actual_load_resolves_new_policy_and_changes_cache_identity(self):
        manager, state, _ = self.successful_manager()
        with patch.object(catalog, "gpu_inventory", return_value=[]):
            with patch.dict(catalog.os.environ, {"MISSUM_NATIVE_MULTI_GPU_SPLIT": "layer"}):
                manager.load(self.model["id"])
                first = manager.session_fingerprint(self.model["id"])
            state["loaded"] = False
            with patch.dict(catalog.os.environ, {"MISSUM_NATIVE_MULTI_GPU_SPLIT": "row"}):
                manager.load(self.model["id"])
                second = manager.session_fingerprint(self.model["id"])
        self.assertEqual(first["split"], "layer")
        self.assertEqual(second["split"], "row")
        self.assertNotEqual(first, second)

    def test_default_layer_and_single_gpu_keep_existing_snapshot_identity(self):
        for devices, placement in (([], None), (self.gpus, "CUDA1")):
            with self.subTest(placement=placement):
                manager, _, _ = self.successful_manager()
                with patch.object(catalog, "gpu_inventory", return_value=devices), patch.dict(
                        catalog.os.environ, {"MISSUM_NATIVE_MULTI_GPU_SPLIT": "layer"}):
                    manager.load(self.model["id"])
                path = self.model["path"]
                expected = dict(version=1,
                                files=[(str(path.resolve()), path.stat().st_size, path.stat().st_mtime_ns)],
                                context=32768, cache="q8_0", slots=1, template=None,
                                placement=placement, fit="2048", gpuLayers="auto", split="layer")
                self.assertEqual(manager.session_fingerprint(self.model["id"]), expected)

    def test_tensor_uses_explicit_context_without_fit_or_cpu_offload_and_freezes_limit(self):
        manager, _, _ = self.successful_manager()
        with patch.object(catalog, "gpu_inventory", return_value=[]), patch.dict(
                catalog.os.environ, {"MISSUM_NATIVE_MULTI_GPU_SPLIT": "tensor",
                                     "MISSUM_NATIVE_MULTI_GPU_CONTEXT_LIMIT": "16384"}):
            manager.load(self.model["id"])
            fingerprint = manager.session_fingerprint(self.model["id"])
            policy = fingerprint["runtimePolicy"]
            self.assertEqual((policy["split"], policy["fit"], policy["context"], policy["gpuLayers"]),
                             ("tensor", "off", 16384, "999"))
            self.assertEqual((policy["cacheK"], policy["cacheV"]), ("q8_0", "q8_0"))
            preset = manager.preset.read_text()
            block = preset.split("[" + self.model["id"] + "]", 1)[1]
            self.assertIn("split-mode = tensor\nfit = off\nn-gpu-layers = 999\nctx-size = 16384", block)
            self.assertNotIn("fit = on", block)
            with patch.dict(catalog.os.environ, {"MISSUM_NATIVE_MULTI_GPU_CONTEXT_LIMIT": "8192"}):
                manager.refresh()
                self.assertEqual(preset, manager.preset.read_text())
                self.assertEqual(fingerprint, manager.session_fingerprint(self.model["id"]))

    def test_tensor_rejects_missing_or_invalid_limit_before_any_load_or_cache_invalidation(self):
        for limit in ("", "0", "-1", "4095", "2147483648", "auto", "16k", "3.2"):
            with self.subTest(limit=limit):
                manager, _, calls = self.successful_manager()
                with patch.object(catalog, "gpu_inventory", return_value=[]), patch.object(
                        manager.sessions, "invalidate") as invalidate, patch.dict(
                            catalog.os.environ, {"MISSUM_NATIVE_MULTI_GPU_SPLIT": "tensor",
                                                 "MISSUM_NATIVE_MULTI_GPU_CONTEXT_LIMIT": limit}):
                    with self.assertRaisesRegex(ValueError, "MISSUM_NATIVE_MULTI_GPU_CONTEXT_LIMIT"):
                        manager.load(self.model["id"])
                self.assertFalse(any(path == "models/load" for path, _ in calls))
                invalidate.assert_not_called()

    def test_tensor_context_limit_never_exceeds_model_maximum_and_does_not_affect_single_gpu(self):
        with patch.dict(catalog.os.environ, {"MISSUM_NATIVE_MULTI_GPU_CONTEXT_LIMIT": "1048576"}):
            tensor = catalog.model_runtime_policy(self.model, split_mode="tensor")
        self.assertEqual(tensor["context"], 32768)
        with patch.dict(catalog.os.environ, {"MISSUM_NATIVE_MULTI_GPU_CONTEXT_LIMIT": "invalid"}):
            single = catalog.model_runtime_policy(self.model, "CUDA1", split_mode="tensor")
        self.assertEqual((single["split"], single["context"], single["fit"]), ("none", 32768, "off"))

    def test_tensor_override_does_not_change_embedding_cache_or_context_policy(self):
        gguf(self.root / "embedding.gguf", architecture="bert", context=8192)
        embedding = next(model for model in catalog.discover_models(self.root) if model["role"] == "embedding")
        with patch.dict(catalog.os.environ, {"MISSUM_NATIVE_MULTI_GPU_CONTEXT_LIMIT": "invalid"}):
            policy = catalog.model_runtime_policy(embedding, split_mode="tensor")
        self.assertEqual((policy["split"], policy["context"], policy["cacheK"], policy["fit"]),
                         ("layer", 8192, "f16", "on"))


class MeasuredModelProfileTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        environment = patch.dict(catalog.os.environ, {}, clear=True)
        environment.start()
        self.addCleanup(environment.stop)
        gguf(self.root / "DeepSeek-00001-of-00003.gguf", architecture="deepseek4", tensors=0, context=1048576)
        for index in (2, 3):
            gguf(self.root / f"DeepSeek-{index:05d}-of-00003.gguf", architecture="deepseek4", context=1048576)
        gguf(self.root / "mmproj-F16.gguf", architecture="clip")
        self.model = next(model for model in catalog.discover_models(self.root) if model["role"] == "general")
        self.binary = self.root / "bin" / "llama-server.exe"
        self.binary.parent.mkdir()
        self.binary.write_bytes(b"validated executable")
        (self.binary.parent / "llama.dll").write_bytes(b"validated llama library")
        (self.binary.parent / "ggml-cuda.dll").write_bytes(b"validated CUDA library")
        self.gpus = [dict(pciBusId=f"00000000:{bus}:00.0", uuid=f"GPU-{index}", name="Quadro RTX 8000", memoryTotalMiB=49152)
                     for index, bus in enumerate(("20", "80"))]
        self.profile = dict(id="deepseek-validated", architecture="deepseek4", split="tensor", context="model-maximum",
                            cacheK="q8_0", cacheV="q8_0", allReduce="none", mmprojDevice="CUDA1", batchSize=1024, ubatchSize=256,
                            modelFiles=catalog.model_file_identity(self.model), runtimeFiles=catalog.runtime_file_identity(self.binary),
                            gpuPciBusIds=[gpu["pciBusId"] for gpu in self.gpus], gpuIdentities=self.gpus)

    def write_profile(self, profile=None):
        (self.root / "model-load-policy.json").write_text(
            json.dumps(dict(version=1, profiles=[profile or self.profile])), encoding="utf-8")

    def manager(self, failures=()):
        manager = catalog.GpuLoadManager(self.root, self.root / "models.ini", self.root, 8081, binary=self.binary)
        state, attempts = {"loaded": False, "failed": False}, []

        def router(path, body=None):
            if path == "models/load":
                attempts.append(dict(manager.policies[self.model["id"]]))
                failure = failures[len(attempts) - 1] if len(attempts) <= len(failures) else None
                state.update(loaded=not failure, failed=bool(failure))
                if failure:
                    with (self.root / "llama.stderr.log").open("a") as stream:
                        stream.write(failure + "\n")
                return {}
            if path.startswith("v1/models"):
                return dict(data=[dict(id=self.model["id"], status=dict(
                    value="loaded" if state["loaded"] else "unloaded", failed=state["failed"]))])
            raise AssertionError(path)

        manager.router = router
        return manager, state, attempts

    def load(self, manager, source=None):
        with patch.object(catalog, "gpu_inventory", return_value=[]), patch.object(
                catalog, "gpu_profile_inventory", return_value=self.gpus):
            child_environment = manager.configure_router_environment(source or {})
            result = manager.load(self.model["id"])
        return result, child_environment

    def test_exact_profile_sets_child_only_allreduce_and_full_context_parameters(self):
        self.write_profile()
        manager, _, attempts = self.manager()
        source = {"UNCHANGED": "value"}
        result, child = self.load(manager, source)
        self.assertTrue(result["success"])
        self.assertEqual(child["GGML_CUDA_ALLREDUCE"], "none")
        self.assertNotIn("GGML_CUDA_ALLREDUCE", source)
        self.assertNotIn("GGML_CUDA_ALLREDUCE", catalog.os.environ)
        self.assertNotIn("GGML_CUDA_DISABLE_GRAPHS", child)
        policy, = attempts
        self.assertEqual((policy["split"], policy["context"], policy["fit"], policy["gpuLayers"]),
                         ("tensor", 1048576, "off", "999"))
        block = manager.preset.read_text().split("[" + self.model["id"] + "]", 1)[1].split("\n[")[0]
        for parameter in ("mmproj-device = CUDA1", "batch-size = 1024", "ubatch-size = 256", "ctx-size = 1048576",
                          "device = CUDA0,CUDA1", "tensor-split = 1,1"):
            self.assertIn(parameter, block)
        fingerprint = manager.session_fingerprint(self.model["id"])
        self.assertEqual(fingerprint["runtimePolicy"]["batchSize"], 1024)
        self.assertEqual(fingerprint["runtimePolicy"]["allReduce"], "none")
        self.assertEqual(fingerprint["runtimePolicy"]["devices"], "CUDA0,CUDA1")
        self.assertEqual(fingerprint["runtimePolicy"]["tensorSplit"], "1,1")
        self.assertNotIn("profileId", fingerprint["runtimePolicy"])

    def test_profile_matches_same_weights_in_text_and_vision_but_not_other_models(self):
        self.write_profile()
        profiles, diagnostic = catalog.read_model_load_profiles(self.root)
        self.assertIsNone(diagnostic)
        models = catalog.discover_models(self.root)
        for model in models:
            self.assertEqual(catalog.matching_model_load_profile(profiles, model, self.binary, self.gpus), self.profile)
        gguf(self.root / "Qwen.gguf")
        qwen = next(model for model in catalog.discover_models(self.root) if "Qwen" in model["id"])
        self.assertIsNone(catalog.matching_model_load_profile(profiles, qwen, self.binary, self.gpus))

    def test_profile_freezes_settings_and_snapshot_when_local_file_changes(self):
        self.write_profile()
        manager, _, attempts = self.manager()
        self.load(manager)
        fingerprint, preset = manager.session_fingerprint(self.model["id"]), manager.preset.read_text()
        revised = dict(self.profile, batchSize=512, ubatchSize=128, mmprojDevice="CUDA0")
        self.write_profile(revised)
        with patch.object(catalog, "gpu_profile_inventory", return_value=self.gpus):
            manager.refresh()
            self.assertTrue(manager.load(self.model["id"])["reused"])
        self.assertEqual(preset, manager.preset.read_text())
        self.assertEqual(fingerprint, manager.session_fingerprint(self.model["id"]))
        self.assertEqual(len(attempts), 1)

    def test_human_allreduce_override_is_preserved_and_disables_unverified_profile_mode(self):
        self.write_profile()
        manager, _, attempts = self.manager()
        _, child = self.load(manager, {"GGML_CUDA_ALLREDUCE": "internal", "GGML_CUDA_DISABLE_GRAPHS": "1"})
        self.assertEqual(child["GGML_CUDA_ALLREDUCE"], "internal")
        self.assertEqual(child["GGML_CUDA_DISABLE_GRAPHS"], "1")
        self.assertEqual(attempts[0]["split"], "layer")
        self.assertIn("restart", (self.root / "gpu-placement.jsonl").read_text())

    def test_human_graphs_disable_skips_profile_even_with_validated_allreduce(self):
        self.write_profile()
        manager, _, attempts = self.manager()
        _, child = self.load(manager, {"GGML_CUDA_ALLREDUCE": "none", "GGML_CUDA_DISABLE_GRAPHS": "1"})
        self.assertEqual(child["GGML_CUDA_DISABLE_GRAPHS"], "1")
        self.assertEqual(attempts[0]["split"], "layer")
        self.assertIn("graphs-disable override", (self.root / "gpu-placement.jsonl").read_text())

    def test_explicit_split_environment_override_takes_precedence_over_local_profile(self):
        self.write_profile()
        manager, _, attempts = self.manager()
        with patch.dict(catalog.os.environ, {"MISSUM_NATIVE_MULTI_GPU_SPLIT": "layer"}):
            self.load(manager)
        self.assertEqual(attempts[0]["split"], "layer")
        self.assertNotIn("runtimePolicy", manager.session_fingerprint(self.model["id"]))

    def test_profile_added_after_startup_requires_restart_before_collective_override(self):
        manager, _, attempts = self.manager()
        manager.configure_router_environment({})
        self.write_profile()
        with patch.object(catalog, "gpu_inventory", return_value=[]), patch.object(
                catalog, "gpu_profile_inventory", return_value=self.gpus):
            manager.load(self.model["id"])
        self.assertEqual(attempts[0]["split"], "layer")
        self.assertIn("restart", (self.root / "gpu-placement.jsonl").read_text())

    def test_changed_shard_projector_runtime_or_gpu_identity_never_applies_profile(self):
        variants = []
        for key, position in (("modelFiles", 0), ("modelFiles", -1), ("runtimeFiles", 0), ("runtimeFiles", -1)):
            profile = copy.deepcopy(self.profile)
            profile[key][position][1] += 1
            variants.append(profile)
        for key, value in (("uuid", "GPU-replacement"), ("name", "Another GPU"), ("memoryTotalMiB", 24576)):
            profile = copy.deepcopy(self.profile)
            profile["gpuIdentities"][0][key] = value
            variants.append(profile)
        for index, profile in enumerate(variants):
            with self.subTest(index=index):
                self.write_profile(profile)
                manager, _, attempts = self.manager()
                _, child = self.load(manager)
                self.assertEqual(attempts[0]["split"], "layer")
                self.assertNotIn("GGML_CUDA_ALLREDUCE", child)

    def test_actual_new_runtime_dll_or_changed_model_file_invalidates_saved_profile(self):
        self.write_profile()
        (self.binary.parent / "new-runtime.dll").write_bytes(b"new library")
        manager, _, attempts = self.manager()
        _, child = self.load(manager)
        self.assertEqual(attempts[0]["split"], "layer")
        self.assertNotIn("GGML_CUDA_ALLREDUCE", child)
        self.profile["runtimeFiles"] = catalog.runtime_file_identity(self.binary)
        self.write_profile()
        shard = self.root / "DeepSeek-00003-of-00003.gguf"
        with shard.open("ab") as stream:
            stream.write(b"changed tensor bytes")
        manager, _, attempts = self.manager()
        _, child = self.load(manager)
        self.assertEqual(attempts[0]["split"], "layer")
        self.assertNotIn("GGML_CUDA_ALLREDUCE", child)

    def test_malformed_profile_reports_diagnostic_and_retains_default_layer(self):
        variants = ["not JSON", json.dumps(dict(version=2, profiles=[]))]
        for key, value in (("batchSize", 0), ("ubatchSize", 2048), ("architecture", "qwen3"), ("gpuIdentities", [])):
            profile = dict(self.profile, **{key: value})
            variants.append(json.dumps(dict(version=1, profiles=[profile])))
        for payload in variants:
            with self.subTest(payload=payload[:60]):
                (self.root / "model-load-policy.json").write_text(payload)
                manager, _, attempts = self.manager()
                self.load(manager)
                self.assertEqual(attempts[0]["split"], "layer")
                self.assertIn("ignored", (self.root / "gpu-placement.jsonl").read_text())

    def test_tensor_allocation_or_explicit_unsupported_failure_has_one_full_context_layer_fallback(self):
        for error in ("CUDA error: out of memory", "LLAMA_SPLIT_MODE_TENSOR not implemented for architecture 'deepseek4'"):
            with self.subTest(error=error):
                self.write_profile()
                manager, _, attempts = self.manager((error,))
                result, _ = self.load(manager)
                self.assertTrue(result["fallback"])
                self.assertEqual([policy["split"] for policy in attempts], ["tensor", "layer"])
                self.assertTrue(all(policy["context"] == 1048576 and policy["fit"] == "off" for policy in attempts))
                self.assertEqual(attempts[1]["mmprojDevice"], "CUDA1")

    def test_unrelated_model_error_has_no_tensor_fallback(self):
        self.write_profile()
        manager, _, attempts = self.manager(("invalid model tensor",))
        with self.assertRaises(RuntimeError):
            self.load(manager)
        self.assertEqual(len(attempts), 1)

    def test_full_context_layer_allocation_failure_is_terminal_without_reduction_or_loop(self):
        self.write_profile()
        manager, _, attempts = self.manager(("CUDA error: out of memory", "CUDA error: out of memory"))
        with self.assertRaises(RuntimeError):
            self.load(manager)
        self.assertEqual([policy["split"] for policy in attempts], ["tensor", "layer"])
        self.assertTrue(all(policy["context"] == 1048576 for policy in attempts))

    def layer_profile(self):
        profile = dict(self.profile, split="layer", batchSize=512, ubatchSize=128)
        profile.pop("allReduce")
        return profile

    def test_layer_profile_uses_full_context_and_no_collective_environment_changes(self):
        self.write_profile(self.layer_profile())
        manager, _, attempts = self.manager()
        source = {"GGML_CUDA_ALLREDUCE": "internal", "GGML_CUDA_DISABLE_GRAPHS": "1"}
        result, child = self.load(manager, source)
        self.assertTrue(result["success"])
        self.assertEqual(child["GGML_CUDA_ALLREDUCE"], "internal")
        self.assertEqual(child["GGML_CUDA_DISABLE_GRAPHS"], "1")
        policy, = attempts
        self.assertEqual((policy["split"], policy["context"], policy["fit"], policy["gpuLayers"]),
                         ("layer", 1048576, "off", "999"))
        self.assertNotIn("allReduce", policy)
        fingerprint = manager.session_fingerprint(self.model["id"])
        self.assertEqual(fingerprint["runtimePolicy"]["batchSize"], 512)
        self.assertEqual(fingerprint["runtimePolicy"]["ubatchSize"], 128)
        self.assertEqual(fingerprint["runtimePolicy"]["mmprojDevice"], "CUDA1")
        self.assertEqual(fingerprint["runtimePolicy"]["devices"], "CUDA0,CUDA1")
        self.assertEqual(fingerprint["runtimePolicy"]["tensorSplit"], "1,1")
        block = manager.preset.read_text().split("[" + self.model["id"] + "]", 1)[1].split("\n[")[0]
        self.assertIn("device = CUDA0,CUDA1\ntensor-split = 1,1", block)

    def test_layer_profile_added_after_startup_matches_without_environment_restart(self):
        manager, _, attempts = self.manager()
        environment = manager.configure_router_environment({})
        self.assertNotIn("GGML_CUDA_ALLREDUCE", environment)
        self.write_profile(self.layer_profile())
        with patch.object(catalog, "gpu_inventory", return_value=[]), patch.object(
                catalog, "gpu_profile_inventory", return_value=self.gpus):
            manager.load(self.model["id"])
        self.assertEqual(attempts[0]["profileId"], self.profile["id"])
        self.assertEqual(attempts[0]["batchSize"], 512)
        self.assertEqual(manager.router_allreduce, None)

    def test_layer_profile_settings_freeze_until_next_load_and_change_cache_identity(self):
        profile = self.layer_profile()
        self.write_profile(profile)
        manager, state, attempts = self.manager()
        self.load(manager)
        fingerprint = manager.session_fingerprint(self.model["id"])
        self.write_profile(dict(profile, batchSize=1024, ubatchSize=256, mmprojDevice="CUDA0"))
        manager.refresh()
        self.assertTrue(manager.load(self.model["id"])["reused"])
        self.assertEqual(fingerprint, manager.session_fingerprint(self.model["id"]))
        self.assertEqual(len(attempts), 1)
        state["loaded"] = False
        with patch.object(catalog, "gpu_inventory", return_value=[]), patch.object(
                catalog, "gpu_profile_inventory", return_value=self.gpus):
            manager.load(self.model["id"])
        self.assertNotEqual(fingerprint, manager.session_fingerprint(self.model["id"]))
        self.assertEqual(attempts[1]["batchSize"], 1024)
        self.assertEqual(attempts[1]["mmprojDevice"], "CUDA0")

    def test_layer_profile_inherits_only_to_same_weight_aliases_and_stale_profile_keeps_legacy_identity(self):
        profile = self.layer_profile()
        self.write_profile(profile)
        profiles, diagnostic = catalog.read_model_load_profiles(self.root)
        self.assertIsNone(diagnostic)
        for model in catalog.discover_models(self.root):
            self.assertEqual(catalog.matching_model_load_profile(profiles, model, self.binary, self.gpus), profile)
        profile = copy.deepcopy(profile)
        profile["gpuIdentities"][0]["uuid"] = "GPU-replacement"
        self.write_profile(profile)
        manager, _, attempts = self.manager()
        _, child = self.load(manager)
        self.assertNotIn("GGML_CUDA_ALLREDUCE", child)
        self.assertEqual(attempts[0]["split"], "layer")
        self.assertEqual(attempts[0]["fit"], "on")
        self.assertNotIn("runtimePolicy", manager.session_fingerprint(self.model["id"]))

    def test_layer_profile_rejects_collective_setting_instead_of_injecting_it(self):
        self.write_profile(dict(self.layer_profile(), allReduce="none"))
        manager, _, attempts = self.manager()
        _, child = self.load(manager)
        self.assertNotIn("GGML_CUDA_ALLREDUCE", child)
        self.assertEqual(attempts[0]["fit"], "on")
        self.assertIn("layer profiles leave AllReduce unset", (self.root / "gpu-placement.jsonl").read_text())

    def test_startup_layer_match_does_not_hide_later_tensor_profile_for_another_model(self):
        gguf(self.root / "Z-DeepSeek.gguf", architecture="deepseek4", context=1048576)
        second = next(model for model in catalog.discover_models(self.root)
                      if "Z-DeepSeek" in model["id"] and model["role"] == "general")
        tensor = dict(self.profile, id="second-model-tensor", modelFiles=catalog.model_file_identity(second))
        (self.root / "model-load-policy.json").write_text(json.dumps(
            dict(version=1, profiles=[self.layer_profile(), tensor])), encoding="utf-8")
        manager, _, _ = self.manager()
        with patch.object(catalog, "gpu_profile_inventory", return_value=self.gpus):
            environment = manager.configure_router_environment({})
        self.assertEqual(environment["GGML_CUDA_ALLREDUCE"], "none")
        events = [json.loads(line) for line in (self.root / "gpu-placement.jsonl").read_text().splitlines()]
        self.assertTrue(any(event["detail"].get("split") == "layer" for event in events))
        self.assertTrue(any(event["detail"].get("profileId") == "second-model-tensor" for event in events))
        self.assertFalse(any(event["outcome"] == "profile-ignored" for event in events))

    def test_layer_only_startup_retains_environment_without_false_no_match_diagnostic(self):
        self.write_profile(self.layer_profile())
        manager, _, _ = self.manager()
        with patch.object(catalog, "gpu_profile_inventory", return_value=self.gpus):
            environment = manager.configure_router_environment({"UNCHANGED": "value"})
        self.assertEqual(environment["UNCHANGED"], "value")
        self.assertNotIn("GGML_CUDA_ALLREDUCE", environment)
        events = [json.loads(line) for line in (self.root / "gpu-placement.jsonl").read_text().splitlines()]
        self.assertTrue(events)
        self.assertTrue(all(event["outcome"] == "profile-environment" for event in events))

    def test_unprofiled_multi_gpu_load_keeps_automatic_device_allocation(self):
        manager, _, attempts = self.manager()
        with patch.dict(catalog.os.environ, {"MISSUM_NATIVE_MULTI_GPU_SPLIT": "layer"}):
            self.load(manager)
        self.assertNotIn("devices", attempts[0])
        self.assertNotIn("tensorSplit", attempts[0])
        block = manager.preset.read_text().split("[" + self.model["id"] + "]", 1)[1].split("\n[")[0]
        self.assertNotIn("device =", block)
        self.assertNotIn("tensor-split =", block)


if __name__ == "__main__":
    unittest.main()
