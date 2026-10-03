"""Real native acceptance: exact-model replicas, independent KV and overlapping GPU inference.

The selected primary must already be loaded on GPU0. This gate never changes
model selection or unloads models. Run with idle gateway/native slots only.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import threading
import time
import urllib.parse
import uuid
from catalog import gpu_inventory
from verify_session_cache import Probe, initial


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", default="http://127.0.0.1:8081")
    parser.add_argument("--control", default="http://127.0.0.1:8082")
    parser.add_argument("--model", required=True)
    parser.add_argument("--report", required=True, type=Path)
    args = parser.parse_args()
    probe = Probe(args.base, args.control, [])
    report = dict(passed=False, model=args.model, layer="native-dual-gpu", rows=[], overlapObserved=False)
    stop = threading.Event()
    try:
        models = probe.request("v1/models")["data"]
        for model in models:
            if model.get("status", {}).get("value") in ("loaded", "sleeping", "loading"):
                slots = probe.request("slots?model=" + urllib.parse.quote(model["id"], safe=""))
                if any(slot.get("is_processing") for slot in slots):
                    raise RuntimeError("Native runtime has an active generation")
        availability = probe.request("agents/status", dict(model=args.model), control=True)
        report["availability"] = availability
        if not availability.get("allowed"):
            raise AssertionError("Native replica is not available: " + json.dumps(availability))
        nonce = uuid.uuid4().hex
        parent_key, child_key = "missum-dual-gpu/" + nonce + "/parent", "missum-dual-gpu/" + nonce + "/child"
        messages, prefix_tokens = probe.turn("parent-seed", args.model, parent_key, initial("parallel-agents", nonce))
        report["seed"] = probe.results[0]
        def branch_prompt(role):
            return [*messages, dict(role="user", content="Nenne zuerst das Kennwort aus dem Kontext. "
                "Schreibe danach die Zahlen 1 bis 80 mit je einer kurzen deutschen Beschreibung. Rolle: " + role)]

        preparation = probe.request("agents/prepare", dict(model=args.model,
            parentSessionCacheKey=parent_key, childSessionCacheKey=child_key,
            prefill=dict(messages=branch_prompt("Subagent"), reasoning_effort="none",
                chat_template_kwargs=dict(enable_thinking=False, reasoning_effort="none"))), control=True)
        report["preparation"] = preparation
        if preparation.get("cacheStatus") != "forked" or preparation.get("cachedTokens", 0) <= 0:
            raise AssertionError("Child did not restore a forked KV snapshot")
        child_model = preparation["instanceId"]
        report["instanceId"] = child_model
        models = probe.request("v1/models")["data"]
        devices = {gpu["index"]: gpu["device"] for gpu in gpu_inventory()}
        for model_id, gpu_index in ((args.model, 0), (child_model, 1)):
            selected = next(model for model in models if model["id"] == model_id)
            arguments = selected["status"]["args"]
            device = arguments[arguments.index("--device") + 1]
            if device != devices[gpu_index] or arguments[arguments.index("--split-mode") + 1] != "none":
                raise AssertionError("Incorrect physical GPU placement: " + model_id)
            report.setdefault("placements", []).append(dict(model=model_id, gpuIndex=gpu_index, device=device,
                context=probe.request("props?model=" + urllib.parse.quote(model_id, safe=""))["default_generation_settings"]["n_ctx"]))
        if report["placements"][0]["context"] != report["placements"][1]["context"]:
            raise AssertionError("Parent and child have different KV geometry")
        for model_id, key in ((args.model, parent_key), (child_model, child_key)):
            prepared = probe.request("sessions/prepare", dict(model=model_id, sessionKey=key), control=True)
            if prepared.get("status") != "resident":
                raise AssertionError("Forked prefix must remain resident before parallel inference")

        def sample_overlap():
            while not stop.is_set():
                try:
                    states = [probe.request("slots?model=" + urllib.parse.quote(model_id, safe=""))
                              for model_id in (args.model, child_model)]
                    if all(any(slot.get("is_processing") for slot in slots) for slots in states):
                        report["overlapObserved"] = True
                        report["overlapObservedAt"] = time.time()
                        return
                except Exception:
                    pass
                stop.wait(0.1)

        def turn(model, key, role):
            prompt = branch_prompt(role)
            started = time.time()
            response = probe.request("v1/chat/completions", dict(model=model, messages=prompt, stream=False,
                id_slot=0, cache_prompt=True, temperature=0, max_tokens=256,
                reasoning_effort="none", chat_template_kwargs=dict(enable_thinking=False, reasoning_effort="none")))
            finished = time.time()
            usage = response.get("usage", {})
            cached = usage.get("prompt_tokens_details", {}).get("cached_tokens")
            content = response.get("choices", [{}])[0].get("message", {}).get("content", "")
            saved = probe.request("sessions/save", dict(model=model, sessionKey=key), control=True)
            row = dict(role=role, model=model, startedAt=started, finishedAt=finished, usage=usage,
                timings=response.get("timings"), save=saved, content=content,
                requiredCachedTokens=max(16, prefix_tokens // 2))
            if type(cached) is not int or cached < row["requiredCachedTokens"]:
                raise AssertionError(role + " lacks measured KV reuse: " + str(cached))
            if "eiche" not in content.lower():
                raise AssertionError(role + " did not receive the shared context")
            if saved.get("status") != "saved":
                raise AssertionError(role + " snapshot did not save")
            return row

        sampler = threading.Thread(target=sample_overlap, daemon=True)
        sampler.start()
        with ThreadPoolExecutor(max_workers=2) as pool:
            parent = pool.submit(turn, args.model, parent_key, "Hauptagent")
            child = pool.submit(turn, child_model, child_key, "Subagent")
            report["rows"] = [parent.result(), child.result()]
        stop.set()
        sampler.join(timeout=5)
        if not report["overlapObserved"]:
            raise AssertionError("The two native model slots were never observed processing together")
        rows = report["rows"]
        report["overlapSeconds"] = min(row["finishedAt"] for row in rows) - max(row["startedAt"] for row in rows)
        if report["overlapSeconds"] <= 0:
            raise AssertionError("Native request intervals did not overlap")
        report["passed"] = True
    except Exception as error:
        report["error"] = str(error)
    finally:
        stop.set()
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(dict(passed=report["passed"], report=str(args.report), error=report.get("error"))))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
