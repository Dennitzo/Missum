# SPDX-License-Identifier: MIT
"""Minimale Laufzeit zur Eingrenzung: loggt nur und meldet die Kanal-Grundlagen.

Wenn der Editor mit dieser Datei stabil bleibt, liegt der Ausstieg an einem der
Bausteine von `unreal_bootstrap.py` und nicht am Schalter `-ExecutePythonScript`.
"""

import socket
import sys
import uuid

import unreal


try:
    unreal.EditorPythonScripting.set_keep_python_script_alive(True)
    unreal.log("MINIMAL: KeepPythonScriptAlive gesetzt")
except BaseException as error:
    unreal.log_warning(f"MINIMAL: KeepPythonScriptAlive nicht verfuegbar: {error}")

INSTANCE = uuid.uuid4().hex
print(f"MINIMAL-LAUFZEIT aktiv, Instanz {INSTANCE}, Python {sys.version.split()[0]}")
unreal.log(f"MINIMAL-LAUFZEIT aktiv, Instanz {INSTANCE}")

socket_probe = "socket-import-ok"
try:
    probe_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    probe_socket.bind(("127.0.0.1", 0))
    socket_probe = f"bind-ok-{probe_socket.getsockname()[1]}"
    probe_socket.close()
except BaseException as error:  # Nur melden, nichts abbrechen.
    socket_probe = f"bind-fehler: {error}"
unreal.log(f"MINIMAL socket: {socket_probe}")

ticker_probe = "nicht versucht"
try:
    state = {"ticks": 0}

    def _tick(delta):
        state["ticks"] += 1
        if state["ticks"] == 20:
            unreal.log(f"MINIMAL ticker: 20 Runden erreicht, delta={delta}")
        return True

    handle = unreal.register_ticker_callback(_tick, 0.05)
    ticker_probe = f"angemeldet {type(handle).__name__}"
except BaseException as error:
    ticker_probe = f"fehler: {error}"
unreal.log(f"MINIMAL ticker: {ticker_probe}")
