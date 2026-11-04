"""Bounded live probe of native reasoning and exact history rendering.

The visible answer is requested in German; reasoning follows model defaults.
"""
import argparse
import json
from pathlib import Path
import urllib.request
from verify_session_cache import Probe


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", default="http://127.0.0.1:8081")
    parser.add_argument("--control", default="http://127.0.0.1:8082")
    parser.add_argument("--model", required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()

    def request(path, body):
        req = urllib.request.Request(args.base.rstrip("/") + "/" + path,
            data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=180) as response:
            return json.load(response)

    messages = [dict(role="system", content="Antworte auf Deutsch."),
        dict(role="user", content="Explain why this Python function can fail, and name one concrete improvement: def divide(a, b): return a / b. Keep the answer brief.")]
    cases, error = [], None
    try:
        probe = Probe(args.base, args.control, [])
        probe.ensure(args.model)
        probe.request("sessions/prepare", dict(model=args.model, sessionKey=None), control=True)
        previous_prompt, previous_reasoning, previous_usage = None, None, None
        for effort in ("low", "low"):
            body = dict(model=args.model, messages=messages, stream=False, max_tokens=512,
                cache_prompt=True, temperature=0, reasoning_effort=effort,
                chat_template_kwargs=dict(enable_thinking=True, reasoning_strength=effort))
            rendered = request("apply-template", body)["prompt"]
            response = request("v1/chat/completions", body)
            message = response["choices"][0]["message"]
            reasoning = message.get("reasoning_content", "")
            prefix_matches = previous_prompt is None or rendered.startswith(previous_prompt + previous_reasoning)
            case = dict(effort=effort, reasoning=reasoning, answer=message.get("content"),
                usage=response.get("usage"), finishReason=response["choices"][0].get("finish_reason"),
                exactHistoricalReasoningPrefix=prefix_matches)
            cases.append(case)
            if not reasoning:
                raise AssertionError("Fixed code probe did not produce reasoning")
            if not prefix_matches:
                raise AssertionError("Native template changed historical reasoning instead of retaining its exact prefix")
            if len(cases) == 2:
                cached = response.get("usage", {}).get("prompt_tokens_details", {}).get("cached_tokens", 0)
                if cached < previous_usage["total_tokens"] - 8:
                    raise AssertionError("Reasoning follow-up must reuse the previous generated tail, not just a generic system prefix")
            previous_prompt, previous_reasoning, previous_usage = rendered, reasoning, response["usage"]
            messages = [*messages, message, dict(role="user", content="Erkläre nun kurz die Behandlung ungültiger Datentypen.")]
    except Exception as exception:
        error = str(exception)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(dict(passed=error is None, error=error, model=args.model, cases=cases), ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(dict(passed=error is None, error=error, report=str(args.report))))
    return 0 if error is None else 1


if __name__ == "__main__":
    raise SystemExit(main())
