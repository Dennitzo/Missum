# SPDX-License-Identifier: MIT
"""Einmalige Umstellung des Unreal-Manifests auf gebündelte Werkzeuge.

Das Gateway erlaubt höchstens 16 Modellwerkzeuge pro Aktion (`MaximumToolsPerAction`).
Der ausgebaute Katalog läst sich nur halten, wenn die neuen Befehle in Gruppenwerkzeuge mit
einem `operation`-Feld gebündelt werden. Dieses Skript erzeugt die neue Werkzeugliste aus
der bestehenden, damit keine Beschreibung verloren geht.
"""

import json
import pathlib


PATH = pathlib.Path("extensions/unreal/manifest/extension.json")


def merge_schemas(doc, names):
    properties = {}
    for name in names:
        for key, value in doc[name]["inputSchema"]["properties"].items():
            properties.setdefault(key, value)
    return properties


def main():
    doc = json.loads(PATH.read_text(encoding="utf-8"))
    old = {tool["modelToolName"]: tool for tool in doc["tools"]}

    def keep(name):
        return dict(old[name])

    def grouped(name, verbs, description, properties, timeout, output, risk):
        return {
            "actionId": "com.missum.unreal/edit",
            "modelToolName": name,
            "description": description,
            "inputSchema": {
                "type": "object",
                "properties": {"operation": {"type": "string", "enum": list(verbs)}, **properties},
                "required": ["operation"],
                "additionalProperties": False,
            },
            "timeoutSeconds": timeout,
            "maximumOutputBytes": output,
            "riskClass": risk,
        }

    blueprint_properties = merge_schemas(
        old, ["unreal.blueprint.create", "unreal.blueprint.read", "unreal.blueprint.edit",
              "unreal.blueprint.compile", "unreal.blueprint.save"])
    blueprint_properties.pop("operation", None)
    blueprint_properties["edit"] = {
        "type": "string",
        "enum": ["variable.add", "variable.remove", "graph.create",
                 "graph.remove", "node.add", "node.remove", "pin.connect",
                 "pin.disconnect", "component.add", "component.remove", "component.set"],
    }
    blueprint_properties["template"] = {
        "type": "string",
        "enum": ["gameMode", "playerCharacter", "npc", "questObjective", "questComponent"],
    }
    blueprint_properties["parentClass"] = {"type": "string"}
    blueprint_properties["assetType"] = {
        "type": "string",
        "enum": ["blueprint", "actorBlueprint", "componentBlueprint", "gameModeBlueprint",
                 "characterBlueprint", "dataAssetBlueprint", "widgetBlueprint", "animBlueprint"],
    }
    blueprint_properties["folder"] = {"type": "string"}
    blueprint_properties["assetName"] = {"type": "string"}

    material_properties = merge_schemas(
        old, ["unreal.material.create", "unreal.material.read", "unreal.material.edit",
              "unreal.material.assign"])
    material_properties.pop("operation", None)
    material_properties["edit"] = {
        "type": "string",
        "enum": ["parameter", "expression.add", "expression.remove", "expression.connect",
                 "property.connect", "blendMode", "shadingModel", "usage", "recompile"],
    }
    material_properties["assetPath"] = {"type": "string"}

    level_properties = merge_schemas(
        old, ["unreal.level.list", "unreal.level.create", "unreal.level.open",
              "unreal.level.save", "unreal.level.read"])
    asset_properties = merge_schemas(old, ["unreal.asset.list", "unreal.asset.read",
                                           "unreal.asset.import"])
    actor_properties = merge_schemas(old, ["unreal.actor.place", "unreal.actor.read",
                                           "unreal.actor.set", "unreal.actor.remove",
                                           "unreal.actor.verify"])
    camera_properties = merge_schemas(old, ["unreal.camera.create", "unreal.camera.set",
                                            "unreal.camera.frame"])
    light_properties = merge_schemas(old, ["unreal.light.create", "unreal.light.set",
                                           "unreal.light.read"])
    sky_properties = merge_schemas(old, ["unreal.sky.create", "unreal.sky.read"])

    tools = [
        keep("unreal.status"),
        keep("unreal.connect"),
        keep("unreal.launcher.start"),
        grouped("unreal.level", ["list", "create", "open", "save", "read"],
                "Karten bearbeiten: list, create, open, save, read. Meldet Assetpfade, Dateinachweis auf der Platte und Schauspieler je Klasse mit Ort und Ausdehnung.",
                level_properties, 300, 524288, "localMutation"),
        grouped("unreal.asset", ["list", "read", "import"],
                "Assetkatalog und Import: list, read, import. Holt FBX, obj oder glTF aus dem Projektordner nach /Game/ und nennt Materialfelder, Stufenzahl und Texturgroesse.",
                asset_properties, 600, 524288, "localMutation"),
        grouped("unreal.material", ["create", "read", "edit", "assign"],
                "Materialien: create, read, edit, assign. Liest Parameter, Ausdrucksknoten und die Schauspieler, die das Material in welchem Feld verwenden.",
                material_properties, 300, 524288, "localMutation"),
        grouped("unreal.actor", ["place", "read", "set", "remove", "verify"],
                "Schauspieler: place, read, set, remove, verify. verify meldet Lage, Ausdehnung, Materialfelder, Abstand und ob der Rahmen im Blickfeld liegt.",
                actor_properties, 300, 524288, "localMutation"),
        grouped("unreal.camera", ["create", "set", "frame"],
                "Kameras: create, set, frame. Richtet eine Kamera auf Auswahl, benannten Bereich oder die ganze Karte und setzt Sichtfeld und Blende.",
                camera_properties, 300, 262144, "localMutation"),
        grouped("unreal.light", ["create", "set", "read"],
                "Lichtquellen: create, set, read. Gerichtetes, Punkt-, Spot-, Rechteck-, Himmels-, Nebel- und Postprozesslicht mit Intensitaet, Farbe, Temperatur und Schatten.",
                light_properties, 300, 262144, "localMutation"),
        grouped("unreal.sky", ["create", "read"],
                "Himmel: create, read. Setzt Sonne, Himmelslicht, Atmosphaere, Nebel und Wolken aus Tageszeit oder Sonnenstand und meldet die berechneten Werte.",
                sky_properties, 300, 262144, "localMutation"),
        grouped("unreal.class", ["search"],
                "Klassen: search. Findet verfuegbare Elternklassen nach Suchwort samt Pfad und Mitgliedern.",
                {"query": {"type": "string"},
                 "limit": {"type": "integer", "minimum": 1, "maximum": 300}},
                90, 262144, "readOnly"),
        grouped("unreal.blueprint", ["create", "read", "edit", "compile", "save", "template"],
                "Blueprints: create, read, edit, compile, save, template. Die Teiloperation von edit steht im Feld edit, der Bauplan von template im Feld template.",
                blueprint_properties, 600, 524288, "localMutation"),
        keep("unreal.view.capture"),
    ]

    doc["version"] = "0.2.0"
    doc["tools"] = tools
    PATH.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    names = [tool["modelToolName"] for tool in tools]
    print(f"Werkzeuge jetzt: {len(names)}")
    for name in names:
        print(" -", name)
    too_long = [name for name, tool in zip(names, tools) if len(tool["description"]) > 299]
    print("Beschreibungen ueber 299 Zeichen:", too_long)


if __name__ == "__main__":
    main()
