# SPDX-License-Identifier: MIT
"""Operationsregister des Missum-Kanals für den Unreal Editor.

Der Kanal in `unreal_bootstrap.py` ruft ausschließlich `handle` und `tick` aus diesem
Modul auf, und zwar nur aus dem über `unreal.register_ticker_callback` angemeldeten
Pump heraus. Damit geschehen alle Editorzugriffe auf dem Spielthread; es gibt keinen
zweiten Thread, der Unreal-Objekte berührt.

Jede Operation gibt ein Wörterbuch zurück oder löst `OpsError` aus. Der Kanal übersetzt
`OpsError` in die Antwort `{ok: false, error: {code, message}}`, die der Missum-Adapter
bereits versteht. Die Form der Antworten entspricht dem Vertrag, den das frühere
eigene C++-Editor-Plugin geliefert hat.
"""

import hashlib
import json
import os
import time

import unreal


PROTOCOL = 1
RUNTIME_VERSION = "0.2.0"
CHANNEL = "python"

# Obergrenzen, damit eine einzelne Antwort nie unbegrenzt groß wird.
MAX_ACTORS_REPORTED = 512
MAX_ITEMS_REPORTED = 400

# Änderungen brauchen eine Vorgangskennung und werdenquittiert, damit ein wiederholter
# Aufruf derselben Kennung nicht noch einmal schreibt.
MUTATING_COMMANDS = (
    "blueprint.", "camera.", "view.capture", "level.", "asset.", "material.", "actor.",
    "light.", "sky.", "class.", "python.",
)
# camera.frame moves the camera and therefore counts as a change.
READONLY_COMMANDS = ("read", "status", "list", "probe", "search", "verify")

OPERATIONS = {}
CAPABILITY_LIMITS = {}


class OpsError(Exception):
    """Fachliche Ablehnung eines Auftrags; wird zu einer Fehlerantwort mit Code."""

    def __init__(self, code, message):
        super().__init__(message)
        self.code = code
        self.message = message


def operation(name, description=""):
    """Registriert eine Operation unter dem Kommandonamen des Adaptervertrags."""

    def decorator(function):
        OPERATIONS[name] = function
        function.operation_description = description
        return function

    return decorator


def limit(name, message):
    """Hält eine Fähigkeitsgrenze fest, die der Status offen ausweist."""
    CAPABILITY_LIMITS[name] = message


def handle(command, arguments, context):
    """Führt ein Kommando aus und erzwingt die Vorgangskennung für Änderungen."""
    if not isinstance(command, str) or not command:
        raise OpsError("unreal.command_missing", "Es fehlt ein Kommandoname.")
    function = OPERATIONS.get(command)
    if function is None:
        if command in KNOWN_UNIMPLEMENTED:
            raise OpsError("unreal.capability_unavailable", KNOWN_UNIMPLEMENTED[command])
        raise OpsError("unreal.command_unknown",
                       f"Dieser Unreal-Kanal kennt das Kommando {command} nicht. "
                       f"Verfügbare Kommandos: {', '.join(sorted(OPERATIONS))}.")
    if is_mutating(command):
        operation_id = text(arguments, "operationId")
        if not operation_id:
            raise OpsError("unreal.operation_id_required",
                           "Änderungen benötigen eine operationId.")
        fingerprint = command + ":" + json.dumps(arguments, sort_keys=True, ensure_ascii=False, default=str)
        digest = hashlib.sha256(fingerprint.encode("utf-8")).hexdigest()
        ledger = context.setdefault("ledger", {})
        if operation_id in ledger:
            previous = ledger[operation_id]
            if previous["digest"] != digest:
                raise OpsError("unreal.operation_conflict",
                               "operationId wurde bereits mit anderen Argumenten verwendet.")
            return previous["result"]
        result = function(arguments, context)
        if len(ledger) >= 4096:
            ledger.pop(next(iter(ledger)), None)
        ledger[operation_id] = {"digest": digest, "result": result}
        return result
    return function(arguments, context)


def tick(context):
    """Rückt laufende Mehrbild-Aufträge weiter; wird in jeder Pump-Runde gerufen."""
    handler = TICK_HANDLERS.get("advance")
    if handler is not None:
        handler(context)


TICK_HANDLERS = {}


def is_mutating(command):
    if not command.startswith(MUTATING_COMMANDS):
        return False
    tail = command.split(".")[-1]
    return not any(marker in tail for marker in READONLY_COMMANDS)


# ---------------------------------------------------------------------------
# Argument- und Objektzugriffe
# ---------------------------------------------------------------------------

def text(arguments, name, default=""):
    if not isinstance(arguments, dict):
        return default
    value = arguments.get(name)
    if isinstance(value, str):
        return value
    if value is None:
        return default
    return str(value)


def number(arguments, name, default=None):
    if not isinstance(arguments, dict):
        return default
    value = arguments.get(name)
    if isinstance(value, bool):
        return default
    if isinstance(value, (int, float)):
        return float(value)
    if isinstance(value, str):
        try:
            return float(value)
        except ValueError:
            return default
    return default


def flag(arguments, name, default=False):
    if not isinstance(arguments, dict):
        return default
    value = arguments.get(name)
    if isinstance(value, bool):
        return value
    if isinstance(value, str):
        return value.strip().lower() in ("1", "true", "ja", "on")
    if value is None:
        return default
    return bool(value)


def mapping(arguments, name):
    if not isinstance(arguments, dict):
        return {}
    value = arguments.get(name)
    return value if isinstance(value, dict) else {}


def attribute(obj, snake_name, *fallback_names):
    """Liest eine Editor-Eigenschaft, zuerst über Reflection, dann als Attribut."""
    for name in (snake_name,) + fallback_names:
        try:
            return obj.get_editor_property(name)
        except BaseException:
            pass
        try:
            return getattr(obj, name)
        except BaseException:
            pass
    return None


def call(obj, name, *arguments, **keyword):
    try:
        return getattr(obj, name)(*arguments, **keyword)
    except BaseException as error:
        raise OpsError("unreal.engine_call_failed",
                       f"{type(error).__name__} beim Aufruf von {name}: {error}")


def vector_dict(value):
    if value is None:
        return {}
    return {"x": float(value.x), "y": float(value.y), "z": float(value.z)}


def rotation_dict(value):
    if value is None:
        return {}
    return {"pitch": float(value.pitch), "yaw": float(value.yaw), "roll": float(value.roll)}


def scale_dict(value):
    if value is None:
        return {}
    return {"x": float(value.x), "y": float(value.y), "z": float(value.z)}


def class_path(obj):
    try:
        return obj.get_class().get_path_name()
    except BaseException:
        return ""


# ---------------------------------------------------------------------------
# Editorzugänge
# ---------------------------------------------------------------------------

def subsystem(class_name):
    try:
        return unreal.get_editor_subsystem(getattr(unreal, class_name))
    except BaseException:
        return None


def actor_subsystem():
    return subsystem("EditorActorSubsystem")


def level_subsystem():
    return subsystem("LevelEditorSubsystem")


def asset_subsystem():
    return subsystem("EditorAssetSubsystem")


def current_world():
    """Die gerade im Editor geöffnete Welt, über das Schauspiellersystem ermittelt."""
    handler = actor_subsystem()
    if handler is not None:
        try:
            world = handler.get_world()
            if world is not None:
                return world
        except BaseException:
            pass
    level_handler = level_subsystem()
    if level_handler is not None:
        try:
            return level_handler.get_current_level().get_world()
        except BaseException:
            pass
    return None


def actors_in_world(world=None):
    world = world if world is not None else current_world()
    if world is None:
        return []
    handler = actor_subsystem()
    if handler is not None:
        try:
            return list(handler.get_all_level_actors())
        except BaseException:
            pass
    try:
        return list(world.get_levels()[0].get_actors())
    except BaseException:
        return []


def valid_asset_path(path):
    return isinstance(path, str) and path.startswith("/Game/") and ".." not in path \
        and "\\" not in path and "//" not in path


def require_asset_path(arguments, name="assetPath"):
    path = text(arguments, name)
    if not valid_asset_path(path):
        raise OpsError("unreal.invalid_asset",
                       "assetPath muss ein gültiger /Game/-Assetpfad ohne .. und ohne Rückstrich sein.")
    return path


def load_asset(path):
    try:
        return unreal.load_asset(path)
    except BaseException:
        return None


def content_directory():
    try:
        return os.path.normpath(str(unreal.Paths.project_content_dir()))
    except BaseException:
        return ""


def package_file(asset_path, extension=".umap"):
    """Wandelt einen /Game/-Pfad in den zugehörigen Dateipfad auf der Platte um.

    `/Game/Maps/Main` liegt im Inhaltverzeichnis des Projekts unter
    `Maps/Main<Erweiterung>`. Andere Mount-Punkte werden abgelehnt, weil Missum
    ausschließlich innerhalb des Projektinhalts arbeitet.
    """
    if not asset_path.startswith("/Game/"):
        raise OpsError("unreal.invalid_asset", "Nur /Game/-Pfade lassen sich in Dateipfade umrechnen.")
    root = content_directory()
    if not root:
        raise OpsError("unreal.content_directory_unknown", "Das Inhaltverzeichnis des Projekts ist nicht bestimmbar.")
    relative = asset_path[len("/Game/"):].replace("/", os.sep)
    return os.path.normpath(os.path.join(root, relative + extension))


def file_digest(path):
    try:
        with open(path, "rb") as stream:
            return hashlib.md5(stream.read()).hexdigest()
    except OSError:
        return ""


# ---------------------------------------------------------------------------
# Kernoperationen des Kanals
# ---------------------------------------------------------------------------

@operation("status", "Verbindung, Welt, Schauspieler, Kameras und Fähigkeiten.")
def op_status(arguments, context):
    world = current_world()
    actors = actors_in_world(world)
    reported = []
    cameras = []
    for actor in actors[:MAX_ACTORS_REPORTED + 1]:
        try:
            name = actor.get_name()
            path = class_path(actor)
        except BaseException:
            continue
        reported.append({"name": name, "classPath": path})
        try:
            if isinstance(actor, unreal.CameraActor):
                component = attribute(actor, "camera_component")
                cameras.append({
                    "name": name,
                    "label": actor.get_actor_label() if hasattr(actor, "get_actor_label") else name,
                    "path": actor.get_path_name(),
                    "position": vector_dict(actor.get_actor_location()),
                    "rotation": rotation_dict(actor.get_actor_rotation()),
                    "fieldOfView": float(component.field_of_view) if component is not None else 90.0,
                })
        except BaseException:
            continue
    return {
        "instanceId": context["instanceId"],
        "processId": context["processId"],
        "projectPath": context["projectPath"],
        "engineVersion": context["engineVersion"],
        "world": world.get_path_name() if world is not None else "",
        "worldActors": reported[:MAX_ACTORS_REPORTED],
        "actorCount": len(actors),
        "actorsTruncated": len(actors) > len(reported[:MAX_ACTORS_REPORTED]),
        "cameras": cameras,
        "capabilities": sorted(OPERATIONS),
        "channel": {
            "type": CHANNEL,
            "protocol": PROTOCOL,
            "runtimeVersion": RUNTIME_VERSION,
            "pump": context.get("pump", ""),
            "transport": "loopback-tcp",
        },
        "capabilityLimits": dict(CAPABILITY_LIMITS),
        "python": context.get("python", ""),
    }


@operation("channel.probe", "Meldet die über Python tatsächlich erreichbaren Editorzugänge.")
def op_probe(arguments, context):
    found = {}
    for class_name in (
        "LevelEditorSubsystem", "EditorActorSubsystem", "EditorAssetSubsystem",
        "BlueprintEditorLibrary", "AssetToolsHelpers", "AssetImportTask", "BlueprintFactory",
        "MaterialEditingLibrary", "MaterialInstanceConstant", "MaterialFactoryNew",
        "CameraActor", "DirectionalLight", "SkyLight", "ExponentialHeightFog",
        "PostProcessVolume", "StaticMesh", "Texture2D", "DataAsset", "UserDefinedStruct",
        "SceneCaptureComponent2D", "WorldPartitionSubsystem",
    ):
        found[class_name] = hasattr(unreal, class_name)
    return {
        "classes": found,
        "channel": {"type": CHANNEL, "protocol": PROTOCOL, "runtimeVersion": RUNTIME_VERSION,
                    "pump": context.get("pump", "")},
        "operations": sorted(OPERATIONS),
        "capabilityLimits": dict(CAPABILITY_LIMITS),
    }


@operation("operation.status", "Stand eines Auftrags, insbesondere einer laufenden Aufnahme.")
def op_operation_status(arguments, context):
    operation_id = text(arguments, "operationId")
    store = context.setdefault("operations", {})
    if operation_id in store:
        record = store[operation_id]
        # Interne Helferfelder (Objektverweise, Zeitpunkte) bleiben aus der Antwort heraus.
        return {key: value for key, value in record.items() if not key.startswith("_")}
    raise OpsError("unreal.operation_missing", "Auftrag wurde in dieser Editor-Sitzung nicht gefunden.")


# ---------------------------------------------------------------------------
# Karten: aufzählen, erzeugen, öffnen, speichern, lesen
# ---------------------------------------------------------------------------

MAP_EXTENSION = ".umap"
DEFAULT_MAP_FOLDER = "/Game/Maps"
DEFAULT_TEMPLATE = "/Engine/Maps/Templates/Template_Default"
EXTERNAL_ACTORS_DIRECTORY = "_ExternalActors_"


def call_any(obj, name, variants, error_code="unreal.signature_unknown"):
    """Probiert mehrere Aufrufformen durch, weil sich Signaturen je Engineversion ändern.

    Ein TypeError gilt als Formproblem und wird mit der nächsten Form versucht. Jede andere
    Ausnahme ist eine echte Antwort der Engine und wird nicht wiederholt.
    """
    failures = []
    for attempt in variants:
        try:
            return getattr(obj, name)(*attempt), attempt
        except TypeError as error:
            failures.append(f"{attempt}: {error}")
        except BaseException as error:
            raise OpsError(error_code, f"{type(error).__name__} bei {name}: {error}")
    raise OpsError(error_code,
                   f"{name} wurde in keiner bekannten Form akzeptiert. previously: " + " | ".join(failures))


def map_folder_for(asset_folder):
    """Begrenzt Kartenerzeugung auf /Game/ und normalisiert den Ordner."""
    folder = (asset_folder or DEFAULT_MAP_FOLDER).strip()
    if not folder.startswith("/Game/"):
        folder = "/Game/" + folder.lstrip("/")
    if ".." in folder or "\\" in folder:
        raise OpsError("unreal.invalid_asset_path", "Kartenordner müssen unter /Game/ liegen.")
    return folder.rstrip("/") or "/Game"


def split_asset_path(asset_path):
    """`/Game/Maps/Main` -> Ordner `/Game/Maps`, Name `Main`."""
    trimmed = asset_path.rstrip("/")
    if trimmed.count("/") < 2:
        raise OpsError("unreal.invalid_asset_path", "Eine Kartenangabe braucht Ordner und Name.")
    folder, _, name = trimmed.rpartition("/")
    return folder, name


def list_map_files(limit=MAX_ITEMS_REPORTED):
    """Alle .umap-Dateien im Projektinhalt, mit Dateigröße und Änderungszeit."""
    root = content_directory()
    found = []
    if not root or not os.path.isdir(root):
        return found
    for directory, subdirectories, filenames in os.walk(root):
        subdirectories[:] = [item for item in subdirectories
                             if not item.startswith(".") and item != EXTERNAL_ACTORS_DIRECTORY]
        for filename in filenames:
            if not filename.lower().endswith(MAP_EXTENSION):
                continue
            full = os.path.join(directory, filename)
            try:
                info = os.stat(full)
            except OSError:
                continue
            relative = os.path.relpath(full, root)
            asset = "/Game/" + relative.replace(os.sep, "/")[: -len(MAP_EXTENSION)]
            folder = os.path.dirname(full)
            found.append({
                "assetPath": asset,
                "name": os.path.splitext(filename)[0],
                "file": os.path.normpath(full),
                "bytes": int(info.st_size),
                "modifiedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(info.st_mtime)),
                "worldPartition": os.path.isdir(os.path.join(os.path.dirname(folder),
                                                             EXTERNAL_ACTORS_DIRECTORY,
                                                             os.path.basename(folder)))
                or os.path.isdir(os.path.join(folder, EXTERNAL_ACTORS_DIRECTORY)),
            })
            if len(found) >= limit:
                return found
    found.sort(key=lambda entry: entry["assetPath"])
    return found


def map_file_exists(asset_path):
    try:
        return os.path.isfile(package_file(asset_path))
    except OpsError:
        return False


def project_uses_world_partition():
    """Erkennt Welt-Partition dateibasiert am Vorkommen von _ExternalActors_-Ordnern."""
    root = content_directory()
    if not root or not os.path.isdir(root):
        return False
    for directory, subdirectories, _ in os.walk(root):
        if EXTERNAL_ACTORS_DIRECTORY in subdirectories:
            return True
        depth = os.path.relpath(directory, root).count(os.sep)
        if depth >= 3:
            subdirectories[:] = []
    return False


@operation("level.list", "Karten im Projekt: Assetpfad, Größe, Änderungszeit, Welt-Partition.")
def op_level_list(arguments, context):
    limit = int(number(arguments, "limit", MAX_ITEMS_REPORTED))
    entries = list_map_files(limit=max(1, min(limit, MAX_ITEMS_REPORTED)))
    current = current_world()
    return {
        "levels": entries,
        "count": len(entries),
        "openLevel": current.get_path_name() if current is not None else "",
        "contentDirectory": content_directory(),
    }


@operation("level.create", "Erzeugt eine neue Karte unter /Game/ mit optionaler Vorlage.")
def op_level_create(arguments, context):
    name = text(arguments, "levelName") or text(arguments, "name")
    if not name:
        raise OpsError("unreal.level_name_required", "Ein Kartenname fehlt.")
    if not (name[0].isalpha() or name[0] == "_"):
        raise OpsError("unreal.level_name_invalid", "Der Kartenname muss mit einem Buchstaben beginnen.")
    if any(character not in "._-" and not character.isalnum() for character in name):
        raise OpsError("unreal.level_name_invalid",
                       "Der Kartenname darf nur Buchstaben, Ziffern, Unterstriche, Punkte und Bindestriche enthalten.")
    folder = map_folder_for(text(arguments, "folder", DEFAULT_MAP_FOLDER))
    asset_path = f"{folder}/{name}"
    handler = level_subsystem()
    if handler is None:
        raise OpsError("unreal.level_subsystem_missing", "Der Editor stellt kein LevelEditorSubsystem zur Verfügung.")
    if map_file_exists(asset_path) and not flag(arguments, "overwrite", False):
        raise OpsError("unreal.level_exists", f"Die Karte {asset_path} existiert bereits.")

    # In UE 5.8 expects new_level the full asset path, then a flag for world partition;
    # new_level_from_template takes asset path and template path. Both save the new map itself.
    partitioned = flag(arguments, "worldPartition", project_uses_world_partition())
    settled = settle_unsaved_changes(arguments)
    template = text(arguments, "template")
    if template:
        created, used = call_any(handler, "new_level_from_template", [(asset_path, template)])
    else:
        created, used = call_any(handler, "new_level", [(asset_path, partitioned), (asset_path,)])
    if not created:
        raise OpsError("unreal.level_create_failed",
                       f"Die Karte {asset_path} konnte nicht erstellt werden (Form {used}).")

    saved, save_form = True, "nicht gespeichert"
    if flag(arguments, "save", True):
        saved, save_form = _save_current(handler)
    file_path = package_file(asset_path)
    return {
        "unsavedChanges": settled,
        "assetPath": asset_path,
        "file": os.path.normpath(file_path),
        "existsOnDisk": os.path.isfile(file_path),
        "bytes": os.path.getsize(file_path) if os.path.isfile(file_path) else 0,
        "saved": bool(saved),
        "saveForm": save_form,
        "createForm": str(used),
        "template": template or "leere Standardkarte",
        "worldPartition": bool(partitioned),
        "world": (current_world().get_path_name() if current_world() is not None else ""),
        "operationId": text(arguments, "operationId"),
    }


def _save_current(handler):
    """Speichert die aktuelle Karte. In UE 5.8 nimmt save_current_level keine Argumente."""
    return call_any(handler, "save_current_level", [()])


@operation("level.open", "Wechselt die Editorwelt auf eine vorhandene Karte.")
def op_level_open(arguments, context):
    asset_path = require_asset_path(arguments, "assetPath")
    if not asset_path.endswith(MAP_EXTENSION):
        pass  # The editor expects the long package name without extension.
    handler = level_subsystem()
    if handler is None:
        raise OpsError("unreal.level_subsystem_missing", "Der Editor stellt kein LevelEditorSubsystem zur Verfügung.")
    if not map_file_exists(asset_path):
        raise OpsError("unreal.level_missing", f"Die Karte {asset_path} existiert nicht im Projekt.")
    settled = settle_unsaved_changes(arguments)
    loaded, used = call_any(handler, "load_level", [(asset_path,)])
    if not loaded:
        raise OpsError("unreal.level_open_failed", f"Die Karte {asset_path} konnte nicht geöffnet werden (Form {used}).")
    world = current_world()
    reported = world.get_path_name() if world is not None else ""
    return {
        "assetPath": asset_path,
        "unsavedChanges": settled,
        "opened": bool(loaded),
        "world": reported,
        "matches": reported.startswith(asset_path),
        "openForm": str(used),
        "actorCount": len(actors_in_world(world)),
        "operationId": text(arguments, "operationId"),
    }


@operation("level.save", "Speichert die aktuelle Karte, optional benannt, und prüft die Datei.")
def op_level_save(arguments, context):
    handler = level_subsystem()
    if handler is None:
        raise OpsError("unreal.level_subsystem_missing", "Der Editor stellt kein LevelEditorSubsystem zur Verfügung.")
    world = current_world()
    if world is None:
        raise OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")
    before = world.get_path_name()
    asset_path = text(arguments, "assetPath")
    dirty = _world_dirty(world)
    saved, used = _save_current(handler)
    save_as_note = "" if not asset_path else (
        "Diese Engineversion stellt über Python kein Speichern unter einer neuen "
        "Kartenkennung zur Verfügung; gespeichert wurde unter der bestehenden.")
    if not saved:
        return {
            "saved": False,
            "world": before,
            "assetPath": asset_path,
            "saveForm": str(used),
            "dirtyBefore": dirty,
            "error": {"code": "unreal.level_save_failed",
                      "message": "Der Editor hat das Speichern abgelehnt."},
        }
    target = asset_path or before.rpartition(".")[0]
    file_path = ""
    try:
        file_path = package_file(target)
    except OpsError:
        file_path = ""
    return {
        "saved": True,
        "world": before,
        "assetPath": target,
        "file": os.path.normpath(file_path) if file_path else "",
        "existsOnDisk": bool(file_path) and os.path.isfile(file_path),
        "bytes": os.path.getsize(file_path) if file_path and os.path.isfile(file_path) else 0,
        "saveForm": str(used),
        "dirtyBefore": dirty,
        "saveAsNote": save_as_note,
        "allDirtyLevelsSaved": _save_all_dirty(handler),
        "operationId": text(arguments, "operationId"),
    }


def dirty_package_names():
    """Namen aller ungespeicherten Karten- und Inhaltspakete (Package kennt kein is_dirty in Python)."""
    names = set()
    for getter in ("get_dirty_map_packages", "get_dirty_content_packages"):
        try:
            names.update(package.get_name() for package in getattr(unreal.EditorLoadingAndSavingUtils, getter)())
        except Exception:
            pass
    return names


def settle_unsaved_changes(arguments):
    """Speichert ungespeicherte Änderungen ohne Dialog, bevor die Editorwelt wechselt.

    Sonst öffnet Unreal den modalen Dialog "Inhalt speichern" und blockiert den Python-Kanal,
    bis jemand klickt. Benannte Karten und Inhalte werden gespeichert; eine unbenannte Karte
    unter /Temp wird unter /Game/Maps/Missum_Unbenannt_<Zeit> gesichert. Nichts geht verloren.
    """
    report = {"saved": False, "savedAs": "", "dirtyBefore": sorted(dirty_package_names())[:50]}
    if not report["dirtyBefore"]:
        return report
    try:
        report["saved"] = bool(unreal.EditorLoadingAndSavingUtils.save_dirty_packages(True, True))
    except Exception as error:
        report["saveError"] = str(error)
    world = current_world()
    if world is not None:
        package_name = world.get_outermost().get_name()
        if package_name.startswith("/Temp/") and package_name in dirty_package_names():
            target = DEFAULT_MAP_FOLDER.rstrip("/") + "/Missum_Unbenannt_" + time.strftime("%Y%m%d_%H%M%S")
            try:
                if unreal.EditorLoadingAndSavingUtils.save_map(world, target):
                    report["savedAs"] = target
            except Exception as error:
                report["saveAsError"] = str(error)
    report["dirtyAfter"] = sorted(dirty_package_names())[:50]
    return report


def _save_all_dirty(handler):
    try:
        value, _ = call_any(handler, "save_all_dirty_levels", [()])
        return bool(value)
    except OpsError:
        return None


def _world_dirty(world):
    """Schmutzstatus so genau wie möglich; die Engine gibt ihn nicht überall frei."""
    try:
        package = world.get_outermost()
        if package is not None and hasattr(package, "is_dirty"):
            return bool(package.is_dirty())
    except BaseException:
        pass
    return "nicht bestimmbar"


@operation("level.read", "Schauspieler je Klasse und beschriftete Schauspieler mit Ort und Ausdehnung.")
def op_level_read(arguments, context):
    world = current_world()
    if world is None:
        raise OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")
    actors = actors_in_world(world)
    limit = int(number(arguments, "limit", MAX_ITEMS_REPORTED))
    limit = max(1, min(limit, MAX_ITEMS_REPORTED))
    wanted = text(arguments, "classFilter").lower()
    labelled_only = flag(arguments, "labelledOnly", False)

    by_class = {}
    detailed = []
    for actor in actors:
        try:
            path = class_path(actor)
        except BaseException:
            continue
        if wanted and wanted not in path.lower():
            continue
        by_class[path] = by_class.get(path, 0) + 1
        if labelled_only:
            continue
        if len(detailed) >= limit:
            continue
        try:
            origin, extent = actor.get_actor_bounds(False)
        except BaseException:
            origin, extent = None, None
        try:
            label = actor.get_actor_label()
        except BaseException:
            label = ""
        try:
            hidden = bool(actor.is_hidden_ed())
        except BaseException:
            hidden = None
        detailed.append({
            "name": actor.get_name(),
            "label": label,
            "classPath": path,
            "location": vector_dict(actor.get_actor_location()) if origin is None else vector_dict(origin),
            "extent": vector_dict(extent),
            "hiddenInEditor": hidden,
        })
    return {
        "world": world.get_path_name(),
        "actorCount": len(actors),
        "byClass": dict(sorted(by_class.items(), key=lambda item: (-item[1], item[0]))),
        "actors": detailed,
        "truncated": len(actors) > len(detailed) or (labelled_only and len(detailed) == 0 and len(actors) > 0),
        "limit": limit,
    }


# ---------------------------------------------------------------------------
# Assetkatalog, Assetdiagnose und Import, auch aus Blender
# ---------------------------------------------------------------------------

IMPORT_EXTENSIONS = (".fbx", ".obj", ".gltf", ".glb", ".usda", ".abc")


def set_if_present(obj, name, value):
    """Setzt eine Eigenschaft nur, wenn diese Engineversion sie kennt; meldet das Ergebnis."""
    try:
        obj.set_editor_property(name, value)
        return True
    except BaseException:
        try:
            setattr(obj, name, value)
            return True
        except BaseException:
            return False


def asset_data_fields(path):
    try:
        data = unreal.EditorAssetLibrary.find_asset_data(path)
        if data is None or not data.is_valid():
            return {}
        return {
            "assetClassPath": str(data.asset_class_path),
            "assetName": str(data.asset_name),
            "packagePath": str(data.package_path),
        }
    except BaseException:
        return {}


def write_ini_values(path, section, values):
    """Setzt Schlüssel eines ini-Abschnitts und schreibt atomar ohne Bytezeichenmarkierung zurück."""
    lines = []
    if os.path.isfile(path):
        with open(path, "r", encoding="utf-8-sig", errors="replace") as stream:
            lines = stream.read().splitlines()
    start = -1
    for index, line in enumerate(lines):
        if line.strip().lower() == f"[{section.lower()}]":
            start = index
            break
    if start < 0:
        lines.append(f"[{section}]")
        start = len(lines) - 1
    end = start + 1
    while end < len(lines) and not lines[end].lstrip().startswith("["):
        end += 1
    for key, value in values.items():
        replacement = f"{key}={value}"
        replaced = False
        for index in range(start + 1, end):
            if lines[index].lstrip().lower().startswith(f"{key.lower()}="):
                lines[index] = replacement
                replaced = True
                break
        if not replaced:
            lines.insert(start + 1, replacement)
            end += 1
    temporary = path + ".missum.tmp"
    with open(temporary, "w", encoding="utf-8", newline="\r\n") as stream:
        stream.write("\n".join(lines) + "\n")
    os.replace(temporary, path)
    try:
        with open(path, "r", encoding="utf-8-sig", errors="replace") as stream:
            content = stream.read()
    except OSError:
        return False
    return all(f"{key}={value}" in content for key, value in values.items())


def save_asset(asset_path, asset_object=None):
    """Speichert ein Asset.

    Nach einer Kompilierung kann die Engine die Pfadabfrage auf ein nicht mehr
    registriertes Objekt führen; `EditorAssetLibrary.save_asset` stürzt dann im Kern ab
    (Assertion Object->IsValidLowLevel()). Deshalb wird zuerst das bereits bekannte Objekt
    gespeichert, und nur wenn gar keine Referenz vorliegt, weicht die Funktion auf den Pfad aus.
    """
    if asset_object is not None:
        try:
            return bool(unreal.EditorAssetLibrary.save_loaded_asset(asset_object))
        except BaseException:
            pass
    try:
        return bool(unreal.EditorAssetLibrary.save_asset(asset_path))
    except BaseException:
        return False


def asset_exists(asset_path):
    try:
        return bool(unreal.EditorAssetLibrary.does_asset_exist(asset_path))
    except BaseException:
        return map_file_exists(asset_path)


def require_new_asset_path(arguments, default_folder):
    """Gewährt Ordner, Name und Zielkennung für ein neu zu erzeugendes Asset."""
    folder = map_folder_for(text(arguments, "folder", default_folder))
    name = text(arguments, "assetName")
    if not name:
        given = text(arguments, "assetPath")
        if given:
            valid_asset_path(given)
            folder, _, name = given.rpartition("/")
            folder = map_folder_for(folder or default_folder)
    if not name:
        raise OpsError("unreal.asset_name_required", "Ein Assetname fehlt.")
    if not (name[0].isalpha() or name[0] == "_"):
        raise OpsError("unreal.asset_name_invalid", "Der Assetname muss mit einem Buchstaben beginnen.")
    return folder, name, f"{folder}/{name}"


@operation("asset.list", "Inhalte eines /Game/-Ordners mit Klasse und Dateigröße.")
def op_asset_list(arguments, context):
    folder = map_folder_for(text(arguments, "folder", "/Game"))
    recursive = flag(arguments, "recursive", True)
    handler = unreal.EditorAssetLibrary
    listed, used = call_any(handler, "list_assets",
                            [(folder, recursive, False), (folder, recursive), (folder,)])
    wanted = text(arguments, "classFilter").lower()
    limit = max(1, min(int(number(arguments, "limit", MAX_ITEMS_REPORTED)), MAX_ITEMS_REPORTED))
    entries = []
    for path in listed or []:
        if not isinstance(path, str):
            path = str(path)
        try:
            info = os.stat(package_file(path, ".uasset"))
            size = int(info.st_size)
            modified = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(info.st_mtime))
        except (OSError, OpsError):
            size, modified = 0, ""
        fields = asset_data_fields(path)
        class_path = fields.get("assetClassPath", "")
        if wanted and wanted not in class_path.lower():
            continue
        entries.append({"assetPath": path, "classPath": class_path, "bytes": size,
                        "modifiedUtc": modified})
        if len(entries) >= limit:
            break
    return {
        "folder": folder,
        "recursive": recursive,
        "assets": entries,
        "count": len(entries),
        "listedByEngine": len(listed or []),
        "listForm": str(used),
    }


def slot_value(entry, name):
    try:
        return entry.get_editor_property(name)
    except BaseException:
        try:
            return getattr(entry, name)
        except BaseException:
            return None


def soft_path_text(value):
    """Liest aus einer Weichreferenz die Assetkennung, nicht die Structurdarstellung."""
    if value is None:
        return ""
    try:
        path_name = value.get_path_name()
        if path_name:
            return str(path_name)
    except BaseException:
        pass
    for name in ("asset_path", "assetPath"):
        try:
            path = getattr(value, name)
            if path:
                return str(path)
        except BaseException:
            pass
    try:
        text = str(value)
    except BaseException:
        return ""
    if text in ("None", "") or "{" in text:
        return ""
    return text


def static_mesh_report(mesh):
    """Materialfelder eines Netzes: zuerst die gespeicherten Weichreferenzen, dann die Abfrage."""
    slots = []
    try:
        section_count = int(mesh.get_num_sections(0))
    except BaseException:
        section_count = 0
    slots_struct = []
    for property_name in ("material_slots", "static_materials"):
        try:
            listed = list(mesh.get_editor_property(property_name))
        except BaseException:
            continue
        if listed:
            slots_struct = listed
            break
    for index in range(max(section_count, len(slots_struct))):
        entry = {"slot": index}
        if index < len(slots_struct):
            slot_entry = slots_struct[index]
            name = slot_value(slot_entry, "material_slot_name")
            if name:
                entry["name"] = name
            material = soft_path_text(slot_value(slot_entry, "material_interface"))
            if material:
                entry["material"] = material
        if not entry.get("material"):
            for attempt in ((0, index), (index,)):
                try:
                    material = mesh.get_material(*attempt)
                    if material is not None:
                        entry["material"] = material.get_path_name()
                        break
                except BaseException:
                    continue
        slots.append(entry)
    numbers = {}
    for name, call in (
        ("lodCount", lambda: mesh.get_num_lods()),
        ("triangleCount", lambda: mesh.get_num_triangles(0)),
        ("sectionCount", lambda: mesh.get_num_sections(0)),
        ("naniteTriangles", lambda: mesh.get_num_nanite_triangles()),
        ("naniteVertices", lambda: mesh.get_num_nanite_vertices()),
    ):
        value = safe_number(call)
        if value is not None:
            numbers[name] = value
    bounds = None
    try:
        bounds = mesh.get_bounds()
    except BaseException:
        pass
    return {
        "materialSlots": slots,
        "numbers": numbers,
        "bounds": {
            "origin": vector_dict(attribute(bounds, "origin")),
            "boxExtent": vector_dict(attribute(bounds, "box_extent")),
        } if bounds is not None else {},
    }


def safe_number(function):
    try:
        return int(function())
    except BaseException:
        return None


def component_report(component):
    report = {"name": component.get_name(), "classPath": class_path(component)}
    try:
        materials = component.get_materials()
        report["materials"] = [
            {"slot": index, "material": item.get_path_name() if item is not None else None}
            for index, item in enumerate(materials)
        ]
    except BaseException:
        pass
    try:
        report["slotNames"] = [str(item) for item in component.get_material_slot_names()]
    except BaseException:
        pass
    try:
        report["numMaterials"] = int(component.get_num_materials())
    except BaseException:
        pass
    return report


@operation("asset.read", "Klassendiagnose eines Assets: Netzaufbau, Materialfelder, Texturgröße.")
def op_asset_read(arguments, context):
    asset_path = require_asset_path(arguments)
    asset = load_asset(asset_path)
    if asset is None:
        raise OpsError("unreal.asset_missing", f"Das Asset {asset_path} wurde nicht gefunden oder lädt nicht.")
    report = {
        "assetPath": asset_path,
        "classPath": class_path(asset),
        "assetClassName": asset.get_class().get_name(),
    }
    try:
        file_path = package_file(asset_path, ".uasset")
        report["file"] = os.path.normpath(file_path)
        report["bytes"] = os.path.getsize(file_path) if os.path.isfile(file_path) else 0
    except OpsError:
        pass
    if isinstance(asset, unreal.StaticMesh):
        report["staticMesh"] = static_mesh_report(asset)
    if isinstance(asset, unreal.Texture2D):
        texture = {}
        for name, call in (("sizeX", asset.blueprint_get_size_x), ("sizeY", asset.blueprint_get_size_y)):
            value = safe_number(call)
            if value is not None:
                texture[name] = value
        for name in ("pixel_format", "compression_settings", "srgb", "mip_load_options"):
            value = attribute(asset, name)
            if value is not None and not callable(value):
                texture[name] = str(value)
        report["texture"] = texture
    if isinstance(asset, (unreal.MaterialInterface if hasattr(unreal, "MaterialInterface") else unreal.Material)):
        report["material"] = material_summary(asset)
    if isinstance(asset, unreal.Blueprint):
        report["blueprint"] = blueprint_summary(asset)

    # Wird ein Schauspieler mitgegeben, kommen die tatsächlich zugewiesenen Felder dazu.
    actor_name = text(arguments, "actorName")
    if actor_name:
        world = current_world()
        if world is None:
            raise OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")
        actor = find_actor(world, actor_name)
        report["actor"] = actor_report(actor)
        components = []
        try:
            for component in actor.get_components_by_class(unreal.ActorComponent):
                info = component_report(component)
                if "materials" in info or "slotNames" in info:
                    components.append(info)
        except BaseException:
            pass
        report["actorComponents"] = components
    report["operationId"] = text(arguments, "operationId")
    return report


@operation("asset.import", "Importiert ein Netz aus dem Missum-Arbeitsverzeichnis nach /Game/.")
def op_asset_import(arguments, context):
    source = text(arguments, "filePath")
    if not source:
        raise OpsError("unreal.import_file_required", "Eine Importdatei fehlt.")
    source = os.path.normpath(source)
    if not os.path.isfile(source):
        raise OpsError("unreal.import_file_missing", f"Die Importdatei existiert nicht: {source}")
    extension = os.path.splitext(source)[1].lower()
    if extension not in IMPORT_EXTENSIONS:
        raise OpsError("unreal.import_extension_unsupported",
                       "Unterstützte Formate: " + ", ".join(IMPORT_EXTENSIONS) + ".")
    workspace = context.get("workspace") or ""
    if workspace:
        try:
            inside = os.path.commonpath([os.path.realpath(source), os.path.realpath(workspace)]) == os.path.realpath(workspace)
        except ValueError:
            inside = False
        if not inside:
            raise OpsError("unreal.import_outside_workspace",
                           "Importdateien müssen innerhalb des Missum-Arbeitsverzeichnisses liegen.")
    folder = map_folder_for(text(arguments, "folder", "/Game/SourceAssets/Blender"))
    name = text(arguments, "assetName") or os.path.splitext(os.path.basename(source))[0]
    if not name or not (name[0].isalpha() or name[0] == "_"):
        raise OpsError("unreal.asset_name_invalid", "Der Assetname muss mit einem Buchstaben beginnen.")
    target = f"{folder}/{name}"

    task = unreal.AssetImportTask()
    set_if_present(task, "filename", source.replace("\\", "/"))
    set_if_present(task, "destination_path", folder)
    set_if_present(task, "destination_name", name)
    set_if_present(task, "automated", True)
    set_if_present(task, "replace_existing", flag(arguments, "replace", False))
    set_if_present(task, "save", flag(arguments, "save", True))
    set_if_present(task, "show_dialog", False)

    options_name = ""
    if extension == ".fbx" and hasattr(unreal, "FbxImportUI"):
        options = unreal.FbxImportUI()
        wanted = {
            "import_mesh": flag(arguments, "importMesh", True),
            "import_materials": flag(arguments, "importMaterials", True),
            "import_textures": flag(arguments, "importTextures", True),
            "import_skeletal_mesh": flag(arguments, "importSkeletal", False),
            "merge_vertices": flag(arguments, "mergeVertices", False),
            "build_nanite": flag(arguments, "nanite", True),
            "auto_compute_tangents": True,
            "vertex_color_import_option": flag(arguments, "vertexColors", True),
        }
        applied = [key for key, value in wanted.items() if set_if_present(options, key, value)]
        set_if_present(task, "options", options)
        options_name = "FbxImportUI(" + ", ".join(applied) + ")"

    tools = unreal.AssetToolsHelpers.get_asset_tools()
    tools.import_asset_tasks([task])

    imported = []
    try:
        imported = [str(item) for item in task.get_editor_property("imported_object_paths")]
    except BaseException:
        pass
    try:
        result = task.get_editor_property("result")
        log_lines = [str(line) for line in (result.get_editor_property("log") or [])][-12:]
        errors = [str(line) for line in (result.get_editor_property("errors") or [])][-12:]
    except BaseException:
        log_lines, errors = [], []

    created = []
    for path in imported:
        asset = load_asset(path)
        entry = {"assetPath": path, "classPath": class_path(asset) if asset is not None else ""}
        try:
            file_path = package_file(path, ".uasset")
            entry["bytes"] = os.path.getsize(file_path) if os.path.isfile(file_path) else 0
            entry["existsOnDisk"] = os.path.isfile(file_path)
        except OpsError:
            pass
        created.append(entry)
    if not imported:
        raise OpsError("unreal.import_failed",
                       "Der Import hat kein Asset erzeugt. " + " | ".join(errors or log_lines or ["keine Meldung"]))
    return {
        "source": source,
        "extension": extension,
        "targetFolder": folder,
        "assetName": name,
        "importOptions": options_name or "Automatik ohne Formatdialog",
        "importedAssets": created,
        "importedCount": len(created),
        "errors": errors,
        "log": log_lines,
        "operationId": text(arguments, "operationId"),
    }


# ---------------------------------------------------------------------------
# Zusammenfassungen für Material und Blueprint; von Diagnose und Werkzeugen geteilt
# ---------------------------------------------------------------------------

def _names(function, *call_arguments):
    try:
        return [str(item) for item in function(*call_arguments) or []]
    except BaseException:
        return []


def material_summary(material):
    """Parameter, Ausdrucksknoten und Flags eines Materials, so weit die Engine sie freigibt."""
    summary = {}
    for name in ("blend_mode", "shading_model", "two_sided", "domain", "editor_only",
                 "used_in_editor", "nansite_enabled", "used_with_translucency"):
        value = attribute(material, name)
        if value is not None and not callable(value):
            summary[name] = str(value)
    library = unreal.MaterialEditingLibrary
    summary["scalarParameters"] = _names(library.get_scalar_parameter_names, material)
    summary["vectorParameters"] = _names(library.get_vector_parameter_names, material)
    summary["textureParameters"] = _names(library.get_texture_parameter_names, material)
    summary["staticSwitchParameters"] = _names(library.get_static_switch_parameter_names, material)
    values = {}
    for parameter in summary["scalarParameters"]:
        try:
            values[parameter] = float(library.get_material_default_scalar_parameter_value(material, parameter))
        except BaseException:
            pass
    for parameter in summary["vectorParameters"]:
        try:
            values[parameter] = vector_dict(library.get_material_default_vector_parameter_value(material, parameter))
        except BaseException:
            pass
    if values:
        summary["defaultValues"] = values
    count = safe_number(lambda: library.get_num_material_expressions(material))
    if count is not None:
        summary["expressionCount"] = count
    expressions = []
    try:
        for expression in library.get_material_expressions(material) or []:
            expressions.append({
                "classPath": class_path(expression),
                "name": str(attribute(expression, "desc") or expression.get_name()),
            })
            if len(expressions) >= MAX_ITEMS_REPORTED:
                break
    except BaseException:
        pass
    if expressions:
        summary["expressions"] = expressions[:MAX_ITEMS_REPORTED]
    return summary


def blueprint_summary(blueprint):
    """Variablen, Graphen und Bauteile eines Blueprints; Elternklasse über die Bibliothek."""
    report = {}
    library = unreal.BlueprintEditorLibrary
    # Die Engine listet auch die geerbten Eigenschaften der Elternklasse. Die Antwort würde
    # sonst von hunderten Einträgen gefüllt; gehalten werden nur die eigenen Variablen.
    declared = _names(library.list_member_variable_names, blueprint, False)
    if not declared:
        declared = [item for item in _names(library.list_member_variable_names, blueprint)
                    if not item.startswith("/Script/")]
    own = declared
    report["variableNames"] = own[:MAX_ITEMS_REPORTED]
    report["inheritedVariableCount"] = max(0, len(declared) - len(own))
    report["graphNames"] = _names(library.list_graph_names, blueprint)
    parent = None
    try:
        parent = library.get_blueprint_parent_class(blueprint)
    except BaseException:
        pass
    report["parentClassPath"] = parent.get_path_name() if parent is not None else ""
    status = attribute(blueprint, "status")
    if status is not None and not callable(status):
        report["compileStatus"] = str(status)
    components, note = _blueprint_components(blueprint)
    if note:
        report["componentNote"] = note
    report["components"] = components[:MAX_ITEMS_REPORTED]
    return report


def _blueprint_components(blueprint):
    """Components incl. inherited ones (Character: capsule, mesh, movement).

    UE 5 exposes them through SubobjectDataSubsystem; simple_construction_script is protected
    in Python and only answered "Exception" in a live run.
    """
    errors = []
    subsystem_class = getattr(unreal, "SubobjectDataSubsystem", None)
    library = getattr(unreal, "SubobjectDataBlueprintFunctionLibrary", None)
    if subsystem_class is not None and library is not None:
        try:
            subsystem = unreal.get_engine_subsystem(subsystem_class)
            handles = subsystem.k2_gather_subobject_data_for_blueprint(blueprint) or []
            components = []
            for handle in handles:
                data = library.get_data(handle)
                target = library.get_object(data)
                if target is None or not isinstance(target, unreal.ActorComponent):
                    continue
                entry = {"name": str(library.get_variable_name(data)), "classPath": class_path(target)}
                try:
                    entry["inherited"] = bool(library.is_inherited_component(data))
                except BaseException:
                    pass
                components.append(entry)
            return components, None
        except BaseException as error:
            errors.append(f"SubobjectDataSubsystem: {type(error).__name__}: {error}")
    try:
        script = blueprint.get_editor_property("simple_construction_script")
        components = []
        if script is not None:
            for node in script.get_all_nodes():
                components.append({
                    "name": str(node.get_editor_property("variable_name")),
                    "classPath": class_path(node.get_editor_property("component_class")),
                })
        return components, None
    except BaseException as error:
        errors.append(f"simple_construction_script: {type(error).__name__}: {error}")
    return [], "Bauteile nicht lesbar: " + " | ".join(errors)[:400]


# ---------------------------------------------------------------------------
# Schauspieler setzen und lesen; die Grundlage jeder Platzierungskontrolle
# ---------------------------------------------------------------------------

BASIC_SHAPES = {
    "cube": "/Engine/BasicShapes/Cube.Cube",
    "sphere": "/Engine/BasicShapes/Sphere.Sphere",
    "plane": "/Engine/BasicShapes/Plane.Plane",
    "cylinder": "/Engine/BasicShapes/Cylinder.Cylinder",
    "cone": "/Engine/BasicShapes/Cone.Cone",
}


def vector_from(dictionary, key, default=0.0):
    source = mapping(dictionary, key)
    value = number(source, "x", default)
    return (float(value), float(number(source, "y", default)), float(number(source, "z", default)))


def angles_from(dictionary, key, default=0.0):
    source = mapping(dictionary, key)
    return (float(number(source, "pitch", default)), float(number(source, "yaw", default)),
            float(number(source, "roll", default)))


def make_vector(values):
    if isinstance(values, dict):
        values = (values.get("x", 0.0), values.get("y", 0.0), values.get("z", 0.0))
    return unreal.Vector(float(values[0]), float(values[1]), float(values[2]))


def make_rotator(values):
    """Baut eine Drehung aus (pitch, yaw, roll) oder einem Wörterbuch mit diesen Schlüsseln.

    Achtung: Epics Python-Konstruktor heißt Rotator(roll, pitch, yaw). Positionsargumente in
    der Reihenfolge pitch, yaw, roll legen den Nickwinkel als Rollen ab; deshalb nur Schlüsselwörter.
    """
    if isinstance(values, dict):
        values = (values.get("pitch", 0.0), values.get("yaw", 0.0), values.get("roll", 0.0))
    return unreal.Rotator(pitch=float(values[0]), yaw=float(values[1]), roll=float(values[2]))


def in_play_session():
    """True, solange eine Spielvorschau (PIE) läuft; dann gibt es keine bearbeitbare Editorwelt."""
    handler = level_subsystem()
    try:
        return bool(handler.is_in_play_in_editor()) if handler is not None else False
    except BaseException:
        return False


def find_actor(word, wanted):
    """Sucht einen Schauspieler nach Name, Beschriftung oder Pfad, eindeutig."""
    if not wanted:
        raise OpsError("unreal.actor_name_required", "Ein Schauspielername fehlt.")
    matches = []
    for actor in actors_in_world(word):
        try:
            name = actor.get_name()
            label = actor.get_actor_label() if hasattr(actor, "get_actor_label") else ""
            path = actor.get_path_name()
        except BaseException:
            continue
        if wanted in (name, label, path):
            matches.append(actor)
    if not matches:
        raise OpsError("unreal.actor_missing", f"Kein Schauspieler namens {wanted} in der offenen Welt.")
    if len(matches) > 1:
        raise OpsError("unreal.actor_ambiguous",
                       f"{len(matches)} Schauspieler entsprechen {wanted}; vollständigen Pfad angeben.")
    return matches[0]


def actor_report(actor):
    # get_actor_bounds liefert in UE 5.8 zwei Vektoren: Mittelpunkt und halbe Ausdehnung.
    try:
        origin, extent = actor.get_actor_bounds(False)
    except BaseException:
        origin, extent = None, None
    report = {
        "name": actor.get_name(),
        "path": actor.get_path_name(),
        "classPath": class_path(actor),
        "location": vector_dict(actor.get_actor_location()),
        "rotation": rotation_dict(actor.get_actor_rotation()),
        "scale": scale_dict(actor.get_actor_scale3d()),
    }
    try:
        report["label"] = actor.get_actor_label()
    except BaseException:
        pass
    if extent is not None:
        report["boundsOrigin"] = vector_dict(origin) if origin is not None else {}
        report["boundsExtent"] = vector_dict(extent)
        if origin is not None:
            report["boundsMin"] = {"x": origin.x - extent.x, "y": origin.y - extent.y, "z": origin.z - extent.z}
            report["boundsMax"] = {"x": origin.x + extent.x, "y": origin.y + extent.y, "z": origin.z + extent.z}
    try:
        report["hiddenInEditor"] = bool(actor.is_hidden_ed())
    except BaseException:
        pass
    return report


@operation("actor.place", "Setzt ein Asset, eine Grundform oder eine Klasse an Ort, Drehung und Maßstab.")
def op_actor_place(arguments, context):
    handler = actor_subsystem()
    if handler is None:
        raise OpsError("unreal.actor_subsystem_missing", "Der Editor stellt kein EditorActorSubsystem zur Verfügung.")
    world = current_world()
    if world is None:
        raise OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")

    position = vector_from(arguments, "position")
    angles = angles_from(arguments, "rotation")
    size = vector_from(arguments, "scale", 1.0)
    location = make_vector(position)
    rotation = make_rotator(angles)

    asset_path = text(arguments, "assetPath")
    primitive = text(arguments, "primitive").strip().lower()
    class_path_value = text(arguments, "actorClass")

    if asset_path:
        valid_asset_path(asset_path)
        asset = load_asset(asset_path)
        if asset is None:
            raise OpsError("unreal.asset_missing", f"Das Asset {asset_path} wurde nicht gefunden.")
        created, form = call_any(handler, "spawn_actor_from_object",
                                 [(asset, location, rotation), (asset, location, rotation, None)])
    elif primitive:
        mesh_path = BASIC_SHAPES.get(primitive)
        if mesh_path is None:
            raise OpsError("unreal.primitive_unknown",
                           "Grundformen sind " + ", ".join(sorted(BASIC_SHAPES)) + ".")
        mesh = load_asset(mesh_path)
        if mesh is None:
            raise OpsError("unreal.primitive_mesh_missing", f"Die Grundform {mesh_path} fehlt in dieser Engine.")
        created, form = call_any(handler, "spawn_actor_from_class",
                                 [(unreal.StaticMeshActor, location, rotation)])
        if created is not None:
            component = attribute(created, "static_mesh_component") or next(
                (item for item in (attribute(created, "components") or [])
                 if isinstance(item, unreal.StaticMeshComponent)), None)
            if component is not None:
                assigned, last_error = False, ""
                # call_method expects its arguments as a tuple; the property route is the fallback.
                for attempt_name, attempt in (
                    ("call_method SetStaticMesh", lambda: component.call_method("SetStaticMesh", (mesh,))),
                    ("set_editor_property static_mesh", lambda: component.set_editor_property("static_mesh", mesh)),
                ):
                    try:
                        attempt()
                        assigned = True
                        break
                    except BaseException as error:
                        last_error = f"{attempt_name}: {error}"
                if not assigned:
                    raise OpsError("unreal.primitive_mesh_unassigned",
                                   f"Das Netz der Grundform {primitive} konnte nicht zugewiesen werden: {last_error}")
    elif class_path_value:
        wanted_class = unreal.find_object(None, class_path_value) if hasattr(unreal, "find_object") else None
        if wanted_class is None:
            raise OpsError("unreal.class_missing", f"Die Klasse {class_path_value} wurde nicht gefunden.")
        created, form = call_any(handler, "spawn_actor_from_class", [(wanted_class, location, rotation)])
    else:
        raise OpsError("unreal.place_target_required",
                       "assetPath, primitive oder actorClass ist erforderlich.")
    if created is None:
        raise OpsError("unreal.actor_spawn_failed", "Der Schauspieler konnte nicht gesetzt werden.")

    label = text(arguments, "label")
    if label:
        try:
            created.set_actor_label(label)
        except BaseException as error:
            raise OpsError("unreal.actor_label_failed", f"Die Beschriftung wurde nicht angenommen: {error}")
    if size != (1.0, 1.0, 1.0):
        try:
            created.set_actor_scale3d(make_vector(size))
        except BaseException:
            pass
    if "visible" in (arguments or {}):
        try:
            created.set_is_hidden_ed(not flag(arguments, "visible", True))
        except BaseException:
            pass
    try:
        handler.set_actor_transform(created, make_vector(position), make_rotator(angles), False)
    except BaseException:
        pass

    mark_world_dirty(world)
    report = actor_report(created)
    report["spawnForm"] = str(form)
    report["world"] = world.get_path_name()
    report["actorCount"] = len(actors_in_world(world))
    report["operationId"] = text(arguments, "operationId")
    report["message"] = "Schauspieler gesetzt; die Karte enthält ungespeicherte Änderungen."
    return report


def mark_world_dirty(world):
    try:
        world.get_outermost().mark_package_dirty()
    except BaseException:
        pass


@operation("actor.read", "Liest einen gesetzten Schauspieler mit Ort, Drehung, Maßstab und Ausdehnung.")
def op_actor_read(arguments, context):
    world = current_world()
    if world is None:
        raise OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")
    wanted = text(arguments, "actorName") or text(arguments, "name") or text(arguments, "assetPath")
    actor = find_actor(world, wanted)
    report = actor_report(actor)
    report["world"] = world.get_path_name()
    components = []
    try:
        for component in actor.get_components(True):
            components.append({"name": component.get_name(), "classPath": class_path(component)})
    except BaseException:
        pass
    report["components"] = components[:MAX_ITEMS_REPORTED]
    return report


# Kommandos, die der Adapter aus dem bestehenden Katalog kennt, aber erst in späteren
# Ausbaustufen über Python geliefert werden. Sie antworten mit einer klaren Grenze,
# statt eine nicht ausgeführte Änderung vorzutäuschen.
KNOWN_UNIMPLEMENTED = {
    # Leer: alle dem Adapter bekannten Kommandos sind registriert. Neue Grenzen tragen hier
    # eine ausdrückliche Meldung, statt als unbekannte Antwort durchzugehen.
}


def describe_operations():
    return {name: getattr(function, "operation_description", "") for name, function in OPERATIONS.items()}
