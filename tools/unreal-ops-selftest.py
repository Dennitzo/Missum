# SPDX-License-Identifier: MIT
"""Selbsttest der Python-Laufzeit ohne laufende Unreal Engine.

Die Laufzeit importiert `unreal`; für reine Logikprüfungen genügt ein Platzhaltermodul, das
nur die Namen liefert, welche die geprüften Funktionen beim Aufruf berühren. Geprüft wird die
nachhaltige Logik: Pfadprüfung, Umrechnungen, Optionsübernahme, Fehlercodes, Vorgangsquittung
und das Schreiben von Konfigurationswerten.

    python tools/unreal-ops-selftest.py

Ohne Python-Interpreter meldet der zugehörige C+-Test einen begründeten Sprung.
"""

import json
import os
import sys
import tempfile
import types


RUNTIME = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                       "extensions", "unreal", "runtime")


class UnrealStub(types.ModuleType):
    """Mindestausstattung des unreal-Moduls für Logiktests ohne Engine."""

    def __init__(self):
        super().__init__("unreal")
        self._content = os.path.join(tempfile.gettempdir(), "missum-selftest-content")

    class _Paths:
        @staticmethod
        def project_content_dir():
            return os.path.join(tempfile.gettempdir(), "missum-selftest-content")

        @staticmethod
        def project_saved_dir():
            return os.path.join(tempfile.gettempdir(), "missum-selftest-saved")

        @staticmethod
        def project_config_dir():
            return os.path.join(tempfile.gettempdir(), "missum-selftest-config")

        @staticmethod
        def project_dir():
            return os.path.join(tempfile.gettempdir(), "missum-selftest-project")

    Paths = _Paths

    class Vector:
        def __init__(self, x=0.0, y=0.0, z=0.0):
            self.x, self.y, self.z = float(x), float(y), float(z)

    class Rotator:
        def __init__(self, pitch=0.0, yaw=0.0, roll=0.0):
            self.pitch, self.yaw, self.roll = float(pitch), float(yaw), float(roll)

    class LinearColor:
        def __init__(self, r=0.0, g=0.0, b=0.0, a=1.0):
            self.r, self.g, self.b, self.a = float(r), float(g), float(b), float(a)

    class Blueprint:
        pass

    class StaticMesh:
        pass

    class Texture2D:
        pass

    class CameraActor:
        pass

    class StaticMeshActor:
        pass

    class StaticMeshComponent:
        pass

    class PrimitiveComponent:
        pass

    class ActorComponent:
        pass

    class MaterialInterface:
        pass

    class Material:
        pass

    class MaterialInstanceConstant:
        pass

    class MaterialProperty:
        MP_BASE_COLOR = "MP_BASE_COLOR"
        MP_METALLIC = "MP_METALLIC"
        MP_ROUGHNESS = "MP_ROUGHNESS"
        MP_EMISSIVE_COLOR = "MP_EMISSIVE_COLOR"

    class EditorAssetLibrary:
        @staticmethod
        def does_asset_exist(path):
            return False

        @staticmethod
        def save_asset(path):
            return True

    class AssetToolsHelpers:
        @staticmethod
        def get_asset_tools():
            return object()

    class AssetImportTask:
        pass

    class MaterialEditingLibrary:
        @staticmethod
        def get_scalar_parameter_names(material):
            return []

        @staticmethod
        def get_vector_parameter_names(material):
            return []

        @staticmethod
        def get_texture_parameter_names(material):
            return []

        @staticmethod
        def get_static_switch_parameter_names(material):
            return []

        @staticmethod
        def get_material_expressions(material):
            return []

        @staticmethod
        def get_num_material_expressions(material):
            return 0

    class BlueprintEditorLibrary:
        @staticmethod
        def list_member_variable_names(blueprint):
            return ["/Script/Engine.Actor.Tags", "/Script/Engine.Actor.Label", "MissionActive"]

        @staticmethod
        def list_graph_names(blueprint):
            return ["EventGraph", "BeginMission"]

        @staticmethod
        def get_blueprint_parent_class(blueprint):
            return None

    class SystemLibrary:
        @staticmethod
        def get_engine_version():
            return "5.8.4-selftest"

    @staticmethod
    def get_editor_subsystem(value):
        return None

    @staticmethod
    def load_asset(path):
        return None

    @staticmethod
    def find_object(outer, path):
        return None


def _install_stub():
    stub = UnrealStub()
    sys.modules["unreal"] = stub
    sys.path.insert(0, RUNTIME)


_failures = []
_checks = 0


def check(condition, label):
    global _checks
    _checks += 1
    if not condition:
        _failures.append(label)
        print(f"FEHLEND: {label}")
    else:
        print(f"ok       {label}")


def main():
    if not os.path.isdir(RUNTIME):
        print(f"Laufzeitverzeichnis fehlt: {RUNTIME}")
        return 2
    _install_stub()
    import unreal_ops as ops
    import unreal_materials as materials
    import unreal_capture as capture
    import unreal_light as light
    import unreal_blueprint as blueprints
    print(f"Laufzeitmodule geladen; Register enthält {len(ops.OPERATIONS)} Kommandos.")

    # Pfadprüfung
    check(ops.valid_asset_path("/Game/Maps/Main"), "gf /Game/-Pfad angenommen")
    check(not ops.valid_asset_path("/Game/../Secret"), "Pfad mit Punkt-Punkt-Doppelpunkt abgelehnt")
    check(not ops.valid_asset_path("/Script/Engine.World"), "Fremder Mount abgelehnt")
    check(not ops.valid_asset_path("/Game/Back\\slash"), "Rückstrich abgelehnt")
    try:
        ops.require_asset_path({"assetPath": "OhneSchraegstrich"}, "assetPath")
        check(False, "ungültiger Assetpfad wirft")
    except ops.OpsError as error:
        check(error.code == "unreal.invalid_asset", "ungültiger Assetpfad meldet unreal.invalid_asset")

    # Ordnerumrechnung und Aufteilung
    check(ops.map_folder_for("") == "/Game/Maps", "leerer Kartenordner wird /Game/Maps")
    check(ops.map_folder_for("City/Grid") == "/Game/City/Grid", "relativer Ordner wird unter /Game/ gesetzt")
    try:
        ops.map_folder_for("/Game/../Windows")
        check(False, "Ordner mit Punkt-Punkt-Doppelpunkt wirft")
    except ops.OpsError:
        check(True, "Ordner mit Punkt-Punkt-Doppelpunkt wirft")
    folder, name = ops.split_asset_path("/Game/Maps/Main")
    check((folder, name) == ("/Game/Maps", "Main"), "Assetpfad in Ordner und Name getrennt")

    # Dateipfadumrechnung
    check(ops.package_file("/Game/Maps/Main").endswith(os.path.join("Maps", "Main.umap")),
          "Kartenpfad auf Platte richtig berechnet")
    check(ops.package_file("/Game/A/B", ".uasset").endswith(os.path.join("A", "B.uasset")),
          "Assetpfad mit eigener Endung berechnet")
    try:
        ops.package_file("/Script/Engine.World")
        check(False, "Nur /Game/-Pfade lassen sich umrechnen")
    except ops.OpsError:
        check(True, "Nur /Game/-Pfade lassen sich umrechnen")

    # Umrechnungen
    vector = ops.vector_dict(ops.unreal.Vector(1, 2, 3))
    check(vector == {"x": 1.0, "y": 2.0, "z": 3.0}, "Vektor in Wörterbuch umgerechnet")
    rotation = ops.rotation_dict(ops.unreal.Rotator(10, 20, 30))
    check(rotation == {"pitch": 10.0, "yaw": 20.0, "roll": 30.0}, "Dreher in Wörterbuch umgerechnet")
    angles = ops.angles_from({"rotation": {"pitch": 5, "yaw": 90}}, "rotation")
    check(angles == (5.0, 90.0, 0.0), "Winkel aus pitch, yaw, roll gelesen")
    position = ops.vector_from({"position": {"x": 1, "y": 2}}, "position")
    check(position == (1.0, 2.0, 0.0), "Position mit fehlender Achse auf null ergänzt")

    # Importgrenzen
    check(".fbx" in ops.IMPORT_EXTENSIONS and ".glb" in ops.IMPORT_EXTENSIONS,
          "unterstützte Importformate erfasst")
    try:
        ops.handle("asset.import", {"filePath": "/tmp/keine-datei.fbx", "operationId": "x1"},
                   {"workspace": tempfile.gettempdir(), "ledger": {}})
        check(False, "fehlende Importdatei gemeldet")
    except ops.OpsError as error:
        check(error.code in ("unreal.import_file_missing", "unreal.content_directory_unknown"),
              f"fehlende Importdatei gemeldet ({error.code})")

    # Zuordnung, Änderungen und Vorgangsquittung
    check(ops.is_mutating("blueprint.read") is False, "Lesender Blueprintbefehl gilt als Änderungsfrei")
    check(ops.is_mutating("blueprint.create") is True, "Schreibender Blueprintbefehl gilt als Änderung")
    check(ops.is_mutating("level.list") is False, "Kartenliste ist änderungsfrei")
    check(ops.is_mutating("material.edit") is True, "Materialbearbeitung ist eine Änderung")
    try:
        ops.handle("level.create", {"levelName": "OhneKennung"}, {"ledger": {}})
        check(False, "Änderung ohne Vorgangskennung abgelehnt")
    except ops.OpsError as error:
        check(error.code == "unreal.operation_id_required", "Änderung ohne Vorgangskennung abgelehnt")
    try:
        ops.handle("gibt.es.nicht", {}, {"ledger": {}})
        check(False, "unbekanntes Kommando abgelehnt")
    except ops.OpsError as error:
        check(error.code == "unreal.command_unknown", "unbekanntes Kommando abgelehnt")

    def dummy(arguments, context):
        return {"echo": ops.text(arguments, "wert")}

    ops.operation("blueprint.selftestecho", "Platzhalter für den Selbsttest.")(dummy)
    context = {"ledger": {}}
    first = ops.handle("blueprint.selftestecho", {"wert": "a", "operationId": "op-1"}, context)
    again = ops.handle("blueprint.selftestecho", {"wert": "a", "operationId": "op-1"}, context)
    check(first == again == {"echo": "a"}, "Wiederholung derselben Kennung liefert dieselbe Antwort")
    try:
        ops.handle("blueprint.selftestecho", {"wert": "b", "operationId": "op-1"}, context)
        check(False, "Kennung mit anderen Argumenten abgelehnt")
    except ops.OpsError as error:
        check(error.code == "unreal.operation_conflict", "Kennung mit anderen Argumenten abgelehnt")

    # Konfigurationsschreiben
    with tempfile.TemporaryDirectory() as directory:
        config = os.path.join(directory, "DefaultEngine.ini")
        with open(config, "w", encoding="utf-8") as stream:
            stream.write("[/Script/EngineSettings.GameMapsSettings]\n"
                         "EditorStartupMap=/Game/Old.Old\n"
                         "[/Script/Other]\nX=1\n")
        verified = ops.write_ini_values(config, "/Script/EngineSettings.GameMapsSettings",
                                        {"EditorStartupMap": "/Game/New.New",
                                         "GameMode": "/Game/BP.X_C"})
        content = open(config, encoding="utf-8").read()
        check(verified, "Konfigurationswerte rückgelesen und bestätigt")
        check("EditorStartupMap=/Game/New.New" in content and "GameMode=/Game/BP.X_C" in content,
              "neue Werte im Abschnitt gesetzt")
        check("[/Script/Other]" in content and "X=1" in content,
              "andere Abschnitte blieben erhalten")
        check(content.count("EditorStartupMap=") == 1, "Schlüssel nicht doppelt geschrieben")

    # Weichreferenzen und Materialfelder
    class SoftPath:
        def __init__(self, value):
            self.asset_path = value

    check(ops.soft_path_text(SoftPath("/Game/Materials/M_X")) == "/Game/Materials/M_X",
          "Weichreferenz als Assetkennung gelesen")
    check(ops.soft_path_text(None) == "", "leere Referenz ergibt leere Kennung")
    check(ops.soft_path_text("x") == "x", "einfache Zeichenkette durchgereicht")

    # Blueprintzusammenfassung filtert geerbte Eigenschaften
    class FakeBlueprint:
        def get_editor_property(self, name):
            raise RuntimeError(name)

        def get_class(self):
            raise RuntimeError("class")

    summary = ops.blueprint_summary(FakeBlueprint())
    check(summary["variableNames"] == ["MissionActive"], "nur eigene Variablen gemeldet")
    check(summary["inheritedVariableCount"] == 2, "geerbte Variablen gezählt, nicht gelistet")

    # Materialzielfenster
    try:
        materials.material_property("gibtNicht")
        check(False, "unbekanntes Materialziel wirft")
    except ops.OpsError as error:
        check(error.code == "unreal.material_property_unknown", "unbekanntes Materialziel wirft")
    check(materials.material_property("baseColor") == "MP_BASE_COLOR", "Materialziel zugeordnet")

    # Vorlagen und Weiterleitungen
    check(set(blueprints.TEMPLATES) == {"gameMode", "playerCharacter", "npc", "questObjective",
                                       "questComponent"}, "fünf Baupläne vorhanden")
    missing = [command for command in blueprints.SUB_OPERATIONS if command not in ops.OPERATIONS]
    check(not missing, f"alle Blueprint-Unterbefehle registriert (fehlen: {missing})")
    registered = sorted(ops.OPERATIONS)
    print("registrierte Kommandos: " + ", ".join(registered))
    for expected in ("status", "view.capture", "level.create", "asset.import", "material.assign",
                     "actor.verify", "camera.frame", "light.create", "sky.read", "class.search",
                     "blueprint.pin.connect", "blueprint.selftestecho"):
        check(expected in ops.OPERATIONS, f"Kommando {expected} ist registriert")

    print(f"\nSelbsttest: {_checks} Prüfungen, {len(_failures)} fehlerhaft.")
    if _failures:
        for failure in _failures:
            print(f"  - {failure}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
