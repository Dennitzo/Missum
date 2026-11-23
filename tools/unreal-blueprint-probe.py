# SPDX-License-Identifier: MIT
"""Kurze Sonde für Blueprint- und Signaturwege in UE 5.8."""

import json
import os

import unreal


OUT = os.path.join(str(unreal.Paths.project_saved_dir()), "Missum", "blueprint-probe.json")
os.makedirs(os.path.dirname(OUT), exist_ok=True)


def doc(owner, name):
    value = getattr(owner, name, None)
    if value is None:
        return "fehlt"
    return (value.__doc__ or "").strip()[:900]


report = {
    "BlueprintEditorLibrary": {
        "create_blueprint_asset_with_parent": doc(unreal.BlueprintEditorLibrary, "create_blueprint_asset_with_parent"),
        "add_member_variable": doc(unreal.BlueprintEditorLibrary, "add_member_variable"),
        "compile_blueprint": doc(unreal.BlueprintEditorLibrary, "compile_blueprint"),
        "list_member_variable_names": doc(unreal.BlueprintEditorLibrary, "list_member_variable_names"),
        "add_function_graph": doc(unreal.BlueprintEditorLibrary, "add_function_graph"),
        "add_event_override": doc(unreal.BlueprintEditorLibrary, "add_event_override"),
        "get_blueprint_variable_category": doc(unreal.BlueprintEditorLibrary, "get_blueprint_variable_category"),
    },
    "EdGraphPinType": {
        "members": [name for name in dir(unreal.EdGraphPinType) if not name.startswith("_")],
        "pin_category": doc(unreal.EdGraphPinType, "pin_category"),
        "pin_sub_category_object": doc(unreal.EdGraphPinType, "pin_sub_category_object"),
    },
    "BlueprintFactory": {
        "members": [name for name in dir(unreal.BlueprintFactory) if not name.startswith("_")][:40],
        "parent_class": doc(unreal.BlueprintFactory, "parent_class"),
    },
    "asset_class_names": [
        name for name in dir(unreal)
        if "Blueprint" in name and not name.startswith("K2")
    ][:40],
}

with open(OUT, "w", encoding="utf-8") as stream:
    json.dump(report, stream, indent=1, ensure_ascii=False, default=str)

for key, value in report["BlueprintEditorLibrary"].items():
    print(f"--- {key}: {value[:420]}")
print("--- EdGraphPinType:", ", ".join(report["EdGraphPinType"]["members"][:30]))
print("--- pin_category:", report["EdGraphPinType"]["pin_category"][:220])
