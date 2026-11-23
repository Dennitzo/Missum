# SPDX-License-Identifier: MIT
"""Zweitsonde für den Kanal: welche Spielthread-Eingänge bietet UE 5.8 über Python?

Schreibt eine kompizierte Namensliste aller Modulnamen des `unreal`-Moduls sowie die
Volltexte ausgewählter Kandidaten. Grundlage für die Entscheidung, ob der Missum-Kanal
ohne eigenen Annahmefaden auskommt.
"""

import json
import os
import re
import sys

import unreal


OUT = os.path.join(str(unreal.Paths.project_saved_dir()), "Missum", "channel-probe.json")
os.makedirs(os.path.dirname(OUT), exist_ok=True)

PATTERNS = [
    "tick", "register", "thread", "deferred", "async", "schedule", "delegate",
    "timer", "ticker", "pump", "frame", "idle", "callback", "event_",
]

names = [name for name in dir(unreal)]
matches = {}
for pattern in PATTERNS:
    matches[pattern] = sorted(name for name in names if pattern in name.lower())

report = {
    "pythonVersion": sys.version.split()[0],
    "engineVersion": unreal.SystemLibrary.get_engine_version(),
    "totalModuleNames": len(names),
    "matches": matches,
    "moduleLevelFunctions": sorted(
        name for name in names
        if not name[0].isupper() and not name.startswith("_")
    ),
}


def dump_class(class_name):
    try:
        value = getattr(unreal, class_name)
    except BaseException as error:
        return {"__fehler__": str(error)}
    entries = []
    for name in dir(value):
        if name.startswith("_"):
            continue
        try:
            attribute = getattr(value, name)
            kind = type(attribute).__name__
            doc = (getattr(attribute, "__doc__", "") or "").split("\n")[0][:400]
        except BaseException as error:
            kind = f"__fehler__: {error}"
            doc = ""
        entries.append({"name": name, "kind": kind, "doc": doc})
    return entries


report["classes"] = {
    "LevelEditorSubsystem": dump_class("LevelEditorSubsystem"),
    "UnrealEditorSubsystem": dump_class("UnrealEditorSubsystem"),
    "TickableWorldSubsystem": dump_class("TickableWorldSubsystem"),
    "EditorActorSubsystem": dump_class("EditorActorSubsystem"),
}

with open(OUT, "w", encoding="utf-8") as stream:
    json.dump(report, stream, indent=1, ensure_ascii=False, default=str)

print(f"Kanalsonde geschrieben: {OUT} ({os.path.getsize(OUT)} Bytes)")
print("register_on_tick vorhanden:", hasattr(unreal, "register_on_tick"))
print("Tick-Kandidaten:", report["matches"]["tick"][:40])
print("Register-Kandidaten:", report["matches"]["register"][:40])
