# SPDX-License-Identifier: MIT
"""Sonde für die tatsächliche Epics-Python-Oberfläche einer installierten Unreal Engine.

Diese Datei ist kein Bestandteil der Erweiterungslaufzeit. Sie wird einmal gegen die
Engine ausgeführt, die Missum später anspricht, und schreibt einen JSON-Bericht mit den
Fähigkeiten, die in dieser Engineversion über Python erreichbar sind. Jeder Befund
beruht auf dem laufenden Interpreter, nicht auf Dokumentation.

Jede Abfrage läuft über die Hilfsfunktion `call`, die den Namen zur Laufzeit auflöst.
Ein fehlendes Attribut ist deshalb ein mitgeschriebener Befund und kein Abbruch.

Aufruf:
    UnrealEditor-Cmd.exe "<projekt>.uproject" -run=pythonscript
        -script="<diese Datei>" -stdout -unattended -nullrhi

Das Zielverzeichnis der Berichtdatei steht in der Umgebungsvariablen
MISSUM_UNREAL_PROBE_OUT. Fehlt sie, schreibt die Sonde nach Saved/Missum im Projekt.
"""

import json
import os
import sys
import time

import unreal


REPORT = {}


def resolve(dotted):
    value = unreal
    for part in dotted.split("."):
        value = getattr(value, part)
    return value


def call(dotted, *arguments):
    """Löst eine Funktion anhand ihres Namens auf und fängt jeden Fehler als Befund ab."""
    try:
        return resolve(dotted)(*arguments)
    except BaseException as error:
        return {"__fehler__": f"{type(error).__name__}: {error}"}


def has(name):
    try:
        return hasattr(unreal, name)
    except BaseException:
        return False


def class_names(pattern, limit=160):
    try:
        return [name for name in dir(unreal) if pattern.lower() in name.lower()][:limit]
    except BaseException as error:
        return {"__fehler__": str(error)}


def members(class_name, *filters, **keyword):
    """Namen einer Klasse, optional gefiltert, mit Signaturtext, wo pybind ihn liefert."""
    limit = int(keyword.get("limit", 400))
    try:
        class_value = resolve(class_name)
    except BaseException as error:
        return {"__klasse__": f"{type(error).__name__}: {error}"}
    found = []
    for name in dir(class_value):
        if name.startswith("_"):
            continue
        lowered = name.lower()
        if filters and not any(str(item).lower() in lowered for item in filters):
            continue
        kind = ""
        signature = ""
        try:
            attribute = getattr(class_value, name)
            kind = type(attribute).__name__
            if kind in ("builtin_function_or_method", "method_descriptor", "function", "member_descriptor"):
                signature = (getattr(attribute, "__doc__", "") or "").split("\n")[0][:400]
            elif kind == "getset_descriptor" or "property" in kind:
                signature = "<eigenschaft>"
        except BaseException as error:
            kind = f"__fehler__: {type(error).__name__}"
        found.append({"name": name, "kind": kind, "signature": signature})
        if len(found) >= limit:
            break
    return found


def section(key, value):
    try:
        REPORT[key] = value
    except BaseException as error:
        REPORT[key] = {"__fehler__": str(error)}


def main():
    started = time.time()
    section("probeStartedAt", time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(started)))
    section("pythonVersion", sys.version.split()[0])
    section("engineVersion", call("SystemLibrary.get_engine_version"))
    section("projectDirectory", call("Paths.project_dir"))
    section("projectSavedDirectory", call("Paths.project_saved_dir"))
    section("pathsMembers", members("Paths"))

    # 1) Kanalgrundlage: gibt es einen Tick-Eingang ohne eigenen Thread?
    section("channel", {
        "registerOnTick": has("register_on_tick"),
        "unregisterOnTick": has("unregister_on_tick"),
        "onTickNames": class_names("on_tick"),
        "onWorldNames": class_names("on_world"),
        "gameThreadNames": class_names("game_thread"),
        "systemLibraryThread": members("SystemLibrary", "thread"),
        "pythonPluginsEnabled": class_names("pip"),
    })

    # 2) Welten, Karten, Level
    section("level", {
        "LevelEditorSubsystem": members("LevelEditorSubsystem"),
        "EditorLevelLibrary": members("EditorLevelLibrary", limit=220),
        "getEditorSubsystem": has("get_editor_subsystem"),
        "subsystemNames": class_names("subsystem", 90),
        "worldMembers": members("World", "partition", "world_type", "name"),
        "levelStreaming": class_names("level", 90),
    })

    # 3) Schauspieler, Transformation, Begrenzung, Linienwurf
    section("actor", {
        "EditorActorSubsystem": members("EditorActorSubsystem"),
        "ActorRelevant": members("Actor", "bounds", "label", "transform", "location", "rotation", "scale", "visible", "materials"),
        "SystemLibraryTrace": members("SystemLibrary", "trace"),
        "SystemLibraryConsole": members("SystemLibrary", "console", "exec"),
        "enums": {
            "LineTraceFlags": has("LineTraceFlags"),
            "CollisionChannel": has("CollisionChannel"),
            "ActorSpawnMethod": has("ActorSpawnMethod"),
            "HiddenEditOrByDefault": has("HiddenEditFlags"),
        },
        "Vector": members("Vector"),
        "Rotator": members("Rotator"),
        "Box": members("Box"),
    })

    # 4) Assets, Katalog, Import
    section("asset", {
        "AssetToolsHelpers": members("AssetToolsHelpers"),
        "AssetTools": members("AssetTools"),
        "EditorAssetLibrary": members("EditorAssetLibrary"),
        "AssetRegistry": members("AssetRegistry"),
        "AssetRegistrySubsystem": members("AssetRegistrySubsystem"),
        "AssetData": members("AssetData"),
        "AssetImportTask": members("AssetImportTask"),
        "interchange": {"InterchangeManager": has("InterchangeManager"), "names": class_names("interchange", 90)},
        "factories": class_names("factory", 200),
        "StaticMesh": members("StaticMesh"),
        "StaticMeshComponent": members("StaticMeshComponent", "material", "bounds"),
        "Texture2D": members("Texture2D", "size", "format", "source"),
    })

    # 5) Materialien
    section("material", {
        "MaterialEditingLibrary": members("MaterialEditingLibrary"),
        "Material": members("Material"),
        "MaterialInstanceConstant": members("MaterialInstanceConstant"),
        "materialExpressions": class_names("materialexpression", 300),
        "blendAndFlags": {
            "BlendMode": has("BlendMode"),
            "ShadingModels": has("ShadingModels"),
            "MaterialUsage": has("MaterialUsage"),
            "MaterialDomain": has("MaterialDomain"),
            "ExpressionBase": has("MaterialExpression"),
        },
    })

    # 6) Blueprint und Graph
    section("blueprint", {
        "BlueprintEditorLibrary": members("BlueprintEditorLibrary"),
        "BlueprintFactory": members("BlueprintFactory"),
        "Blueprint": members("Blueprint"),
        "BlueprintCompiledStatus": has("BlueprintCompiledStatus"),
        "EdGraph": members("EdGraph"),
        "EdGraphNode": members("EdGraphNode"),
        "EdGraphPin": members("EdGraphPin"),
        "k2nodes": class_names("k2node", 200),
        "graphNames": class_names("graph", 200),
        "schemaActions": class_names("schemaaction", 90),
        "nodeFactory": class_names("nodefactory", 90),
        "dataAsset": {
            "DataAsset": has("DataAsset"),
            "DataAssetFactory": has("DataAssetFactory"),
            "BlueprintDataAssetFactory": has("BlueprintDataAssetFactory"),
            "UserDefinedStruct": has("UserDefinedStruct"),
            "structNames": class_names("struct", 60),
        },
        "componentClasses": class_names("component", 200),
    })

    # 7) Licht, Himmel, Nebel, Kamera
    section("environment", {
        "lightClasses": class_names("light", 140),
        "skyClasses": class_names("sky", 90),
        "fogClasses": class_names("fog", 70),
        "cameraClasses": class_names("camera", 90),
        "postProcess": class_names("postprocess", 70),
        "DirectionalLight": members("DirectionalLight"),
        "SkyLight": members("SkyLight"),
        "ExponentialHeightFog": members("ExponentialHeightFog"),
        "CameraComponent": members("CameraComponent", "fov", "field", "location", "rotation", "aperture", "focus"),
        "CameraActor": members("CameraActor"),
        "SceneCaptureComponent2D": members("SceneCaptureComponent2D", "capture", "target", "fov"),
    })

    # 8) Aufnahmewege
    section("capture", {
        "KismetRenderingLibrary": members("KismetRenderingLibrary"),
        "TextureRenderTarget2D": members("TextureRenderTarget2D"),
        "exportNames": class_names("export", 90),
        "screenshotNames": class_names("screenshot", 70),
        "captureNames": class_names("capture", 90),
        "automationNames": class_names("automation", 70),
        "executeConsoleCommand": members("SystemLibrary", "execute_console"),
        "engineShowConsole": has("EditorLevelLibrary") and members("EditorLevelLibrary", "screenshot", "capture"),
    })

    # 9) Konfiguration und Spielmodus
    section("configuration", {
        "configNames": class_names("config", 70),
        "settingsNames": class_names("settings", 90),
        "GameMapsSettings": has("GameMapsSettings"),
        "gameModeNames": class_names("gamemode", 50),
    })

    section("probeElapsedSeconds", round(time.time() - started, 2))
    section("probeComplete", True)

    target = os.environ.get("MISSUM_UNREAL_PROBE_OUT", "")
    if not target:
        saved = call("Paths.project_saved_dir")
        if isinstance(saved, dict):
            saved = os.path.join(os.getcwd(), "Saved")
        target = os.path.join(str(saved), "Missum", "api-probe.json")
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "w", encoding="utf-8") as stream:
        json.dump(REPORT, stream, indent=2, ensure_ascii=False, default=str)
    print(f"Missum API-Sonde geschrieben: {target} ({os.path.getsize(target)} Bytes)")


main()
