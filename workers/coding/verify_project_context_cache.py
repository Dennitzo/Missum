"""Opt-in real native project-cache probe; never load/unload models or reset/delete caches.

The selected model must already be loaded and every native slot idle. Creates one
unique project and independent temporary session identities. Actual cached_tokens
must prove prefix reuse; 'resident' and 'forked' preparation statuses alone do not.
Run only with the coordinating agent's release, outside a user generation.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import shutil
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

from session_cache import canonical_project_token_prefix


def model_state(item):
    status = item.get("status", {})
    return status.get("value") if isinstance(status, dict) else status


class ProjectCacheProbe:
    def __init__(self, base, control, model, timeout, report):
        self.base, self.control = base.rstrip("/"), control.rstrip("/")
        self.model, self.timeout, self.report = model, timeout, report

    def request(self, path, body=None, control=False):
        address = (self.control if control else self.base) + "/" + path
        request = urllib.request.Request(address,
            data=json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None,
            headers={"Content-Type": "application/json"})
        started = time.monotonic()
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                result = json.load(response)
        except urllib.error.HTTPError as error:
            raise RuntimeError(path + ": HTTP " + str(error.code) + " "
                + error.read(4096).decode("utf-8", "replace")) from error
        self.report["requests"].append(dict(path=path, control=control,
            elapsedSeconds=time.monotonic() - started))
        return result

    def assert_idle(self):
        models = self.request("v1/models").get("data", [])
        selected = next((item for item in models if item.get("id") == self.model), None)
        if selected is None or model_state(selected) != "loaded":
            raise RuntimeError("Selected model must already be loaded; probe will not change model residency")
        for item in models:
            state = model_state(item)
            if state == "loading":
                raise RuntimeError("Runtime is loading a model; no cache/inference operation was requested")
            if state not in ("loaded", "sleeping"):
                continue
            slots = self.request("slots?model=" + urllib.parse.quote(item["id"], safe=""))
            if any(slot.get("is_processing") for slot in slots):
                raise RuntimeError("Runtime is busy; no cache/inference operation was requested")

    def rendered_tokens(self, body, generation):
        rendered = self.request("apply-template", dict(body, model=self.model,
            add_generation_prompt=generation)).get("prompt")
        if not isinstance(rendered, str) or not rendered:
            raise AssertionError("Native template returned no prompt")
        tokens = self.request("tokenize", dict(model=self.model, content=rendered,
            add_special=True, parse_special=True)).get("tokens")
        if not isinstance(tokens, list) or not tokens or any(type(token) is not int for token in tokens):
            raise AssertionError("Native tokenizer returned no token vector")
        return rendered, tokens

    def tokens(self, body, generation):
        return self.rendered_tokens(body, generation)[1]

    def turn(self, scenario, session, inference, minimum_cached, prepare_body=None, expected_word=None):
        self.assert_idle()
        started = time.monotonic()
        if prepare_body is None:
            prepare = self.request("sessions/prepare", dict(model=self.model, sessionKey=session,
                sessionId=session), control=True)
        else:
            prepare = self.request("sessions/project-prepare", prepare_body, control=True)
        prepared_at = time.monotonic()
        row = dict(scenario=scenario, session=session, prepare=prepare,
            preparationSeconds=prepared_at - started, requiredCachedTokens=minimum_cached, passed=False,
            basePrefill=dict(evaluatedTokens=prepare.get("sourceEvaluatedTokens"),
                reusedTokens=prepare.get("sourceCachedTokens"), sampledTokens=prepare.get("preparationSampledTokens"),
                note="Preparation wall time includes native prefill, snapshot writes/copy/restore; no isolated base-prefill duration is exposed."))
        self.report["scenarios"].append(row)
        if prepare.get("status") not in ("forked", "restored", "resident", "miss"):
            raise AssertionError(scenario + ": preparation failed: " + json.dumps(prepare, ensure_ascii=False))
        self.assert_idle()
        response = self.request("v1/chat/completions", inference)
        completed_at = time.monotonic()
        usage = response.get("usage", {})
        cached = usage.get("prompt_tokens_details", {}).get("cached_tokens")
        answer = response.get("choices", [{}])[0].get("message", {})
        row.update(usage=usage, timings=response.get("timings"),
            inferenceSeconds=completed_at - prepared_at, answer=answer,
            contextLimit=self.report.get("contextLimit"))
        if answer.get("role") != "assistant" or answer.get("tool_calls"):
            raise AssertionError(scenario + ": response did not produce a plain assistant message")
        if type(cached) is not int or cached < minimum_cached:
            raise AssertionError(scenario + ": actual cached_tokens=" + str(cached)
                + ", required >= " + str(minimum_cached))
        if expected_word is not None and (answer.get("content") or "").strip().strip("\"'. \n") != expected_word:
            raise AssertionError(scenario + ": branch answer did not retain its own private marker")
        self.assert_idle()
        saved = self.request("sessions/save", dict(model=self.model, sessionKey=session, force=True), control=True)
        if saved.get("status") not in ("saved", "unchanged"):
            raise AssertionError(scenario + ": snapshot save failed: " + json.dumps(saved))
        row.update(save=saved, totalSeconds=time.monotonic() - started, passed=True)
        clean = {field: answer[field] for field in ("role", "content", "reasoning_content", "tool_calls") if field in answer}
        return [*inference["messages"], clean]


def check_cache_headroom(directory, reserve_gib):
    """Conservatively prevent the worker's ordinary pruning from evicting user snapshots."""
    directory = directory.resolve()
    existing = {path.name: path.stat().st_size for path in directory.glob("*.bin") if path.is_file()}
    reserve = max(int(reserve_gib * 1024**3), max(existing.values(), default=0))
    required = 5 * reserve  # immutable base, two branches, mismatch branch, possible save of prior resident owner
    total = sum(existing.values())
    free = shutil.disk_usage(directory if directory.exists() else directory.parent).free
    if total + required >= 64 * 1024**3 or free < required + 4 * 1024**3:
        raise RuntimeError("Insufficient conservative cache headroom; probe refuses to trigger eviction of user snapshots")
    return dict(directory=str(directory), existingSnapshots=len(existing), existingBytes=total,
        reservedProbeBytes=required, freeBytes=free, snapshotsBefore=existing)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", default="http://127.0.0.1:8081")
    parser.add_argument("--control", default="http://127.0.0.1:8082")
    parser.add_argument("--model", required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=1800)
    parser.add_argument("--cache-directory", type=Path, default=Path.home() / ".missum" / "native-runtime" / "session-cache")
    parser.add_argument("--snapshot-reserve-gib", type=float, default=4)
    args = parser.parse_args()
    if args.timeout <= 0 or args.snapshot_reserve_gib <= 0:
        parser.error("Timeout and snapshot reserve must be positive")
    report = dict(passed=False, layer="real-native-runtime", model=args.model, scenarios=[], requests=[],
        note="Isolated project/session identities. No model load/unload, service restart, global cache reset or deletion.")
    probe = ProjectCacheProbe(args.base, args.control, args.model, args.timeout, report)
    try:
        probe.assert_idle()  # Read-only gate must precede every native mutation.
        report["cacheHeadroom"] = check_cache_headroom(args.cache_directory, args.snapshot_reserve_gib)
        properties = probe.request("props?model=" + urllib.parse.quote(args.model, safe=""))
        report["contextLimit"] = properties.get("default_generation_settings", {}).get("n_ctx")
        nonce = uuid.uuid4().hex
        project, epoch = "probe-project-" + nonce, "probe-" + nonce
        tools = [dict(type="function", function=dict(name="probe_observation_only", description="Nur ein Katalogeintrag, niemals aufrufen.",
            parameters=dict(type="object", properties={}, additionalProperties=False)))]
        facts = "\n".join("Notiz " + str(index) + ": Gemeinsames Projekt " + nonce
            + ", feststehende Information Nummer " + str(index) + "." for index in range(48))
        prefix = [dict(role="system", content="Antworte ausschließlich mit dem verlangten einzelnen Wort. Keine Denktexte. Keine Werkzeuge aufrufen."),
            dict(role="user", content="MISSUM_SHARED_PROJECT_CONTEXT_V1\n" + project + "\n" + facts
                + "\n\nMISSUM_SHARED_PROJECT_CONTEXT_END_V1\n")]
        common = dict(model=args.model, tools=tools, tool_choice="auto", temperature=0, stream=False,
            cache_prompt=True, max_tokens=64, chat_template_kwargs=dict(enable_thinking=False))
        prefill = dict(common, messages=prefix, add_generation_prompt=False)
        prefix_rendered, original_prefix_tokens = probe.rendered_tokens(prefill, False)
        report["projectKey"], report["cacheEpoch"] = project, epoch
        if not isinstance(report["contextLimit"], int) or len(original_prefix_tokens) + 512 >= report["contextLimit"]:
            raise RuntimeError("Native context limit is unavailable or too small for this probe")
        digest = hashlib.sha256(json.dumps(prefill, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()).hexdigest()
        sessions = {letter: "probe-project-session-" + nonce + "-" + letter for letter in ("a", "b")}
        histories = {}
        prefix_tokens = None
        for letter, word in (("a", "ALPHA"), ("b", "BETA")):
            inference = dict(common, messages=[*prefix, dict(role="user", content="Nur in dieser Sitzung ist das private Kennwort " + word
                + ". Merke es dir. Antworte ausschließlich " + word + ".")])
            actual_rendered, actual = probe.rendered_tokens(inference, True)
            canonical = canonical_project_token_prefix(original_prefix_tokens, actual, prefix_rendered, actual_rendered,
                prefix[-1]["content"], lambda vector: probe.request("detokenize", dict(model=args.model, tokens=vector)).get("content"))
            if prefix_tokens is not None and canonical != prefix_tokens:
                raise AssertionError("Independent sessions did not share the same complete-content canonical prefix")
            prefix_tokens = canonical
            threshold = max(1, len(prefix_tokens) - 1)
            report["sharedPrefixTokens"] = len(prefix_tokens)
            report["trimmedProjectFramingTokens"] = len(original_prefix_tokens) - len(prefix_tokens)
            report["prefixTokenSha256"] = hashlib.sha256(json.dumps(prefix_tokens, separators=(",", ":")).encode()).hexdigest()
            prepare = dict(model=args.model, sessionKey=sessions[letter], sessionId=sessions[letter],
                projectKey=project, cacheEpoch=epoch, prefixHash=digest, prefill=prefill, firstInference=inference)
            histories[letter] = probe.turn("new-session-" + letter + "/project-prefix", sessions[letter],
                inference, threshold, prepare, word)
        for letter, word in (("a", "ALPHA"), ("b", "BETA")):
            inference = dict(common, messages=[*histories[letter], dict(role="user",
                content="Welches private Kennwort gilt ausschließlich in dieser Sitzung? Antworte nur mit diesem Wort.")])
            histories[letter] = probe.turn("return-to-session-" + letter + "/independent-dialogue", sessions[letter],
                inference, threshold, expected_word=word)

        mismatch = dict(common, messages=[*prefix, dict(role="user", content="Antworte ausschließlich KONTROLLE.")], tools=copy.deepcopy(tools))
        mismatch["tools"][0]["function"]["name"] = "different_probe_tool_prefix"
        mismatch_tokens = probe.tokens(mismatch, True)
        if mismatch_tokens[:len(prefix_tokens)] == prefix_tokens:
            raise AssertionError("Changed tool schema did not alter the native prefix; this template cannot demonstrate the required mismatch gate")
        session = "probe-project-session-" + nonce + "-mismatch"
        probe.assert_idle()
        started = time.monotonic()
        rejected = probe.request("sessions/project-prepare", dict(model=args.model, sessionKey=session, sessionId=session,
            projectKey=project, cacheEpoch=epoch, prefixHash=digest, prefill=prefill, firstInference=mismatch), control=True)
        report["scenarios"].append(dict(scenario="mismatched-tool-prefix/rejected", prepare=rejected,
            preparationSeconds=time.monotonic() - started, passed=rejected.get("status") == "unavailable"))
        if rejected.get("status") != "unavailable" or "prefix" not in rejected.get("detail", "").lower():
            raise AssertionError("Mismatched tool prefix was not safely rejected")
        probe.turn("mismatched-tool-prefix/normal-session-fallback", session, mismatch, 0, expected_word="KONTROLLE")
        existing = report["cacheHeadroom"]["snapshotsBefore"]
        missing = [name for name in existing if not (args.cache_directory / name).is_file()]
        report["preservedExistingSnapshotFiles"] = not missing
        if missing:
            raise AssertionError("Existing cache snapshots disappeared during probe: " + ",".join(missing))
        report["passed"] = all(row["passed"] for row in report["scenarios"])
    except Exception as error:
        report["error"] = str(error)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(dict(passed=report["passed"], scenarios=len(report["scenarios"]), report=str(args.report), error=report.get("error"))))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
