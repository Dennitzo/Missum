# SPDX-License-Identifier: MIT
"""Kurze Sonde für Begrenzungsrahmen und Kartensignaturen in UE 5.8."""

import json
import os

import unreal


OUT = os.path.join(str(unreal.Paths.project_saved_dir()), "Missum", "box-probe.json")
os.makedirs(os.path.dirname(OUT), exist_ok=True)


def members(class_name):
    try:
        value = getattr(unreal, class_name)
    except BaseException as error:
        return {"__fehler__": str(error)}
    found = []
    for name in dir(value):
        if name.startswith("_"):
            continue
        try:
            attribute = getattr(value, name)
            kind = type(attribute).__name__
        except BaseException as error:
            kind = f"__fehler__: {error}"
            attribute = None
        doc = ""
        if kind in ("builtin_function_or_method", "method_descriptor", "member_descriptor"):
            doc = (getattr(attribute, "__doc__", "") or "").split("\n")[0][:300]
        found.append({"name": name, "kind": kind, "doc": doc})
    return found


report = {
    "Box": members("Box"),
    "Vector": members("Vector"),
    "Rotator": members("Rotator"),
    "get_actor_bounds": (getattr(unreal.Actor, "get_actor_bounds", None).__doc__ or "")
    if hasattr(unreal.Actor, "get_actor_bounds") else "fehlt",
    "new_level": (getattr(unreal.LevelEditorSubsystem, "new_level", None).__doc__ or "")
    if hasattr(unreal.LevelEditorSubsystem, "new_level") else "fehlt",
    "save_current_level": (getattr(unreal.LevelEditorSubsystem, "save_current_level", None).__doc__ or "")
    if hasattr(unreal.LevelEditorSubsystem, "save_current_level") else "fehlt",
    "load_level": (getattr(unreal.LevelEditorSubsystem, "load_level", None).__doc__ or "")
    if hasattr(unreal.LevelEditorSubsystem, "load_level") else "fehlt",
    "new_level_from_template": (getattr(unreal.LevelEditorSubsystem, "new_level_from_template", None).__doc__ or "")
    if hasattr(unreal.LevelEditorSubsystem, "new_level_from_template") else "fehlt",
    "set_static_mesh": (getattr(unreal.StaticMeshComponent, "set_static_mesh", None).__doc__ or "")
    if hasattr(unreal.StaticMeshComponent, "set_static_mesh") else "fehlt",
}

with open(OUT, "w", encoding="utf-8") as stream:
    json.dump(report, stream, indent=1, ensure_ascii=False, default=str)

print("Box-Mitglieder:", [item["name"] for item in report["Box"] if isinstance(item, dict)][:40])
print("new_level:", report["new_level"][:400])
print("save_current_level:", report["save_current_level"][:300])
print("load_level:", report["load_level"][:200])
