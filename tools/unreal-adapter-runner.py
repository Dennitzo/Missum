# SPDX-License-Identifier: MIT
"""Führt Szenarioaufträge durch den echten Missum-Unreal-Adapter aus.

Anders als das direkte Kanalkommando läuft hier jeder Auftrag über dieselbe Zuordnung und
dieselben Prüfungen, die auch das Sprachmodell durchläuft: Werkzeugname, Assetpfadgrenze,
Importgrenze, Vorgangskennung und die Antwortform. Der Adapter bekommt die Bereitschafts-
quittung als lokale Ergänzung, genau wie im Zusammenspiel mit der Anwendung.

    python unreal-adapter-runner.py <adapter.exe> <readyPath> <workspaceRoot> <szenario.json>

Szenario: ein Array aus Objekten mit "tool", "arguments" und optional "timeout".
"""

import json
import os
import subprocess
import sys
import time


ACTION = "com.missum.unreal/edit"


def main(argv):
    if len(argv) < 5:
        print(__doc__)
        return 2
    adapter, ready_path, workspace, scenario_path = argv[1:5]
    with open(ready_path, "r", encoding="utf-8") as stream:
        receipt = json.load(stream)
    with open(scenario_path, "r", encoding="utf-8") as stream:
        entries = json.load(stream)

    environment = dict(os.environ)
    environment["ASSISTANT_EXTENSION_WORKSPACE_ROOT"] = workspace
    failures = 0

    for position, entry in enumerate(entries, start=1):
        tool = entry.get("tool") or entry.get("command")
        if not tool:
            print("Szenarieintrag ohne Werkzeugname, übersprungen.")
            failures += 1
            continue
        arguments = dict(entry.get("arguments") or {})
        arguments["_missumUnreal"] = {
            "port": receipt["port"],
            "processId": receipt["processId"],
            "token": receipt["token"],
        }
        request = {
            "protocolVersion": 1,
            "type": "action.invoke",
            "requestId": f"runner-{position}-{int(time.time() * 1000)}",
            "payload": {"actionId": ACTION, "modelToolName": tool, "arguments": arguments},
        }
        print(f"\n=== ADAPTER {tool} ===", flush=True)
        try:
            completed = subprocess.run(
                [adapter],
                input=json.dumps(request, ensure_ascii=False) + "\n",
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                env=environment,
                timeout=float(entry.get("timeout", 600)),
            )
        except subprocess.TimeoutExpired:
            print(f"KEINE ANTWORT nach {entry.get('timeout', 600)} s")
            failures += 1
            continue

        lines = [line for line in (completed.stdout or "").splitlines() if line.strip()]
        if not lines:
            print(f"Adapter ohne Ausgabe, Exitcode {completed.returncode}")
            print((completed.stderr or "")[:800])
            failures += 1
            continue
        try:
            response = json.loads(lines[-1])
        except ValueError:
            print(lines[-1][:3000])
            failures += 1
            continue

        printed = json.dumps(response, ensure_ascii=False, indent=1)
        limit = int(os.environ.get("MISSUM_UNREAL_CLIENT_PRINT_LIMIT", "4000"))
        print(printed[:limit])
        if len(printed) > limit:
            print(f"… Antwort auf {len(printed)} Zeichen gekürzt.")
        if response.get("type") != "action.result":
            print(f"FEHLER {tool}: {response.get('type')} {json.dumps(response)[:400]}")
            failures += 1

    print(f"\nAdapterszenario abgeschlossen, {failures} fehlerhafte Aufträge.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
