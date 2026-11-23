# SPDX-License-Identifier: MIT
"""Drittsonde: exakte Signaturen und Verhalten der Kanal-Eingänge in UE 5.8.

Prüft `register_ticker_callback`, `register_slate_post_tick_callback` und
`register_python_shutdown_callback` samt Rückgabewert und ob der Ticker im
vorliegenden Ausführungskontext tatsächlich feuert.
"""

import json
import os
import time

import unreal


OUT = os.path.join(str(unreal.Paths.project_saved_dir()), "Missum", "ticker-probe.json")
os.makedirs(os.path.dirname(OUT), exist_ok=True)

report = {"engineVersion": unreal.SystemLibrary.get_engine_version()}

for name in (
    "register_ticker_callback", "unregister_ticker_callback",
    "register_slate_post_tick_callback", "unregister_slate_post_tick_callback",
    "register_slate_pre_tick_callback", "unregister_slate_pre_tick_callback",
    "register_python_shutdown_callback", "unregister_python_shutdown_callback",
):
    entry = {"exists": hasattr(unreal, name)}
    if entry["exists"]:
        entry["doc"] = (getattr(unreal, name).__doc__ or "").strip()
    report[name] = entry

# Reales Anmelde- und Auslöseverhalten im aktuellen Kontext.
fired = []


def _ticker(delta):
    fired.append(round(delta, 5))
    return True


registration = {}
for candidate in ("ticker", "slate_post_tick", "slate_pre_tick"):
    try:
        if candidate == "ticker":
            handle = unreal.register_ticker_callback("MissumProbeTicker", _ticker, 0.01)
        elif candidate == "slate_post_tick":
            handle = unreal.register_slate_post_tick_callback(_ticker)
        else:
            handle = unreal.register_slate_pre_tick_callback(_ticker)
        registration[candidate] = {"angemeldet": True, "handleTyp": type(handle).__name__,
                                   "handleWert": str(handle)[:120]}
    except BaseException as error:
        registration[candidate] = {"angemeldet": False,
                                   "fehler": f"{type(error).__name__}: {error}"}

report["registration"] = registration

deadline = time.time() + 3.0
while time.time() < deadline and not fired:
    time.sleep(0.05)
report["tickerFeuerteImCommandlet"] = {"anzahl": len(fired), "ersteWerte": fired[:5]}

for candidate, entry in registration.items():
    if not entry.get("angemeldet"):
        continue
    try:
        if candidate == "ticker":
            unreal.unregister_ticker_callback("MissumProbeTicker")
        elif candidate == "slate_post_tick":
            unreal.unregister_slate_post_tick_callback(entry["handleWert"])
        else:
            unreal.unregister_slate_pre_tick_callback(entry["handleWert"])
        entry["abgemeldet"] = True
    except BaseException as error:
        entry["abgemeldet"] = False
        entry["abmeldefehler"] = f"{type(error).__name__}: {error}"

with open(OUT, "w", encoding="utf-8") as stream:
    json.dump(report, stream, indent=1, ensure_ascii=False, default=str)
print(f"Tickersonde geschrieben: {OUT}")
