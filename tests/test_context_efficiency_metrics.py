"""Pure journal fixtures: no gateway, runtime, model or client is started."""
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import importlib.util
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest


SPEC = importlib.util.spec_from_file_location(
    "context_efficiency", Path(__file__).resolve().parents[1] / "windows" / "measure-context-efficiency.py")
COLLECTOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(COLLECTOR)
MODEL = "coding/qwen-test-27b"
FIXTURE_HASH = "a" * 64
ACCEPTANCE_HASH = "b" * 64


class ContextEfficiencyMetricsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name)
        self.database = self.base / "journal.db"
        self.writer = sqlite3.connect(self.database)
        self.writer.executescript("""
            CREATE TABLE runs(run_id TEXT PRIMARY KEY,state TEXT,mode TEXT,selected_model TEXT,
                              request_json TEXT,created_at TEXT,updated_at TEXT);
            CREATE TABLE run_events(id INTEGER PRIMARY KEY AUTOINCREMENT,run_id TEXT,
                                    event_type TEXT,data_json TEXT,created_at TEXT);
        """)
        self.origin = datetime(2026, 10, 3, tzinfo=timezone.utc)

    def tearDown(self):
        self.writer.close()
        self.temp.cleanup()

    def stamp(self, seconds):
        return (self.origin + timedelta(seconds=seconds)).isoformat()

    def event(self, run_id, event_type, data, seconds=2):
        self.writer.execute("INSERT INTO run_events(run_id,event_type,data_json,created_at) VALUES(?,?,?,?)",
                            (run_id, event_type, json.dumps(data), self.stamp(seconds)))
        self.writer.commit()

    def metrics(self, cached=0):
        return dict(runtimeQueueMilliseconds=0, tokenCountingMilliseconds=12,
                    promptMilliseconds=100, generationMilliseconds=2000,
                    timeToFirstTokenMilliseconds=110, totalMilliseconds=2200,
                    promptEvaluatedTokens=100 - cached, cachedPromptTokens=cached,
                    reasoningTokens=None, inputTokens=100, outputTokens=10)

    def add_run(self, run_id, mode="general", profile="legacy", cached=0, offset=0,
            duration=10, parent=None, state="Completed", source="model.turn.metrics"):
        request = {"clientCapabilities": ["research.deliverables"] if mode == "science" else []}
        if parent:
            request["subagent"] = {"parentRunId": parent, "agentId": "agent-test"}
        self.writer.execute("INSERT INTO runs VALUES(?,?,?,?,?,?,?)",
                            (run_id, state, "Coding" if mode == "coding" else "Auto", MODEL,
                             json.dumps(request), self.stamp(offset), self.stamp(offset + duration)))
        self.event(run_id, "run.started", {}, offset + 1)
        self.event(run_id, source, {"round": 1, "phase": "main", "contextProfile": profile,
                                   "modelId": MODEL, "reasoningEffort": "high",
                                   "queueMilliseconds": 0, "metrics": self.metrics(cached),
                                   "inputTokens": 100, "outputTokens": 10, "toolCalls": 0}, offset + 2)
        if state == "Completed":
            self.event(run_id, "run.completed", {"inputTokens": 100, "outputTokens": 10}, offset + duration)

    def collect(self, run_id):
        reader = COLLECTOR.open_database(self.database)
        try:
            return COLLECTOR.collect_run(reader, run_id)
        finally:
            reader.close()

    def json_file(self, filename, value):
        path = self.base / filename
        path.write_text(json.dumps(value), encoding="utf-8")
        return filename, COLLECTOR.digest_file(path)

    def acceptance(self, reference, pair, run_ids):
        proof = {"passed": True, "fixtureSha256": pair["fixtureSha256"],
                 "acceptanceSha256": pair["acceptanceSha256"],
                 "checks": {check: True for check in pair["requiredChecks"]}}
        proof["runIds" if len(run_ids) > 1 else "runId"] = run_ids if len(run_ids) > 1 else run_ids[0]
        reference["acceptanceFile"], reference["acceptanceEvidenceSha256"] = self.json_file(run_ids[0] + ".json", proof)

    def pdf_capture(self, reference, run_ids, offset, duration):
        pdf = self.base / (run_ids[0] + ".pdf")
        pdf.write_bytes(b"%PDF-1.7\nfixture validated externally\n")
        proof = {"runId": run_ids[0], "passed": True, "captureMode": "run-scoped-first-pdf",
                 "startedAt": self.stamp(offset + 1), "firstPdfAt": self.stamp(offset + duration - 1),
                 "pdfPath": pdf.name, "pdfSha256": COLLECTOR.digest_file(pdf)}
        if len(run_ids) > 1:
            proof["runIds"] = run_ids
        reference["firstPdfFile"], reference["firstPdfEvidenceSha256"] = self.json_file(run_ids[0] + "-pdf.json", proof)

    def complete_manifest(self, workflows=False):
        manifest = COLLECTOR.example_manifest()
        for index, pair in enumerate(manifest["pairs"]):
            pair.update(fixtureSha256=FIXTURE_HASH, acceptanceSha256=ACCEPTANCE_HASH,
                        modelId=MODEL, reasoningEffort="high")
            for side, profile in (("before", "legacy"), ("after", "compact-v1")):
                offset = index * 100 + (0 if side == "before" else 40)
                duration = 10 if side == "before" else 6
                run_ids = [pair["id"] + "-" + side]
                self.add_run(run_ids[0], pair["mode"], profile, 60 if pair["cache"] == "warm" else 0, offset, duration)
                if workflows:
                    run_ids.append(run_ids[0] + "-followup")
                    self.add_run(run_ids[1], pair["mode"], profile, 60, offset + 15, duration)
                    pair[side] = {"runIds": run_ids}
                else:
                    pair[side] = {"runId": run_ids[0]}
                self.acceptance(pair[side], pair, run_ids)
                if pair["mode"] == "science":
                    self.pdf_capture(pair[side], run_ids, offset, duration)
        return manifest

    def compare(self, manifest):
        reader = COLLECTOR.open_database(self.database)
        try:
            return COLLECTOR.compare_manifest(reader, manifest, self.base)
        finally:
            reader.close()

    def test_database_is_read_only_and_missing_numbers_stay_null(self):
        self.add_run("root")
        reader = COLLECTOR.open_database(self.database)
        try:
            with self.assertRaises(sqlite3.OperationalError):
                reader.execute("DELETE FROM runs")
        finally:
            reader.close()
        self.assertIsNone(COLLECTOR.number(True))
        self.assertIsNone(COLLECTOR.number(float("nan")))
        self.assertIsNone(COLLECTOR.measured_sum([]))
        self.assertIsNone(COLLECTOR.measured_sum([1, None]))
        self.assertEqual(0, COLLECTOR.measured_sum([0]))

    def test_new_native_events_and_owned_child_count_once_not_forwarded(self):
        self.add_run("root", mode="coding")
        self.event("root", "coding.metrics", {"round": 1, "phase": "main", "metrics": self.metrics()})
        self.add_run("child", mode="coding", parent="root", offset=2, duration=5)
        self.event("root", "subagent.event", {"event": {"type": "model.turn.metrics", "data": {"metrics": self.metrics()}}})
        result = self.collect("root")
        self.assertEqual(1, len(result["turns"]))
        self.assertEqual(["child"], [child["runId"] for child in result["children"]])
        self.assertEqual(4400, result["summedNativeWorkMilliseconds"])
        self.assertEqual(10000, result["gatewayRequestToCompletionMilliseconds"])
        self.assertTrue(result["allNativeMetricsComplete"])

    def test_first_visible_root_answer_ignores_metadata_reasoning_and_child(self):
        self.add_run("root")
        self.event("root", "text.delta", {"delta": "", "phase": "main"}, 1)
        self.event("root", "reasoning.delta", {"delta": "hidden"}, 2)
        self.event("root", "subagent.event", {"event": {"type": "text.delta", "data": {"delta": "child"}}}, 2)
        self.event("root", "text.delta", {"delta": "child", "agentId": "agent-test"}, 2)
        self.event("root", "text.delta", {"delta": "visible answer", "phase": "main"}, 3)
        result = self.collect("root")
        self.assertEqual(self.stamp(3), result["firstVisibleAnswerAt"])
        self.assertEqual(3000, result["firstVisibleAnswerMillisecondsFromRequest"])
        self.assertEqual(110, result["turns"][0]["metrics"]["timeToFirstTokenMilliseconds"])
        self.assertNotIn("visible answer", json.dumps(result))

    def test_summarization_and_repeated_real_rounds_are_never_dropped(self):
        self.add_run("root", mode="coding")
        self.event("root", "coding.metrics", {"round": 1, "phase": "summarization", "metrics": self.metrics(), "reasoningEffort": "high"})
        self.event("root", "model.turn.metrics", {"round": 1, "metrics": self.metrics(), "reasoningEffort": "high"})
        result = self.collect("root")
        self.assertEqual(3, len(result["turns"]))
        self.assertEqual(300, result["totals"]["inputTokens"])
        self.assertEqual(200, result["nativeMinusTerminalTokens"]["inputTokens"])
        self.assertTrue(result["allNativeMetricsComplete"])

    def test_missing_field_and_unrecorded_terminal_work_block_comparison(self):
        self.add_run("root")
        payload = {"round": 1, "metrics": self.metrics()}
        payload["metrics"].pop("promptMilliseconds")
        self.writer.execute("UPDATE run_events SET data_json=? WHERE event_type='model.turn.metrics'", (json.dumps(payload),))
        self.writer.execute("UPDATE run_events SET data_json=? WHERE event_type='run.completed'", (json.dumps({"inputTokens": 200, "outputTokens": 10}),))
        self.writer.commit()
        result = self.collect("root")
        self.assertIsNone(result["totals"]["promptMilliseconds"])
        self.assertFalse(result["allNativeMetricsComplete"])
        self.assertIn("terminal_token_coverage_unproven:inputTokens", result["issues"])

    def test_legacy_general_progress_is_observation_not_complete_timing(self):
        self.add_run("root")
        self.writer.execute("DELETE FROM run_events WHERE event_type='model.turn.metrics'")
        self.event("root", "model.generation", {"state": "generationStarting", "promptTokens": 10819, "cachedPromptTokens": 10815})
        result = self.collect("root")
        self.assertEqual(10819, result["firstPromptObservation"]["promptTokens"])
        self.assertIsNone(result["totals"]["totalMilliseconds"])
        self.assertFalse(result["allNativeMetricsComplete"])

    def test_legacy_coding_fallback_cancelled_child_and_retries_are_explicit(self):
        self.add_run("root", mode="coding", source="coding.metrics")
        self.add_run("child", mode="coding", parent="root", state="Cancelled")
        self.event("root", "model.generation", {"state": "generationRetry"})
        for _ in range(2):
            self.event("root", "server_tool.started", {"tool": "web.search", "arguments": {"query": "fixed"}})
        result = self.collect("root")
        self.assertEqual("coding.metrics", result["turns"][0]["source"])
        self.assertEqual(1, result["repeatedSourceActions"])
        self.assertEqual(1, result["generationRetries"])
        self.assertIn("run_not_completed", result["children"][0]["issues"])
        self.assertFalse(result["allNativeMetricsComplete"])

    def test_eighteen_valid_pairs_cover_every_mode_and_cache(self):
        result = self.compare(self.complete_manifest())
        self.assertEqual("complete", result["status"])
        self.assertTrue(result["canClaimSpeedComparison"])
        self.assertTrue(result["allModeCacheGroupsFaster"])
        self.assertEqual([3] * 6, [group["successfulPairs"] for group in result["groups"]])

    def test_ordered_followups_are_included_and_user_idle_excluded(self):
        result = self.compare(self.complete_manifest(workflows=True))
        self.assertTrue(result["canClaimSpeedComparison"])
        before = result["pairs"][0]["before"]
        self.assertEqual(2, len(before["runs"]))
        self.assertEqual(20000, before["gatewayActiveMilliseconds"])
        self.assertEqual(25000, before["gatewayEnvelopeMilliseconds"])
        self.assertEqual(200, before["totals"]["inputTokens"])

    def test_noncompleted_or_mismatched_identity_and_cache_reject_pairs(self):
        manifest = self.complete_manifest()
        self.writer.execute("UPDATE runs SET state='Cancelled' WHERE run_id=?", (manifest["pairs"][0]["before"]["runId"],))
        self.writer.execute("UPDATE runs SET selected_model='other' WHERE run_id=?", (manifest["pairs"][1]["after"]["runId"],))
        self.writer.execute("UPDATE run_events SET data_json=? WHERE run_id=? AND event_type='model.turn.metrics'",
                            (json.dumps({"round": 1, "contextProfile": "compact-v1", "modelId": MODEL,
                                         "reasoningEffort": "high", "metrics": self.metrics(0)}), manifest["pairs"][3]["after"]["runId"]))
        self.writer.commit()
        result = self.compare(manifest)
        self.assertEqual("incomplete", result["status"])
        self.assertFalse(result["canClaimSpeedComparison"])
        self.assertIsNone(result["allModeCacheGroupsFaster"])
        self.assertTrue(result["pairs"][0]["errors"])
        self.assertTrue(result["pairs"][1]["errors"])
        self.assertTrue(result["pairs"][3]["errors"])

    def test_reused_run_or_changed_fixture_cannot_inflate_repetitions(self):
        manifest = self.complete_manifest()
        manifest["pairs"][1]["before"]["runId"] = manifest["pairs"][0]["before"]["runId"]
        manifest["pairs"][2]["fixtureSha256"] = "c" * 64
        result = self.compare(manifest)
        self.assertTrue(result["pairs"][1]["errors"])
        self.assertTrue(result["pairs"][2]["errors"])
        self.assertFalse(result["canClaimSpeedComparison"])

    def test_acceptance_hash_boolean_and_required_checks_are_authoritative(self):
        manifest = self.complete_manifest()
        pair = manifest["pairs"][0]
        reference = pair["before"]
        path = self.base / reference["acceptanceFile"]
        proof = json.loads(path.read_text())
        proof["passed"] = 1
        path.write_text(json.dumps(proof))
        reference["acceptanceEvidenceSha256"] = COLLECTOR.digest_file(path)
        self.assertTrue(self.compare(manifest)["pairs"][0]["errors"])
        proof["passed"] = True
        proof["checks"]["followupComplete"] = False
        path.write_text(json.dumps(proof))
        reference["acceptanceEvidenceSha256"] = COLLECTOR.digest_file(path)
        self.assertTrue(self.compare(manifest)["pairs"][0]["errors"])
        reference["acceptanceEvidenceSha256"] = "0" * 64
        self.assertTrue(self.compare(manifest)["pairs"][0]["errors"])

    def test_science_requires_fresh_run_scoped_first_pdf_not_project_lifetime(self):
        manifest = self.complete_manifest()
        pair = next(pair for pair in manifest["pairs"] if pair["mode"] == "science")
        reference = pair["after"]
        filename = reference.pop("firstPdfFile")
        self.assertTrue(self.compare(manifest)["pairs"][12]["errors"])
        reference["firstPdfFile"] = filename
        path = self.base / filename
        proof = json.loads(path.read_text())
        proof["startedAt"] = self.stamp(-86400)
        path.write_text(json.dumps(proof))
        reference["firstPdfEvidenceSha256"] = COLLECTOR.digest_file(path)
        self.assertTrue(self.compare(manifest)["pairs"][12]["errors"])

    def test_invalid_warm_fraction_and_incomplete_coverage_never_claim_speed(self):
        manifest = self.complete_manifest()
        incomplete = deepcopy(manifest)
        incomplete["pairs"].pop()
        self.assertFalse(self.compare(incomplete)["canClaimSpeedComparison"])
        for invalid in (0, 0.1, 2, True, float("nan")):
            manifest["minimumWarmReuseFraction"] = invalid
            with self.assertRaises(ValueError):
                self.compare(manifest)


if __name__ == "__main__":
    unittest.main()
