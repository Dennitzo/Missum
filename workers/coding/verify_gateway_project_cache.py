"""Opt-in gateway-to-native project KV smoke using an already loaded model.

Creates one uniquely named artifact workspace and two UUID sessions. Calls only
the public run API for inference; native endpoints are read-only idle checks.
Never loads/unloads a model, resets/deletes a cache, or deletes persisted runs.
"""
import argparse
import hashlib
import json
from pathlib import Path
import time
import urllib.error
import urllib.request
import uuid

from verify_project_context_cache import ProjectCacheProbe, check_cache_headroom


TERMINAL_EVENTS = {"run.completed", "run.failed", "run.cancelled"}


def message(text, role="user"):
    return dict(role=role, content=[dict(type="text", text=text)])


def request_body(model, session, workspace, context, prompt, continuation=False):
    # Coding mode requires the coding capability. The allow-list is empty and
    # the explicit prompt forbids invoking its capability-derived client tools.
    return dict(protocolVersion="1.0", mode="coding", sessionId=session,
        messages=[message(prompt)], clientCapabilities=["coding"],
        allowedServerTools=[], preferredCodingModelId=model, reasoningEffort="none",
        limits=dict(maximumOutputTokens=64), workspacePath=str(workspace),
        codingOptions=dict(useWorkingState=True, reasoningPolicy="maximum",
            workspacePath=str(workspace), continueSessionContext=continuation),
        contextProfileVersion="compact-v1", projectContext=context)


def event_payload(lines):
    data = "\n".join(line[5:].lstrip() for line in lines if line.startswith("data:"))
    return json.loads(data) if data else None


class GatewayProjectProbe:
    def __init__(self, gateway, model, timeout, report, native, cache_directory):
        self.gateway = gateway.rstrip("/")
        self.model, self.timeout, self.report = model, timeout, report
        self.native, self.cache_directory = native, cache_directory
        self.client_id = "gateway-project-probe-" + uuid.uuid4().hex
        self.active_run = None

    def request(self, path, body=None, idempotency=None):
        headers = {"Content-Type": "application/json", "X-Missum-Client-ID": self.client_id}
        if idempotency:
            headers["Idempotency-Key"] = idempotency
        request = urllib.request.Request(self.gateway + path,
            data=json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None,
            headers=headers)
        started = time.monotonic()
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                result = None if response.status == 204 else json.load(response)
        except urllib.error.HTTPError as error:
            raise RuntimeError(path + ": HTTP " + str(error.code) + " "
                + error.read(4096).decode("utf-8", "replace")) from error
        self.report["requests"].append(dict(path=path, elapsedSeconds=time.monotonic() - started))
        return result

    def events(self, run_id):
        request = urllib.request.Request(self.gateway + "/v1/runs/" + run_id + "/events",
            headers={"Accept": "text/event-stream", "X-Missum-Client-ID": self.client_id})
        deadline = time.monotonic() + self.timeout
        previous, lines = 0, []
        with urllib.request.urlopen(request, timeout=min(self.timeout, 30)) as response:
            for raw in response:
                if time.monotonic() > deadline:
                    raise TimeoutError("Disposable gateway run exceeded its smoke deadline")
                line = raw.decode("utf-8").rstrip("\r\n")
                if line:
                    lines.append(line)
                    continue
                event = event_payload(lines)
                lines = []
                if event is None:
                    continue
                if event.get("runId") != run_id or not isinstance(event.get("id"), int) or event["id"] <= previous:
                    raise AssertionError("Gateway emitted an invalid run/event identity")
                previous = event["id"]
                yield event
                if event.get("type") in TERMINAL_EVENTS:
                    return
        raise AssertionError("Gateway event stream ended without a terminal event")

    def worker_log_offset(self):
        path = self.cache_directory / "events.jsonl"
        return path.stat().st_size if path.exists() else 0

    def worker_events(self, offset):
        path = self.cache_directory / "events.jsonl"
        if not path.exists():
            return []
        result = []
        with path.open("rb") as stream:
            stream.seek(offset)
            for line in stream:
                try:
                    result.append(json.loads(line))
                except json.JSONDecodeError:
                    pass  # A concurrently appended final diagnostic line is optional.
        return result

    def turn(self, scenario, body, expected_word, minimum_cached=None):
        self.native.assert_idle()
        started, offset = time.monotonic(), self.worker_log_offset()
        row = dict(scenario=scenario, request=body, events=[], passed=False)
        self.report["scenarios"].append(row)
        accepted = self.request("/v1/runs", body, "gateway-cache-probe-" + uuid.uuid4().hex)
        row["accepted"] = accepted
        run_id = accepted.get("runId")
        if not isinstance(run_id, str) or not run_id.startswith("run-"):
            raise AssertionError("Gateway did not accept a run identity")
        self.active_run = run_id
        answer = ""
        for event in self.events(run_id):
            row["events"].append(event)
            kind, data = event.get("type"), event.get("data", {})
            if kind == "text.delta":
                replace = data.get("replaceFrom")
                answer = (answer[:replace] if isinstance(replace, int) else answer) + data.get("delta", "")
            if kind in ("client_tool.proposed", "server_tool.started", "run.waiting_for_client"):
                raise AssertionError("Smoke prompt unexpectedly requested a tool/client operation")
            if kind in ("run.failed", "run.cancelled"):
                raise AssertionError("Gateway run did not complete: " + json.dumps(data, ensure_ascii=False))
        self.active_run = None
        row.update(answer=answer, elapsedSeconds=time.monotonic() - started,
            snapshot=self.request("/v1/runs/" + run_id), nativeCacheEvents=self.worker_events(offset))
        self.native.assert_idle()
        if row["snapshot"].get("state") != "completed" or row["snapshot"].get("selectedModel") != self.model:
            raise AssertionError("Gateway did not finish using the explicitly selected model")
        if answer.strip().strip("\"'. \n") != expected_word:
            raise AssertionError(scenario + ": branch did not preserve its own private marker")
        metrics = [event["data"] for event in row["events"] if event.get("type") == "model.turn.metrics"]
        row["turnMetrics"] = metrics
        if len(metrics) != 1 or metrics[0].get("toolCalls") != 0:
            raise AssertionError("Expected exactly one bounded model turn without tool calls")
        measured = metrics[0].get("metrics", {})
        cached = measured.get("cachedPromptTokens")
        prepares = [event for event in row["nativeCacheEvents"]
            if event.get("operation") == "project-prepare" and event.get("status") == "forked"]
        if minimum_cached is None:
            if len(prepares) != 1 or not isinstance(prepares[0].get("projectPrefixTokens"), int):
                raise AssertionError("Gateway did not traverse a successful native project-prepare")
            minimum_cached = max(1, prepares[0]["projectPrefixTokens"] - 1)
            row["projectPrefixTokens"] = prepares[0]["projectPrefixTokens"]
        row["requiredCachedTokens"] = minimum_cached
        if not isinstance(cached, int) or cached < minimum_cached:
            raise AssertionError(scenario + ": actual gateway cachedPromptTokens=" + str(cached)
                + ", required >= " + str(minimum_cached))
        if measured.get("outputTokens", 65) > 64:
            raise AssertionError("Gateway did not respect the explicit 64-output-token bound")
        row["contextEvents"] = [event["data"] for event in row["events"] if event.get("type") == "context.changed"]
        row["passed"] = True
        print(json.dumps(dict(scenario=scenario, passed=True, runId=run_id,
            cachedPromptTokens=cached, requiredCachedTokens=minimum_cached,
            elapsedSeconds=row["elapsedSeconds"])), flush=True)
        return row

    def cancel_own_failed_run(self):
        if self.active_run is not None:
            run_id, self.active_run = self.active_run, None
            return self.request("/v1/runs/" + run_id + "/cancel", {})
        return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--gateway", default="http://127.0.0.1:8080")
    parser.add_argument("--native", default="http://127.0.0.1:8081")
    parser.add_argument("--control", default="http://127.0.0.1:8082")
    parser.add_argument("--model", required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=180)
    parser.add_argument("--cache-directory", type=Path, default=Path.home() / ".missum" / "native-runtime" / "session-cache")
    args = parser.parse_args()
    if not 1 <= args.timeout <= 1800:
        parser.error("Timeout must be between 1 and 1800 seconds per bounded run")
    report = dict(passed=False, layer="real-gateway-to-native", model=args.model, scenarios=[], requests=[],
        note="Unique artifact workspace and UUID sessions; no model lifecycle/reset/deletion operations.")
    native = ProjectCacheProbe(args.native, args.control, args.model, args.timeout, report)
    probe = GatewayProjectProbe(args.gateway, args.model, args.timeout, report, native, args.cache_directory)
    try:
        native.assert_idle()
        report["cacheHeadroom"] = check_cache_headroom(args.cache_directory, 4)
        report["gatewayHealth"] = probe.request("/v1/health/live")
        if report["gatewayHealth"].get("protocolVersion") != "1.0":
            raise AssertionError("Gateway protocol does not match RunRequest v1.0")
        nonce = uuid.uuid4().hex
        workspace = args.report.resolve().parent / ("gateway-project-probe-" + nonce)
        workspace.mkdir(parents=True, exist_ok=False)
        text = "\n".join("Gemeinsame Projektnotiz " + str(index) + ": Feststehende Beobachtung " + str(index)
            + " des Artefaktprojekts " + nonce + "." for index in range(48))
        context = dict(projectKey="gateway-project-" + nonce, generation=1,
            contentHash=hashlib.sha256(text.encode("utf-8")).hexdigest(), text=text,
            sourceWatermark="gateway-project-smoke-v1")
        sessions = {letter: str(uuid.uuid4()) for letter in ("a", "b")}
        report.update(workspace=str(workspace), projectContext=context, sessions=sessions)
        (workspace / "probe-context.txt").write_text(text, encoding="utf-8")
        prefix_tokens = None
        for letter, word in (("a", "ALPHA"), ("b", "BETA")):
            prompt = "Dies ist eine reine Dialogprüfung. Keine Dateien lesen oder ändern, keine Werkzeuge aufrufen. " \
                + "Das private Kennwort ausschließlich dieser Sitzung lautet " + word + ". " \
                + "Merke es dir nur für diese Sitzung. Antworte jetzt ausschließlich mit dem einen Wort " + word + "."
            row = probe.turn("new-session-" + letter + "/project-prefix", request_body(args.model,
                sessions[letter], workspace, context, prompt), word)
            if prefix_tokens is not None and row["projectPrefixTokens"] != prefix_tokens:
                raise AssertionError("Gateway used different immutable prefixes for identical project context")
            prefix_tokens = row["projectPrefixTokens"]
            report["sharedPrefixTokens"] = prefix_tokens
        for letter, word in (("a", "ALPHA"), ("b", "BETA")):
            prompt = "Welche private Kennung wurde ausschließlich in dieser Sitzung vereinbart? " \
                + "Antworte nur mit dem vereinbarten Wort. Keine Werkzeuge aufrufen."
            probe.turn("return-to-session-" + letter + "/independent-dialogue", request_body(args.model,
                sessions[letter], workspace, context, prompt, continuation=True), word, max(1, prefix_tokens - 1))
        ledger = json.loads((args.cache_directory / "ownership.json").read_text(encoding="utf-8"))
        report["projectCacheOwnership"] = {identity: item for identity, item in ledger.get("entries", {}).items()
            if item.get("projectKey") == context["projectKey"]}
        if {item.get("kind") for item in report["projectCacheOwnership"].values()} != {"projectBase", "sessionBranch"}:
            raise AssertionError("Gateway project base and independent branch ownership were not persisted")
        before = report["cacheHeadroom"]["snapshotsBefore"]
        missing = [name for name in before if not (args.cache_directory / name).is_file()]
        report["preservedExistingSnapshotFiles"] = not missing
        if missing:
            raise AssertionError("Existing snapshot files disappeared during gateway smoke")
        report["passed"] = all(row["passed"] for row in report["scenarios"])
    except Exception as error:
        report["error"] = str(error)
        try:
            report["cancelledOwnFailedRun"] = probe.cancel_own_failed_run()
        except Exception as cancellation_error:
            report["ownRunCancellationError"] = str(cancellation_error)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(dict(passed=report["passed"], scenarios=len(report["scenarios"]),
        report=str(args.report), error=report.get("error"))))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
