import configparser
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from test_catalog import catalog, gguf


class MtpRuntimeTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        gguf(self.root / "ExampleModel.gguf")
        self.model = catalog.discover_models(self.root)[0]
        self.model_id = self.model["id"]
        self.draft = self.root / "mtp-ExampleModel-Q8_0.gguf"
        gguf(self.draft)
        self.model["mtp"] = self.draft

        discovery = patch.object(catalog, "discover_models", side_effect=lambda _: [self.model])
        discovery.start()
        self.addCleanup(discovery.stop)
        environment = patch.dict(catalog.os.environ, {"MISSUM_NATIVE_MULTI_GPU_SPLIT": "layer"})
        environment.start()
        self.addCleanup(environment.stop)
        self.manager = catalog.GpuLoadManager(self.root, self.root / "models.ini", self.root, 8081)

    def preset_block(self):
        presets = configparser.ConfigParser(interpolation=None)
        presets.read_string("[preset-header]\n" + self.manager.preset.read_text(encoding="utf-8"))
        return dict(presets[self.model_id])

    def router_loads(self, failures=()):
        attempts = []
        status = {"value": "unloaded"}

        def router(path, body=None, timeout=15):
            if path.startswith("v1/models"):
                return {"data": [{"id": self.model_id, "status": dict(status)}]}
            if path.startswith("props?"):
                return {"default_generation_settings": {"n_ctx": int(attempts[-1]["ctx-size"])}}
            if path == "models/load":
                self.assertEqual({"model": self.model_id}, body)
                attempts.append(self.preset_block())
                index = len(attempts) - 1
                if index < len(failures):
                    status.update(value="unloaded", failed=True)
                    with (self.root / "llama.stderr.log").open("a", encoding="utf-8") as log:
                        log.write(failures[index] + "\n")
                else:
                    status.update(value="loaded", failed=False)
                return {}
            self.fail(f"Unexpected native call: {path}")

        self.manager.router = router
        return attempts

    def test_managed_catalog_defers_mtp_to_the_explicit_model_load(self):
        self.manager.refresh()
        self.assertNotIn("spec-type", self.preset_block())
        self.assertNotIn("spec-draft-model", self.preset_block())

        catalog.write_presets(self.root, self.manager.preset)
        preset = self.preset_block()
        self.assertEqual("draft-mtp", preset["spec-type"])
        self.assertEqual(self.draft.as_posix(), preset["spec-draft-model"])
        self.assertEqual("2", preset["spec-draft-n-max"])

    def test_mtp_load_uses_the_resolved_sidecar_and_distinct_cache_identity(self):
        self.manager.refresh()
        ordinary_identity = self.manager.session_fingerprint(self.model_id)
        attempts = self.router_loads()
        with patch.object(catalog, "gpu_inventory", return_value=[]):
            self.manager.load(self.model_id, 8192)
            live_identity = self.manager.session_fingerprint(self.model_id)
            replacement = self.root / "mtp-ExampleModel-Q4_0.gguf"
            gguf(replacement)
            self.model["mtp"] = replacement
            self.manager.refresh()
            self.assertTrue(self.manager.load(self.model_id, 8192)["reused"])
            self.assertEqual(live_identity, self.manager.session_fingerprint(self.model_id))

        self.assertEqual(1, len(attempts))
        self.assertEqual("draft-mtp", attempts[0]["spec-type"])
        self.assertEqual(self.draft.as_posix(), attempts[0]["spec-draft-model"])
        self.assertEqual(self.draft.as_posix(), self.preset_block()["spec-draft-model"])
        self.assertEqual("2", attempts[0]["spec-draft-n-max"])
        self.assertEqual("8192", attempts[0]["ctx-size"])
        self.assertNotIn("mtp", ordinary_identity)
        self.assertIn("mtp", live_identity)
        events = [json.loads(line) for line in (self.root / "gpu-placement.jsonl").read_text().splitlines()]
        self.assertEqual(1, sum(event["outcome"] == "mtp-applied" for event in events))

    def test_moe_performance_retry_keeps_context_and_mtp(self):
        attempts = self.router_loads(("CUDA error: out of memory",))
        with patch.object(catalog, "gpu_inventory", return_value=[]), patch.object(
                catalog, "moe_performance_options", return_value=dict(
                    moeCacheMiB=4096, batchSize=2048, ubatchSize=1024, promptLookup=True)):
            result = self.manager.load(self.model_id, 8192)

        self.assertTrue(result["fallback"])
        self.assertEqual(["8192", "8192"], [preset["ctx-size"] for preset in attempts])
        self.assertEqual(["draft-mtp,ngram-simple", "draft-mtp"], [preset["spec-type"] for preset in attempts])
        self.assertLessEqual(int(attempts[0]["spec-ngram-simple-size-n"]), int(attempts[0]["spec-ngram-simple-size-m"]))
        self.assertEqual("4096", attempts[0]["moe-cache-mib"])
        self.assertNotIn("moe-cache-mib", attempts[1])
        self.assertEqual(self.draft.as_posix(), attempts[1]["spec-draft-model"])

    def test_moe_performance_budget_requires_vram_pressure_and_available_ram(self):
        mib = 1024**2
        model = self.model
        policy = catalog.model_runtime_policy(model)
        devices = [{"device": "CUDA0", "free": 24_576 * mib}]
        metadata = {"general.architecture": "qwen4exp", "qwen4exp.expert_count": 512, "lazy_ple_bytes": 28_000 * mib}
        with patch.object(catalog, "model_metadata", return_value=metadata), patch.object(
                catalog, "native_server_help", return_value="--moe-cache-mib"), patch.object(
                catalog, "model_file_bytes", return_value=73_000 * mib), patch.object(
                catalog, "available_host_memory", return_value=100_000 * mib), patch.object(
                catalog, "estimate_replica_bytes", return_value=48_000 * mib) as estimate:
            constrained = catalog.moe_performance_options(model, policy, devices, "llama-server.exe")
            estimate.assert_called_once_with(model, model["context"], weight_bytes=45_000 * mib)
            full_resident = catalog.moe_performance_options(
                model, policy, [{"device": "CUDA0", "free": 64_000 * mib}], "llama-server.exe")

        self.assertEqual(5632, constrained["moeCacheMiB"])
        self.assertNotIn("moeCacheMiB", full_resident)
        self.assertEqual(1024, full_resident["ubatchSize"])

    def test_mtp_load_failure_preserves_existing_gpu_fallback_and_context_limit(self):
        devices = [{"index": 0, "device": "CUDA0", "free": 48 * 1024**3}]
        attempts = self.router_loads(("error loading MTP draft model", "CUDA error: out of memory"))
        with patch.object(catalog, "gpu_inventory", return_value=devices):
            result = self.manager.load(self.model_id, 8192)

        self.assertTrue(result["success"])
        self.assertTrue(result["fallback"])
        self.assertEqual(["CUDA0", "CUDA0", None], [preset.get("device") for preset in attempts])
        self.assertEqual(["draft-mtp", None, None], [preset.get("spec-type") for preset in attempts])
        self.assertEqual(["8192", "8192", "8192"], [preset["ctx-size"] for preset in attempts])
        self.assertNotIn("spec-draft-model", self.preset_block())
        self.assertNotIn("mtp", self.manager.session_fingerprint(self.model_id))
        self.manager.refresh()
        self.assertNotIn("spec-type", self.preset_block())
        events = [json.loads(line) for line in (self.root / "gpu-placement.jsonl").read_text().splitlines()]
        self.assertEqual(1, sum(event["outcome"] == "mtp-fallback" for event in events))

    def events(self):
        return [json.loads(line) for line in (self.root / "gpu-placement.jsonl").read_text().splitlines()]

    def test_draft_weights_are_counted_into_the_admission_size(self):
        plain = catalog.model_file_bytes({**self.model, "mtp": None})
        self.assertEqual(plain + self.draft.stat().st_size, catalog.model_file_bytes(self.model))
        with patch.dict(catalog.os.environ, {"MISSUM_MTP_DISABLED": "1"}):
            self.assertEqual(plain, catalog.model_file_bytes(self.model))

    def test_speculation_token_count_defaults_to_two_and_can_be_raised(self):
        self.assertEqual(2, catalog.mtp_draft_tokens())
        with patch.dict(catalog.os.environ, {"MISSUM_MTP_DRAFT_TOKENS": "5"}):
            self.assertEqual(5, catalog.mtp_draft_tokens())
            catalog.write_presets(self.root, self.manager.preset)
            self.assertEqual("5", self.preset_block()["spec-draft-n-max"])
        with patch.dict(catalog.os.environ, {"MISSUM_MTP_DRAFT_TOKENS": "0"}):
            self.assertEqual(2, catalog.mtp_draft_tokens())

    def test_environment_switch_turns_speculation_off(self):
        with patch.dict(catalog.os.environ, {"MISSUM_MTP_DISABLED": "1"}):
            catalog.write_presets(self.root, self.manager.preset)
            self.assertNotIn("spec-type", self.preset_block())
            self.assertNotIn("spec-draft-model", self.preset_block())

    def test_capability_probe_requires_the_exact_switches_in_the_help_output(self):
        catalog._MTP_CAPABILITY.clear()
        self.addCleanup(catalog._MTP_CAPABILITY.clear)
        help_text = ("--spec-type none,draft-simple,draft-mtp\n"
                     "--spec-draft-model, -md, --model-draft FNAME\n"
                     "--spec-draft-n-max N\n")
        with patch.object(catalog.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, help_text, "")):
            supported, reason = catalog.native_mtp_support("llama-server.exe")
        self.assertTrue(supported)
        self.assertEqual("speculation switches present", reason)
        self.assertTrue(catalog.native_mtp_support("llama-server.exe")[0])

    def test_capability_probe_rejects_a_router_without_the_switches(self):
        catalog._MTP_CAPABILITY.clear()
        self.addCleanup(catalog._MTP_CAPABILITY.clear)
        supported, reason = catalog.native_mtp_support(sys.executable)
        self.assertFalse(supported)
        self.assertIn("--spec-type", reason)

    def test_preset_omits_speculation_keys_and_load_still_succeeds_without_the_capability(self):
        attempts = self.router_loads()
        with patch.object(catalog, "native_mtp_support", return_value=(False, "missing --spec-type")), \
                patch.object(catalog, "gpu_inventory", return_value=[]):
            result = self.manager.load(self.model_id, 8192)
        self.assertTrue(result["success"])
        self.assertEqual([None], [preset.get("spec-type") for preset in attempts])
        self.assertNotIn("spec-draft-model", self.preset_block())
        self.assertIn("mtp-unsupported", [event["outcome"] for event in self.events()])

    def test_load_walks_the_companion_list_before_dropping_speculation(self):
        second = self.root / "mtp-ExampleModel-Q4_0.gguf"
        gguf(second)
        self.model["mtp_alternatives"] = [(second,)]
        attempts = self.router_loads(("error loading MTP draft model", "error loading MTP draft model"))
        with patch.object(catalog, "gpu_inventory", return_value=[]):
            result = self.manager.load(self.model_id, 8192)
        self.assertTrue(result["success"])
        self.assertEqual(["draft-mtp", "draft-mtp", None], [preset.get("spec-type") for preset in attempts])
        self.assertEqual([self.draft.as_posix(), second.as_posix()],
                         [preset["spec-draft-model"] for preset in attempts[:2]])
        outcomes = [event["outcome"] for event in self.events()]
        self.assertEqual(1, outcomes.count("mtp-avoided"))
        self.assertEqual(1, outcomes.count("mtp-fallback"))
        # Both companions failed, so the model that finally loaded ran without
        # speculation and must not be reported as an applied draft.
        self.assertNotIn("mtp-applied", outcomes)

    def test_companion_is_skipped_when_only_the_target_fits_the_device(self):
        fitting = catalog.model_file_bytes({**self.model, "mtp": None}) + 2048 * 1024 ** 2
        devices = [{"index": 0, "device": "CUDA0", "free": fitting}]
        attempts = self.router_loads()
        with patch.object(catalog, "gpu_inventory", return_value=devices):
            result = self.manager.load(self.model_id, 8192)
        self.assertTrue(result["success"])
        self.assertEqual([None], [preset.get("spec-type") for preset in attempts])
        self.assertIn("mtp-skipped-memory", [event["outcome"] for event in self.events()])
        self.assertIn("CUDA0", [event["device"] for event in self.events() if event["outcome"] == "loaded"])

    def test_mtp_load_failure_retries_plain_model_only_once_for_non_memory_errors(self):
        attempts = self.router_loads(("unsupported draft architecture", "invalid main model tensor"))
        with patch.object(catalog, "gpu_inventory", return_value=[]):
            with self.assertRaises(RuntimeError):
                self.manager.load(self.model_id)
        self.assertEqual(["draft-mtp", None], [preset.get("spec-type") for preset in attempts])


    def test_vanished_companion_is_neither_admitted_nor_speculated(self):
        plain = catalog.model_file_bytes({**self.model, "mtp": None})
        self.assertEqual(plain + self.draft.stat().st_size, catalog.model_file_bytes(self.model))
        self.draft.unlink()
        self.assertEqual(plain, catalog.model_file_bytes(self.model))
        catalog.write_presets(self.root, self.manager.preset)
        self.assertNotIn("spec-type", self.preset_block())
        attempts = self.router_loads()
        with patch.object(catalog, "gpu_inventory", return_value=[]):
            result = self.manager.load(self.model_id, 8192)
        self.assertTrue(result["success"])
        self.assertEqual([None], [preset.get("spec-type") for preset in attempts])
        self.assertNotIn("mtp", self.manager.session_fingerprint(self.model_id))

    def test_incomplete_split_companion_is_not_speculated(self):
        missing = self.root / "mtp-ExampleModel-Q8_0-00002-of-00002.gguf"
        self.model["mtp_shards"] = [self.draft, missing]
        attempts = self.router_loads()
        with patch.object(catalog, "gpu_inventory", return_value=[]):
            result = self.manager.load(self.model_id, 8192)
        self.assertTrue(result["success"])
        self.assertEqual([None], [preset.get("spec-type") for preset in attempts])
        self.assertNotIn("spec-draft-model", self.preset_block())


if __name__ == "__main__":
    unittest.main()
