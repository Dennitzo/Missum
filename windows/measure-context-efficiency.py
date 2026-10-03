"""Read-only gateway journal collector. Never loads models or submits runs."""
import argparse
from contextlib import closing
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import re
import sqlite3
import statistics


METRICS = (
    "runtimeQueueMilliseconds", "tokenCountingMilliseconds", "promptMilliseconds",
    "generationMilliseconds", "timeToFirstTokenMilliseconds", "totalMilliseconds",
    "promptEvaluatedTokens", "cachedPromptTokens", "reasoningTokens", "inputTokens", "outputTokens",
)
REQUIRED_METRICS = tuple(key for key in METRICS if key != "reasoningTokens")
EVENT_TYPES = (
    "run.started", "run.completed", "model.turn.metrics", "coding.metrics", "context.profile",
    "model.generation", "server_tool.started", "client_tool.proposed", "model.fallback", "provider.fallback", "text.delta",
)


def number(value):
    return value if type(value) in (int, float) and math.isfinite(value) and value >= 0 else None


def timestamp(value):
    if not isinstance(value, str):
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        return parsed if parsed.tzinfo is not None else None
    except ValueError:
        return None


def elapsed(start, end):
    start, end = timestamp(start), timestamp(end)
    return (end - start).total_seconds() * 1000 if start and end and end >= start else None


def measured_sum(values):
    values = list(values)
    return sum(values) if values and all(number(value) is not None for value in values) else None


def digest_file(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read_json(path):
    value = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected JSON object: {path}")
    return value


def open_database(path):
    connection = sqlite3.connect(Path(path).resolve().as_uri() + "?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA query_only=ON")
    connection.execute("BEGIN")  # One consistent WAL snapshot for parent, children and events.
    return connection


def metric_turns(events):
    # Actual child rows are collected separately; forwarded subagent.event rows
    # are never queried. Both events can describe the same completed native call.
    preferred_rounds = {event["data"].get("round") for event in events if event["type"] == "model.turn.metrics"}
    turns = []
    for event in events:
        if event["type"] not in ("model.turn.metrics", "coding.metrics"):
            continue
        data = event["data"]
        if (event["type"] == "coding.metrics" and data.get("phase") != "summarization"
                and data.get("round") in preferred_rounds):
            continue
        payload = data.get("metrics") if isinstance(data.get("metrics"), dict) else {}
        turns.append({
            "eventId": event["id"], "createdAt": event["createdAt"], "source": event["type"],
            "round": data.get("round"), "phase": data.get("phase"), "modelId": data.get("modelId"),
            "contextProfile": data.get("contextProfile"), "reasoningEffort": data.get("reasoningEffort"),
            "toolCalls": number(data.get("toolCalls")), "queueMilliseconds": number(data.get("queueMilliseconds")),
            "toolCatalogSignature": data.get("toolCatalogSignature"),
            "metrics": {key: number(payload.get(key)) for key in METRICS},
        })
    # Do not coalesce two model.turn.metrics rows merely because their round is
    # equal: a crash/retry can really perform the same logical round twice.
    return turns


def collect_run(connection, run_id, children=True):
    row = connection.execute("SELECT * FROM runs WHERE run_id=?", (run_id,)).fetchone()
    if row is None:
        raise ValueError(f"Run not found: {run_id}")
    request = json.loads(row["request_json"])
    marks = ",".join("?" for _ in EVENT_TYPES)
    events = [{"id": event["id"], "type": event["event_type"], "data": json.loads(event["data_json"]),
               "createdAt": event["created_at"]} for event in connection.execute(
        f"SELECT id,event_type,data_json,created_at FROM run_events WHERE run_id=? AND event_type IN ({marks}) ORDER BY id",
        (run_id, *EVENT_TYPES))]
    turns = metric_turns(events)
    completed = [event for event in events if event["type"] == "run.completed"]
    started = next((event["createdAt"] for event in events if event["type"] == "run.started"), None)
    ended = completed[-1]["createdAt"] if completed else None
    visible = next((event["createdAt"] for event in events if event["type"] == "text.delta"
                    and not event["data"].get("agentId") and isinstance(event["data"].get("delta"), str)
                    and event["data"]["delta"].strip()), None)
    issues = []
    if row["state"] != "Completed" or not completed:
        issues.append("run_not_completed")
    if not turns:
        issues.append("native_turn_metrics_missing")
    for turn in turns:
        missing = [key for key in REQUIRED_METRICS if turn["metrics"][key] is None]
        if missing:
            issues.append(f"event_{turn['eventId']}_missing:" + ",".join(missing))
    totals = {key: measured_sum(turn["metrics"][key] for turn in turns) for key in METRICS}
    terminal_difference = {}
    if completed:
        for key in ("inputTokens", "outputTokens"):
            terminal = number(completed[-1]["data"].get(key))
            if terminal is None or totals[key] is None or terminal > totals[key]:
                issues.append("terminal_token_coverage_unproven:" + key)
            terminal_difference[key] = totals[key] - terminal if terminal is not None and totals[key] is not None else None
    retry_events = [event for event in events if event["type"] == "model.generation"
                    and event["data"].get("state") == "generationRetry"]
    if retry_events:
        issues.append("interrupted_native_attempt_metrics_unproven")
    source_calls = [event for event in events if event["type"] == "server_tool.started"
                    and event["data"].get("tool") in ("web.search", "web.fetch")]
    source_keys = [json.dumps((event["data"].get("tool"), event["data"].get("arguments")), sort_keys=True) for event in source_calls]
    proposals = {event["data"].get("proposalId") for event in events if event["type"] == "client_tool.proposed"}
    caps = request.get("clientCapabilities") or []
    mode = "science" if "research.deliverables" in caps else "coding" if row["mode"] == "Coding" else "general"
    profile = next((event["data"].get("version") for event in events if event["type"] == "context.profile"), None)
    profile = profile or next((turn["contextProfile"] for turn in turns if turn["contextProfile"]), None) or request.get("contextProfileVersion")
    summary = {
        "runId": run_id, "mode": mode, "status": row["state"], "modelId": row["selected_model"],
        "requestCreatedAt": row["created_at"], "startedAt": started, "completedAt": ended,
        "contextProfile": profile, "requestSha256": hashlib.sha256(row["request_json"].encode()).hexdigest(),
        "gatewayRequestToCompletionMilliseconds": elapsed(row["created_at"], ended),
        "firstVisibleAnswerAt": visible,
        "firstVisibleAnswerMillisecondsFromRequest": elapsed(row["created_at"], visible),
        "processingMilliseconds": elapsed(started, ended), "turns": turns, "totals": totals,
        "nativeMinusTerminalTokens": terminal_difference,
        "firstPromptObservation": next((event["data"] for event in events if event["type"] == "model.generation"
                                        and number(event["data"].get("promptTokens")) is not None), None),
        "generationRetries": len(retry_events),
        "modelFallbacks": sum(event["type"] == "model.fallback" for event in events),
        "providerFallbacks": sum(event["type"] == "provider.fallback" for event in events),
        "sourceActions": len(source_calls), "repeatedSourceActions": len(source_keys) - len(set(source_keys)),
        "clientProposals": len(proposals - {None}), "issues": issues, "children": [],
    }
    if children:
        child_ids = [child[0] for child in connection.execute(
            "SELECT run_id FROM runs WHERE json_extract(request_json,'$.subagent.parentRunId')=? ORDER BY created_at,run_id", (run_id,))]
        summary["children"] = [collect_run(connection, child_id, children=False) for child_id in child_ids]
    nodes = [summary, *summary["children"]]
    summary["allNativeMetricsComplete"] = all(not node["issues"] for node in nodes)
    summary["summedNativeWorkMilliseconds"] = measured_sum(node["totals"]["totalMilliseconds"] for node in nodes)
    return summary


def bound_evidence(reference, pair, run, base):
    path = (base / reference["acceptanceFile"]).resolve()
    if digest_file(path) != reference.get("acceptanceEvidenceSha256"):
        raise ValueError("acceptance evidence hash mismatch")
    proof = read_json(path)
    run_ids = run.get("runIds", [run["runId"]])
    expected = {"fixtureSha256": pair["fixtureSha256"], "acceptanceSha256": pair["acceptanceSha256"]}
    expected["runIds" if len(run_ids) > 1 else "runId"] = run_ids if len(run_ids) > 1 else run_ids[0]
    if proof.get("passed") is not True or any(proof.get(key) != value for key, value in expected.items()):
        raise ValueError("acceptance evidence does not bind this successful fixture/run")
    for check in pair.get("requiredChecks", []):
        if not isinstance(proof.get("checks"), dict) or proof["checks"].get(check) is not True:
            raise ValueError("acceptance check not passed: " + str(check))
    return {"path": str(path), "sha256": digest_file(path), "passed": True}


def first_pdf_capture(reference, run, base):
    if "firstPdfFile" not in reference:
        return None
    path = (base / reference["firstPdfFile"]).resolve()
    if digest_file(path) != reference.get("firstPdfEvidenceSha256"):
        raise ValueError("first PDF evidence hash mismatch")
    proof = read_json(path)
    start, end, pdf_at = timestamp(run["startedAt"]), timestamp(run["completedAt"]), timestamp(proof.get("firstPdfAt"))
    run_ids = run.get("runIds", [run["runId"]])
    if (proof.get("runId") not in run_ids or (len(run_ids) > 1 and proof.get("runIds") != run_ids)
            or proof.get("passed") is not True
            or proof.get("captureMode") != "run-scoped-first-pdf" or timestamp(proof.get("startedAt")) != start
            or not start or not end or not pdf_at or not start <= pdf_at <= end):
        raise ValueError("first PDF capture is not scoped to this completed run")
    pdf = (path.parent / proof["pdfPath"]).resolve()
    with pdf.open("rb") as stream:
        valid_header = stream.read(5) == b"%PDF-"
    if not valid_header or digest_file(pdf) != proof.get("pdfSha256"):
        raise ValueError("first PDF artifact/hash invalid")
    return {"evidencePath": str(path), "pdfSha256": proof["pdfSha256"],
            "millisecondsFromRunStart": elapsed(run["startedAt"], proof["firstPdfAt"])}


def collect_workflow(connection, run_ids):
    runs = [collect_run(connection, run_id) for run_id in run_ids]
    for previous, current in zip(runs, runs[1:]):
        previous_end, current_start = timestamp(previous["completedAt"]), timestamp(current["requestCreatedAt"])
        if not previous_end or not current_start or current_start < previous_end:
            raise ValueError("workflow runIds must be ordered, completed and sequential")
    visible = next((run["firstVisibleAnswerAt"] for run in runs if run["firstVisibleAnswerAt"]), None)
    return {
        "runId": run_ids[0], "runIds": run_ids, "runs": runs,
        "startedAt": runs[0]["startedAt"], "completedAt": runs[-1]["completedAt"],
        "firstVisibleAnswerAt": visible,
        "firstVisibleAnswerMillisecondsFromRequest": elapsed(runs[0]["requestCreatedAt"], visible),
        "gatewayActiveMilliseconds": measured_sum(run["gatewayRequestToCompletionMilliseconds"] for run in runs),
        "gatewayEnvelopeMilliseconds": elapsed(runs[0]["requestCreatedAt"], runs[-1]["completedAt"]),
        "summedNativeWorkMilliseconds": measured_sum(run["summedNativeWorkMilliseconds"] for run in runs),
        "totals": {key: measured_sum(node["totals"][key] for run in runs for node in [run, *run["children"]]) for key in METRICS},
        "allNativeMetricsComplete": all(run["allNativeMetricsComplete"] for run in runs),
    }


def compare_manifest(connection, manifest, base):
    results, errors, used_runs, coverage, scenarios = [], [], set(), {}, {}
    if manifest.get("schema") != "missum.context-efficiency.v1":
        raise ValueError("Unsupported manifest schema")
    if not isinstance(manifest.get("pairs"), list) or any(not isinstance(pair, dict) for pair in manifest["pairs"]):
        raise ValueError("pairs must be an array of objects")
    warm_fraction = number(manifest.get("minimumWarmReuseFraction", 0.5))
    if warm_fraction is None or not 0.5 <= warm_fraction <= 1:
        raise ValueError("minimumWarmReuseFraction must be between 0.5 and 1")
    for pair in manifest.get("pairs", []):
        result = {"id": pair.get("id"), "mode": pair.get("mode"), "cache": pair.get("cache"), "errors": []}
        try:
            key = (pair["mode"], pair["cache"])
            if key[0] not in ("general", "coding", "science") or key[1] not in ("cold", "warm"):
                raise ValueError("unknown mode/cache condition")
            for field in ("fixtureSha256", "acceptanceSha256"):
                if not re.fullmatch(r"[a-f0-9]{64}", pair.get(field) or ""):
                    raise ValueError("missing measured fixture/acceptance hash")
            if not isinstance(pair.get("fixtureId"), str) or not pair["fixtureId"].strip():
                raise ValueError("missing fixtureId")
            if not isinstance(pair.get("requiredChecks", []), list) or any(not isinstance(check, str) for check in pair.get("requiredChecks", [])):
                raise ValueError("requiredChecks must be an array of names")
            scenario = tuple(pair.get(field) for field in ("fixtureId", "fixtureSha256", "acceptanceSha256", "modelId", "reasoningEffort"))
            if pair["mode"] in scenarios and scenarios[pair["mode"]] != scenario:
                raise ValueError("scenario/model/effort differs between repetitions of this mode")
            scenarios[pair["mode"]] = scenario
            runs = []
            for side, profile in (("before", "legacy"), ("after", "compact-v1")):
                reference = pair[side]
                run_ids = reference.get("runIds", [reference.get("runId")])
                if (not isinstance(run_ids, list) or not run_ids
                        or any(not isinstance(run_id, str) or not run_id or run_id in used_runs for run_id in run_ids)
                        or len(run_ids) != len(set(run_ids))):
                    raise ValueError("missing or reused workflow runId")
                used_runs.update(run_ids)
                if reference.get("database"):
                    with closing(open_database((base / reference["database"]).resolve())) as side_connection:
                        run = collect_workflow(side_connection, run_ids)
                else:
                    run = collect_workflow(connection, run_ids)
                result[side] = run
                if any(leg["mode"] != pair["mode"] or leg["contextProfile"] != profile for leg in run["runs"]):
                    raise ValueError("mode/context profile mismatch")
                if not run["allNativeMetricsComplete"] or run["gatewayActiveMilliseconds"] is None:
                    raise ValueError("incomplete successful native measurements")
                for node in [node for leg in run["runs"] for node in [leg, *leg["children"]]]:
                    if node["modelId"] != pair.get("modelId") or not pair.get("reasoningEffort"):
                        raise ValueError("model/reasoning identity mismatch")
                    if any(turn["reasoningEffort"] != pair["reasoningEffort"]
                           or (turn["modelId"] and turn["modelId"] != pair["modelId"]) for turn in node["turns"]):
                        raise ValueError("model/reasoning changed during run")
                first = run["runs"][0]["turns"][0]["metrics"]
                cached, prompt = first["cachedPromptTokens"], first["inputTokens"]
                if key[1] == "cold" and cached != 0:
                    raise ValueError("cold run actually reused prompt cache")
                if key[1] == "warm" and (not prompt or cached / prompt < warm_fraction):
                    raise ValueError("warm cache reuse unproven")
                result[side + "Acceptance"] = bound_evidence(reference, pair, run, base)
                result[side + "FirstPdf"] = first_pdf_capture(reference, run, base)
                if key[0] == "science" and result[side + "FirstPdf"] is None:
                    raise ValueError("science workflow requires run-scoped first PDF evidence")
                runs.append(run)
            before, after = (run["gatewayActiveMilliseconds"] for run in runs)
            if before <= 0 or after <= 0:
                raise ValueError("nonpositive measured elapsed time")
            result["afterOverBeforeDuration"] = after / before
            coverage[key] = coverage.get(key, 0) + 1
        except (KeyError, ValueError, OSError, TypeError) as error:
            result["errors"].append(str(error))
            errors.append(str(result["id"]) + ": " + str(error))
        results.append(result)
    groups = []
    for mode in ("general", "coding", "science"):
        for cache in ("cold", "warm"):
            matched = [result for result in results if result["mode"] == mode and result["cache"] == cache and not result["errors"]]
            if coverage.get((mode, cache), 0) != 3:
                errors.append(f"{mode}/{cache}: requires exactly three independent successful pairs")
            groups.append({"mode": mode, "cache": cache, "successfulPairs": len(matched),
                           "medianAfterOverBeforeDuration": statistics.median(result["afterOverBeforeDuration"] for result in matched) if len(matched) == 3 else None})
    complete = not errors
    return {"schema": "missum.context-efficiency-report.v1", "status": "complete" if complete else "incomplete",
            "canClaimSpeedComparison": complete, "allModeCacheGroupsFaster": all(group["medianAfterOverBeforeDuration"] < 1 for group in groups) if complete else None,
            "measurement": "sum of sequential gateway request-to-completion durations; user idle excluded; child work overlaps and is never added to wall time",
            "groups": groups, "pairs": results, "errors": errors}


def example_manifest():
    return {"schema": "missum.context-efficiency.v1", "minimumWarmReuseFraction": 0.5,
            "pairs": [{"id": f"{mode}-{cache}-{repeat}", "mode": mode, "cache": cache,
                       "fixtureId": f"{mode}-representative-v1", "fixtureSha256": None, "acceptanceSha256": None,
                       "modelId": None, "reasoningEffort": None,
                       "requiredChecks": ["taskComplete", "permissionsPreserved", "endToEndTimingVerified"] + {
                           "general": ["followupComplete", "targetedSource"],
                           "coding": ["readEditVerified"],
                           "science": ["foundationsPdf", "scientificCheck", "restartContinuation"],
                       }[mode],
                       "before": {"runId": None, "acceptanceFile": None, "acceptanceEvidenceSha256": None},
                       "after": {"runId": None, "acceptanceFile": None, "acceptanceEvidenceSha256": None}}
                      for mode in ("general", "coding", "science") for cache in ("cold", "warm") for repeat in range(1, 4)]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", type=Path)
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--run", action="append", default=[])
    parser.add_argument("--output", type=Path)
    parser.add_argument("--example-manifest", action="store_true")
    parser.add_argument("--baseline", type=Path, help="Existing observations; never accepted as paired benchmark evidence")
    args = parser.parse_args()
    if args.example_manifest:
        report = example_manifest()
    else:
        if args.database is None or bool(args.manifest) == bool(args.run):
            parser.error("provide --database and exactly one of --manifest or --run")
        with closing(open_database(args.database)) as connection:
            report = compare_manifest(connection, read_json(args.manifest), args.manifest.resolve().parent) if args.manifest else {
                "status": "observations", "canClaimSpeedComparison": False,
                "runs": [collect_run(connection, run_id) for run_id in args.run]}
        if args.baseline:
            baseline = read_json(args.baseline)
            report["baselineObservation"] = {"path": str(args.baseline.resolve()), "sha256": digest_file(args.baseline),
                                             "kind": baseline.get("kind"), "runCount": len(baseline.get("runs", [])),
                                             "eligibleForPairedComparison": False}
        report["capturedAt"] = datetime.now(timezone.utc).isoformat()
    content = json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False)
    if args.output:
        args.output.write_text(content + "\n", encoding="utf-8")
    else:
        print(content)
    return 2 if report.get("status") == "incomplete" else 0


if __name__ == "__main__":
    raise SystemExit(main())
