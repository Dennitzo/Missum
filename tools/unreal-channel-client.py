# SPDX-License-Identifier: MIT
"""Prüfclient für den Missum-Unreal-Kanal.

Werkzeug für Entwickler und für die Abnahme: es liest die Bereitschaftsquittung, die der
Editor schreibt, verbindet sich über Loopback-TCP und sendet genau ein Kommando im
Adapterprotokoll. Es ist nicht Teil der Erweiterung und wird nicht mitpaketiert.

    python unreal-channel-client.py <readyPath> <command> [jsonArgumente]

Beispiele:
    python unreal-channel-client.py %TEMP%\\ready.json status
    python unreal-channel-client.py %TEMP%\\ready.json level.list "{}"

Rückgabewert 0 bei Erfolg, 1 bei Fehlerantwort, 2 bei Verbindungsproblem.
"""

import json
import os
import socket
import sys
import time


def wait_for_receipt(path, timeout):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if os.path.isfile(path):
            try:
                with open(path, "r", encoding="utf-8") as stream:
                    return json.load(stream)
            except (OSError, ValueError):
                pass
        time.sleep(0.25)
    return None


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    ready_path, command = argv[1], argv[2]
    raw = argv[3] if len(argv) > 3 else "{}"
    if raw.startswith("@"):
        # PowerShell zerstört eingebettete Anführungszeichen auf der Kommandozeile, deshalb
        # können die Argumente auch aus einer Datei kommen.
        with open(raw[1:], "r", encoding="utf-8") as stream:
            arguments = json.load(stream)
    else:
        arguments = json.loads(raw)
    timeout = float(os.environ.get("MISSUM_UNREAL_CLIENT_TIMEOUT", "600"))

    receipt = wait_for_receipt(ready_path, timeout)
    if receipt is None:
        print(f"KEINE BEREITSCHAFTSQUITTUNG unter {ready_path} innerhalb {timeout:.0f} s.")
        return 2
    if receipt.get("initializationError"):
        print("Initialisierungsfehler des Editors:", receipt["initializationError"])

    port = int(receipt.get("port") or 0)
    token = receipt.get("token") or ""
    if port <= 0 or len(token) < 32:
        print(f"Quittung unvollständig: port={port} tokenLaenge={len(token)}")
        return 2
    print(f"Argumente: {json.dumps(arguments, ensure_ascii=False)[:900]}")
    print(f"Quittung: Port {port}, Prozess {receipt.get('processId')}, "
          f"Pump {receipt.get('channel', {}).get('pump')}, "
          f"Projekt {receipt.get('projectPath')}", flush=True)

    request = {
        "token": token,
        "requestId": f"check-{int(time.time() * 1000)}",
        "command": command,
        "arguments": arguments,
    }
    with socket.create_connection(("127.0.0.1", port), timeout=timeout) as connection:
        connection.sendall((json.dumps(request, ensure_ascii=False) + "\n").encode("utf-8"))
        stream = connection.makefile("rb")
        line = stream.readline()
    if not line:
        print("Der Editor hat die Verbindung ohne Antwort geschlossen.")
        return 2
    response = json.loads(line.decode("utf-8"))
    printed = json.dumps(response, ensure_ascii=False, indent=2)
    limit = int(os.environ.get("MISSUM_UNREAL_CLIENT_PRINT_LIMIT", "12000"))
    print(printed[:limit])
    if len(printed) > limit:
        print(f"… Antwort auf {len(printed)} Zeichen gekürzt, volle Länge {len(printed)}.")
    if response.get("ok"):
        return 0
    print(f"FEHLER {command}: {response.get('error', {}).get('code')} "
          f"{response.get('error', {}).get('message')}")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
