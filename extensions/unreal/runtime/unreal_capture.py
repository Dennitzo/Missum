# SPDX-License-Identifier: MIT
"""Szenenwerkzeuge des Missum-Unreal-Kanals: Aufnahme, Schauspieler, Kamera, Licht und Himmel.

Das Modul trägt sich in das Register von `unreal_ops` ein und wird von `unreal_bootstrap`
nach dem Kern geladen. Alle Aufrufe geschehen auf dem Spielthread.

Aufnahmen sind Aufträge über mehrere Bildwiederholungen. Der erste Aufruf stößt die Erzeugung
an und meldet `pending`; der Pump prüft jede Runde das Ergebnis, und `operation.status` liefert
die fertige Antwort. Der Editor-Thread wird dabei nie blockiert.

Die Aufnahme nutzt den von der Engine freigegebenen Konsolenweg `HighResShot` auf den aktiven
Editor-Viewport. Für eine Kameraaufnahme wird der Viewport zuerst an die Kamera gelegt und nach
der Aufnahme wiederhergestellt. Jede Antwort trägt ihren Aufnahmezweig; steht der Editorweg
nicht zur Verfügung, sagt die Antwort es ausdrücklich, damit ein Rückfall auf die
Fensteraufnahme nie als Editoraufnahme ausgegeben wird.
"""

import math
import os
import shutil
import time
import uuid

import unreal

import unreal_ops as ops


CAPTURE_TIMEOUT_SECONDS = 120.0
STABLE_SECONDS = 2.0
# HighResShot silently does nothing in some editor configurations; try the next route after this.
FALLBACK_AFTER_SECONDS = 12.0
LIGHT_KINDS = {
    "directional": "DirectionalLight",
    "point": "PointLight",
    "spot": "SpotLight",
    "rect": "RectLight",
    "sky": "SkyLight",
}

_size_cache = {}


def _now():
    return time.monotonic()


# ---------------------------------------------------------------------------
# Viewportzugriff
# ---------------------------------------------------------------------------

_viewport_cache = {"key": None, "label": "", "tried": []}

# Die Engine adressiert Viewports über einen Namen; der Schlüssel des aktiven Fensters ist
# nicht dokumentiert. Suchen statt raten: der erste Schlüssel, der eine Antwort gibt, wird benutzt.
VIEWPORT_KEY_CANDIDATES = ("", "Editor", "Perspective", "ActiveEditorViewport", "None")


def viewport_key():
    if _viewport_cache["key"] is not None:
        return _viewport_cache["key"]
    handler = ops.level_subsystem()
    if handler is None:
        return None
    for candidate in VIEWPORT_KEY_CANDIDATES:
        try:
            key = unreal.Name(candidate) if candidate else unreal.Name._create()
        except BaseException:
            try:
                key = unreal.Name(candidate)
            except BaseException:
                continue
        try:
            info = handler.get_level_viewport_camera_info(key)
        except BaseException as error:
            _viewport_cache["tried"].append(f"{candidate or '<leer>'}: {type(error).__name__}")
            continue
        if info is None:
            _viewport_cache["tried"].append(f"{candidate or '<leer>'}: keine Antwort")
            continue
        _viewport_cache["key"] = key
        _viewport_cache["label"] = candidate or "<leer>"
        return key
    return None


def viewport_camera_info():
    """Ort und Blickrichtung des aktiven Editor-Viewports, so gut die Engine sie preisgibt."""
    handler = ops.level_subsystem()
    key = viewport_key()
    if handler is None or key is None:
        return None
    try:
        result = handler.get_level_viewport_camera_info(key)
        if isinstance(result, tuple) and len(result) >= 2:
            return result[0], result[1]
    except BaseException:
        return None
    return None


def viewport_fov(default=90.0):
    handler = ops.level_subsystem()
    key = viewport_key()
    if handler is None or key is None:
        return default
    try:
        value = handler.get_level_viewport_fov(key)
        return float(value) if value is not None else default
    except BaseException:
        return default


def set_viewport_camera_info(location, rotation):
    """Legt den aktiven Editor-Viewport fest. Die Engine akzeptiert je Version verschiedene Formen."""
    handler = ops.level_subsystem()
    key = viewport_key()
    if handler is None or location is None or rotation is None:
        return False, ("kein LevelEditorSubsystem, kein vorgabewert oder kein erreichbarer Viewport-Schlüssel "
                       f"(versucht: {', '.join(_viewport_cache['tried'])})")
    try:
        ops.call_any(handler, "set_level_viewport_camera_info",
                     [(location, rotation, key), (location, rotation)])
        return True, ""
    except ops.OpsError as error:
        return False, error.message


def camera_field_of_view(component):
    """Sichtfeld einer Kamerabauteils, über alle verfügbaren Zugriffsformen."""
    if component is None:
        return 90.0
    for attempt in (lambda: float(component.field_of_view),
                    lambda: float(component.get_field_of_view()),
                    lambda: float(component.get_editor_property("field_of_view"))):
        try:
            value = attempt()
            return float(value)
        except BaseException:
            continue
    return 90.0


# ---------------------------------------------------------------------------
# Aufnahme
# ---------------------------------------------------------------------------

def _screenshot_directories():
    found = []
    for call_name in ("screen_shot_dir", "project_saved_dir"):
        try:
            value = getattr(unreal.Paths, call_name)()
            if value:
                found.append(os.path.normpath(str(value)))
        except BaseException:
            continue
    return list(dict.fromkeys(found))


def _snapshot_pngs(directories):
    known = {}
    for directory in directories:
        if not os.path.isdir(directory):
            continue
        for current, subdirectories, filenames in os.walk(directory):
            if len(current.split(os.sep)) - len(directory.split(os.sep)) > 4:
                continue
            for filename in filenames:
                if not filename.lower().endswith(".png"):
                    continue
                full = os.path.join(current, filename)
                try:
                    known[full] = os.stat(full).st_size
                except OSError:
                    pass
    return known


def _newer_png(directories, known, since_wall, token=""):
    """Neueste Aufnahme dieses Auftrags. Mit token zählt nur eine Datei, deren Name ihn trägt:
    HighResShot schreibt oft erst Sekunden später, und eine verspätete Datei eines früheren
    Auftrags oder ein eigener HighResShot des Agenten darf nie als diese Aufnahme gelten."""
    best = None
    for path, size in _snapshot_pngs(directories).items():
        if path in known:
            continue
        if token and token not in os.path.basename(path):
            continue
        try:
            modified = os.stat(path).st_mtime
        except OSError:
            continue
        if modified < since_wall - 5:
            continue
        if best is None or modified > best[1]:
            best = (path, modified, size)
    return best


def _size_stable(path):
    try:
        size = os.stat(path).st_size
    except OSError:
        return False
    if size <= 8:
        return False
    state = _size_cache.setdefault(path, {"size": size, "seen": _now()})
    if state["size"] != size:
        state["size"] = size
        state["seen"] = _now()
        return False
    return _now() - state["seen"] >= STABLE_SECONDS


def _fail(record, code, message):
    record.update({"state": "failed", "errorCode": code, "errorMessage": message})


def _complete(record, output_path, route):
    try:
        info = os.stat(output_path)
    except OSError:
        _fail(record, "unreal.capture_missing", "Die Aufnahme wurde nicht als Datei geschrieben.")
        return
    record.update({
        "state": "completed",
        "path": output_path,
        "bytes": int(info.st_size),
        "renderMode": route,
        "captureRoute": route,
    })


@ops.operation("view.capture", "Nimmt den Editor-Viewport oder eine benannte Kamera auf, mit Quellekennung.")
def op_view_capture(arguments, context):
    operation_id = ops.text(arguments, "operationId")
    if not operation_id:
        raise ops.OpsError("unreal.operation_id_required", "Eine Aufnahme braucht eine operationId.")
    root = context.get("captureRoot") or ""
    if not root:
        raise ops.OpsError("unreal.capture_root_missing",
                           "Das Missum-Aufnahmeverzeichnis wurde dem Editor nicht mitgeteilt.")
    output_path = ops.text(arguments, "outputPath")
    if not output_path or not os.path.isabs(output_path):
        raise ops.OpsError("unreal.capture_path", "Das Bildziel fehlt oder ist kein absoluter Pfad.")
    output_path = os.path.normpath(output_path)
    if not output_path.lower().endswith(".png"):
        raise ops.OpsError("unreal.capture_path", "Das Bildziel muss auf .png enden.")
    real_root = os.path.realpath(root).lower()
    real_target = os.path.realpath(output_path).lower()
    if not real_target.startswith(real_root + os.sep):
        raise ops.OpsError("unreal.capture_path", "Das Bildziel liegt nicht im Missum-Aufnahmeordner.")
    if os.path.exists(output_path):
        raise ops.OpsError("unreal.capture_path", "Das Bildziel existiert bereits.")

    if ops.in_play_session():
        raise ops.OpsError("unreal.pie_running",
                           "Die Spielvorschau läuft; der Editor hat währenddessen keine aufnehmbare Editorwelt. "
                           "Beende die Vorschau (unreal.python: LevelEditorSubsystem.editor_request_end_play()) "
                           "oder nimm das Spielfenster mit image.input operation=capture auf.")
    world = ops.current_world()
    if world is None:
        raise ops.OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")

    source = ops.text(arguments, "source", "viewport")
    if source not in ("viewport", "camera"):
        raise ops.OpsError("unreal.capture_source", "Quelle muss viewport oder camera sein.")

    record = {
        "state": "pending",
        "operationId": operation_id,
        "projectPath": context.get("projectPath", ""),
        "instanceId": context.get("instanceId", ""),
        "world": world.get_path_name(),
        "source": source,
        "cameraRequest": ops.text(arguments, "cameraName"),
        "path": output_path,
        "startedAt": _now(),
        "startedWall": time.time(),
        "route": "highResShot",
        "token": "MissumShot_" + uuid.uuid4().hex[:16],
        "attempts": [],
        "_restore": None,
    }
    context.setdefault("operations", {})[operation_id] = record

    if source == "camera":
        camera = _find_camera(world, record["cameraRequest"])
        component = _camera_component(camera)
        position = camera.get_actor_location()
        rotation = camera.get_actor_rotation()
        record["cameraName"] = camera.get_path_name()
        record["position"] = ops.vector_dict(position)
        record["rotation"] = ops.rotation_dict(rotation)
        record["fieldOfView"] = camera_field_of_view(component)
        before = viewport_camera_info()
        if before is not None:
            record["_restore"] = (before[0], before[1])
        moved, reason = set_viewport_camera_info(position, rotation)
        if not moved:
            _fail(record, "unreal.capture_viewport_uncontrolled",
                  "Der Editor-Viewport ließ sich nicht auf die Kamera legen: " + reason + ". "
                  "Als Rückfall steht die Fensteraufnahme über image.input offen.")
            return {"state": "failed", "operationId": operation_id,
                    "errorCode": "unreal.capture_viewport_uncontrolled",
                    "errorMessage": record.get("errorMessage", ""), "route": "viewportMove"}

    directories = _screenshot_directories()
    record["directories"] = directories
    record["_known"] = _snapshot_pngs(directories)
    width = int(ops.number(arguments, "width", 1920))
    height = int(ops.number(arguments, "height", 1080))
    record["width"], record["height"] = width, height
    command = f"HighResShot {width}x{height} filename={record['token']}"
    try:
        unreal.SystemLibrary.execute_console_command(world, command)
        record["attempts"].append(f"{command} gesendet")
    except BaseException as error:
        _fail(record, "unreal.capture_route_unavailable",
              f"Der Editorweg HighResShot steht nicht zur Verfügung ({error}). "
              "Nutze die Fensteraufnahme über image.input mit der Fensterkennung des Editors.")
        _restore_viewport(record)
        return {"state": "failed", "operationId": operation_id, "route": record["route"]}
    return {"state": "pending", "operationId": operation_id, "route": record["route"]}


def _restore_viewport(record):
    restore = record.pop("_restore", None)
    if restore is None:
        return
    set_viewport_camera_info(restore[0], restore[1])


def advance_captures(context):
    """Rückt laufende Aufträge weiter; wird vom Pump in jeder Runde gerufen."""
    for record in list((context.get("operations") or {}).values()):
        if record.get("state") != "pending":
            continue
        output_path = record.get("path", "")
        found = _newer_png(record.get("directories", []), record.get("_known") or {},
                           record.get("startedWall", 0), record.get("token", ""))
        if found is not None and _size_stable(found[0]):
            try:
                os.makedirs(os.path.dirname(output_path), exist_ok=True)
                shutil.copyfile(found[0], output_path)
            except OSError as error:
                _fail(record, "unreal.capture_copy_failed", str(error))
            record["engineFile"] = found[0] if found else ""
            _restore_viewport(record)
            _complete(record, output_path, record.get("route", "highResShot"))
            continue
        if _try_next_route(record):
            continue
        if _now() - record.get("startedAt", 0) > CAPTURE_TIMEOUT_SECONDS:
            _restore_viewport(record)
            _fail(record, "unreal.capture_timeout",
                  f"Die Editoraufnahme kam innerhalb von {int(CAPTURE_TIMEOUT_SECONDS)} Sekunden nicht an "
                  f"(Suchordner: {', '.join(record.get('directories', []))}). "
                  "Als Rückfall steht die Fensteraufnahme über image.input offen.")


def _try_next_route(record):
    """Wechselt nach FALLBACK_AFTER_SECONDS ohne Datei auf den nächsten Aufnahmeweg."""
    started = record.get("routeStartedAt", record.get("startedAt", 0))
    if _now() - started < FALLBACK_AFTER_SECONDS:
        return False
    route = record.get("route", "highResShot")
    world = ops.current_world()
    width = int(record.get("width", 1920))
    height = int(record.get("height", 1080))
    if route == "highResShot":
        record["route"] = "automationScreenshot"
        record["routeStartedAt"] = _now()
        try:
            name = record.get("token") or ("MissumCapture_" + uuid.uuid4().hex[:16])
            unreal.AutomationLibrary.take_high_res_screenshot(width, height, name)
            record["attempts"].append("AutomationLibrary.take_high_res_screenshot gesendet")
        except BaseException as error:
            record["attempts"].append(f"AutomationLibrary nicht verfügbar ({error})")
            record["routeStartedAt"] = started - FALLBACK_AFTER_SECONDS
        return True
    if route == "automationScreenshot":
        record["route"] = "sceneCapture"
        record["routeStartedAt"] = _now()
        try:
            path = _scene_capture(world, record, width, height)
            os.makedirs(os.path.dirname(record["path"]), exist_ok=True)
            shutil.copyfile(path, record["path"])
            record["engineFile"] = path
            _restore_viewport(record)
            _complete(record, record["path"], "sceneCapture")
        except BaseException as error:
            record["attempts"].append(f"SceneCapture2D fehlgeschlagen ({type(error).__name__}: {error})")
            # Der letzte Weg ist gescheitert: sofort melden statt bis zur Zeitgrenze zu warten.
            _restore_viewport(record)
            _fail(record, "unreal.capture_failed",
                  "Keine Editoraufnahme möglich (" + "; ".join(record["attempts"]) + "). "
                  "Als Rückfall steht die Fensteraufnahme über image.input offen.")
        return True
    return False


def _scene_capture(world, record, width, height):
    """Rendert die Ansicht des Viewports (oder der Kamera) über ein SceneCapture2D in eine PNG."""
    info = viewport_camera_info()
    if record.get("source") == "camera" and record.get("position"):
        # position/rotation liegen als Wörterbücher im Auftrag; make_* nimmt beide Formen.
        location = ops.make_vector(record["position"])
        rotation = ops.make_rotator(record["rotation"])
    elif info is not None:
        location, rotation = info[0], info[1]
    else:
        raise RuntimeError("Die Viewport-Kamera ist nicht lesbar.")
    target = unreal.RenderingLibrary.create_render_target2d(
        world, width, height, unreal.TextureRenderTargetFormat.RTF_RGBA8)
    # The temporary capture actor must not leave the map "changed" (that would trigger
    # Unreal's modal save prompt later), so remember the state and restore it afterwards.
    package_name = world.get_outermost().get_name()
    dirty_before = package_name in ops.dirty_package_names()
    actor = unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.SceneCapture2D, location, rotation)
    try:
        component = actor.get_component_by_class(unreal.SceneCaptureComponent2D)
        component.set_editor_property("texture_target", target)
        component.set_editor_property("capture_source", unreal.SceneCaptureSource.SCS_FINAL_COLOR_LDR)
        if record.get("fieldOfView"):
            component.set_editor_property("fov_angle", float(record["fieldOfView"]))
        component.capture_scene()
        directory = os.path.dirname(record["path"])
        name = "missum-scene-" + record.get("operationId", "x")[:12]
        unreal.RenderingLibrary.export_render_target(world, target, directory, name)
        for candidate in (os.path.join(directory, name + ".png"), os.path.join(directory, name)):
            if os.path.exists(candidate) and os.path.getsize(candidate) > 8:
                return candidate
        raise RuntimeError("Das Render-Target wurde nicht als Bild geschrieben.")
    finally:
        actor.destroy_actor()
        # A saved map that was clean before goes back to clean: its content is unchanged again.
        if not dirty_before and not package_name.startswith("/Temp/"):
            try:
                handler = ops.level_subsystem()
                if handler is not None:
                    handler.save_current_level()
            except Exception:
                pass


# ---------------------------------------------------------------------------
# Schauspieler: nachlesen, ändern, entfernen, prüfen
# ---------------------------------------------------------------------------

@ops.operation("actor.set", "Ändert Ort, Drehung, Maßstab, Beschriftung, Sichtbarkeit oder ein Feld.")
def op_actor_set(arguments, context):
    world = ops.current_world()
    if world is None:
        raise ops.OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")
    actor = ops.find_actor(world, ops.text(arguments, "actorName") or ops.text(arguments, "name"))
    changed = []
    if "position" in arguments:
        actor.set_actor_location(ops.make_vector(ops.vector_from(arguments, "position")), False, False)
        changed.append("position")
    if "rotation" in arguments:
        actor.set_actor_rotation(ops.make_rotator(ops.angles_from(arguments, "rotation")), False)
        changed.append("rotation")
    if "scale" in arguments:
        actor.set_actor_scale3d(ops.make_vector(ops.vector_from(arguments, "scale", 1.0)))
        changed.append("scale")
    if ops.text(arguments, "label"):
        actor.set_actor_label(ops.text(arguments, "label"))
        changed.append("label")
    if "visible" in arguments:
        actor.set_is_hidden_ed(not ops.flag(arguments, "visible", True))
        changed.append("visible")
    property_name = ops.text(arguments, "property")
    if property_name:
        raw = arguments.get("value")
        target = actor
        component_path = ops.text(arguments, "componentPath")
        if component_path:
            for component in actor.get_components(True):
                if component.get_name() == component_path:
                    target = component
                    break
        try:
            target.set_editor_property(property_name, raw)
            changed.append(property_name)
        except BaseException as error:
            raise ops.OpsError("unreal.actor_property_rejected",
                               f"{property_name}: {type(error).__name__}: {error}")
    ops.mark_world_dirty(world)
    result = ops.actor_report(actor)
    result["changed"] = changed
    result["operationId"] = ops.text(arguments, "operationId")
    return result


@ops.operation("actor.remove", "Entfernt einen benannten Schauspieler aus der offenen Welt.")
def op_actor_remove(arguments, context):
    world = ops.current_world()
    if world is None:
        raise ops.OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")
    actor = ops.find_actor(world, ops.text(arguments, "actorName") or ops.text(arguments, "name"))
    label = actor.get_actor_label()
    path = actor.get_path_name()
    handler = ops.actor_subsystem()
    try:
        removed = bool(handler.destroy_actor(actor))
    except BaseException as error:
        raise ops.OpsError("unreal.actor_remove_failed", f"{type(error).__name__}: {error}")
    if removed:
        ops.mark_world_dirty(world)
    return {
        "removed": removed,
        "label": label,
        "path": path,
        "actorCount": len(ops.actors_in_world(world)),
        "operationId": ops.text(arguments, "operationId"),
    }


@ops.operation("actor.verify", "Prüft Lage, Ausdehnung, Materialfelder und Blickfeldzugehörigkeit.")
def op_actor_verify(arguments, context):
    world = ops.current_world()
    if world is None:
        raise ops.OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")
    actor = ops.find_actor(world, ops.text(arguments, "actorName") or ops.text(arguments, "name"))
    result = ops.actor_report(actor)
    result["world"] = world.get_path_name()

    fields = []
    try:
        for component in actor.get_components_by_class(unreal.PrimitiveComponent):
            info = {"component": component.get_name(), "classPath": ops.class_path(component)}
            try:
                info["materials"] = [
                    {"slot": index, "material": item.get_path_name() if item is not None else None}
                    for index, item in enumerate(component.get_materials() or [])
                ]
            except BaseException:
                pass
            try:
                info["slotNames"] = [str(item) for item in component.get_material_slot_names()]
            except BaseException:
                pass
            fields.append(info)
    except BaseException:
        pass
    result["materialFields"] = fields

    reference_label = ops.text(arguments, "referenceActor")
    origin, extent = actor.get_actor_bounds(False)
    if reference_label:
        reference = ops.find_actor(world, reference_label)
        distance = reference.get_actor_location().distance_to(origin)
        result["distanceToReference"] = float(distance)
    if "referencePosition" in arguments:
        point = ops.make_vector(ops.vector_from(arguments, "referencePosition"))
        result["distanceToReferencePosition"] = float(point.distance_to(origin))

    camera_position, camera_rotation, camera_fov, view_source = None, None, 90.0, ""
    wanted_camera = ops.text(arguments, "cameraName")
    if wanted_camera:
        camera = _find_camera(world, wanted_camera)
        camera_position = camera.get_actor_location()
        camera_rotation = camera.get_actor_rotation()
        component = _camera_component(camera)
        camera_fov = camera_field_of_view(component)
        view_source = wanted_camera
    else:
        info = viewport_camera_info()
        if info is not None:
            camera_position, camera_rotation = info
            camera_fov = viewport_fov(camera_fov)
            view_source = f"editor-viewport ({_viewport_cache['label']})"
    if camera_position is not None:
        # Vektorlänge wird selbst berechnet: length ist hier eine Methode, keine Zahl.
        delta = (origin.x - camera_position.x, origin.y - camera_position.y,
                 origin.z - camera_position.z)
        distance = math.sqrt(delta[0] ** 2 + delta[1] ** 2 + delta[2] ** 2)
        forward = camera_rotation.get_forward_vector()
        cosine = 0.0
        if distance > 0:
            cosine = (delta[0] * forward.x + delta[1] * forward.y + delta[2] * forward.z) / distance
        angle = math.degrees(math.acos(max(-1.0, min(1.0, cosine))))
        half = camera_fov / 2.0
        result["camera"] = {
            "source": view_source,
            "position": ops.vector_dict(camera_position),
            "rotation": {"pitch": float(camera_rotation.pitch), "yaw": float(camera_rotation.yaw),
                         "roll": float(camera_rotation.roll)},
            "fieldOfView": camera_fov,
        }
        result["distanceToCamera"] = distance
        result["angleToCameraView"] = angle
        result["inView"] = bool(angle <= half and (not result.get("maxDistance")
                                                   or distance <= float(result.get("maxDistance"))))
    if "expectLocation" in arguments:
        expected = ops.vector_from(arguments, "expectLocation")
        tolerance = float(ops.number(arguments, "tolerance", 5.0))
        delta = math.sqrt(sum((a - b) ** 2 for a, b in
                              zip((origin.x, origin.y, origin.z), expected)))
        result["locationDelta"] = delta
        result["locationMatches"] = bool(delta <= tolerance)
    if "expectExtent" in arguments:
        expected = ops.vector_from(arguments, "expectExtent")
        deltas = [abs(a - b) for a, b in zip((extent.x, extent.y, extent.z), expected)]
        result["extentDelta"] = max(deltas)
    result["operationId"] = ops.text(arguments, "operationId")
    return result


def _find_camera(world, wanted):
    if not wanted:
        raise ops.OpsError("unreal.camera_name_required", "Für eine Kameraangabe fehlt cameraName.")
    matches = []
    for actor in ops.actors_in_world(world):
        try:
            if not isinstance(actor, unreal.CameraActor):
                continue
        except BaseException:
            continue
        if wanted in (actor.get_name(), actor.get_actor_label(), actor.get_path_name()):
            matches.append(actor)
    if not matches:
        raise ops.OpsError("unreal.camera_missing", f"Die Kamera {wanted} wurde nicht gefunden.")
    if len(matches) > 1:
        raise ops.OpsError("unreal.camera_ambiguous",
                           f"{len(matches)} Kameras entsprechen {wanted}; vollständigen Pfad verwenden.")
    return matches[0]


def _camera_component(camera):
    try:
        component = camera.get_component_by_class(unreal.CameraComponent)
        if component is not None:
            return component
    except BaseException:
        pass
    return ops.attribute(camera, "camera_component")


# ---------------------------------------------------------------------------
# Kamera
# ---------------------------------------------------------------------------

@ops.operation("camera.create", "Erzeugt eine Kamera in der offenen Welt, optional mit Sichtfeld.")
def op_camera_create(arguments, context):
    world = ops.current_world()
    if world is None:
        raise ops.OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")
    name = ops.text(arguments, "cameraName")
    if not name:
        raise ops.OpsError("unreal.camera_arguments", "Ein Kameraname fehlt.")
    for actor in ops.actors_in_world(world):
        try:
            if name in (actor.get_name(), actor.get_actor_label()):
                raise ops.OpsError("unreal.camera_exists", "Eine Kamera mit diesem Namen existiert bereits.")
        except BaseException:
            continue
    position = ops.make_vector(ops.vector_from(arguments, "position"))
    rotation = ops.make_rotator(ops.angles_from(arguments, "rotation"))
    handler = ops.actor_subsystem()
    if handler is None:
        raise ops.OpsError("unreal.actor_subsystem_missing", "Der Editor stellt kein EditorActorSubsystem zur Verfügung.")
    # World.spawn_actor gibt es über Python nicht; erzeugt wird über dasselbe System wie bei Grundformen.
    created, form = ops.call_any(handler, "spawn_actor_from_class", [(unreal.CameraActor, position, rotation)])
    if created is None:
        raise ops.OpsError("unreal.camera_failed", "Die Kamera konnte nicht erstellt werden.")
    created.set_actor_label(name)
    component = _camera_component(created)
    fov = ops.number(arguments, "fieldOfView")
    if component is not None and fov is not None:
        try:
            component.field_of_view = float(fov)
        except BaseException:
            pass
    if component is not None and ops.number(arguments, "aperture") is not None:
        settings = ops.attribute(component, "post_process_settings")
        if settings is not None:
            try:
                settings.set_editor_property("camera_aperture", float(ops.number(arguments, "aperture")))
            except BaseException:
                pass
    ops.mark_world_dirty(world)
    result = ops.actor_report(created)
    result["fieldOfView"] = camera_field_of_view(component)
    result["worldDirty"] = True
    result["operationId"] = ops.text(arguments, "operationId")
    result["message"] = "Kamera erstellt; die Karte enthält ungespeicherte Änderungen."
    return result


@ops.operation("camera.set", "Setzt Ort, Drehung, Sichtfeld, Blende und Schärfewerte einer Kamera.")
def op_camera_set(arguments, context):
    world = ops.current_world()
    if world is None:
        raise ops.OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")
    camera = _find_camera(world, ops.text(arguments, "cameraName"))
    changed = []
    if "position" in arguments:
        camera.set_actor_location(ops.make_vector(ops.vector_from(arguments, "position")), False, False)
        changed.append("position")
    if "rotation" in arguments:
        camera.set_actor_rotation(ops.make_rotator(ops.angles_from(arguments, "rotation")), False)
        changed.append("rotation")
    component = _camera_component(camera)
    if component is None:
        raise ops.OpsError("unreal.camera_component_missing", "Die Kamera hat keine Bauteilzugabe.")
    for property_name, argument_name in (("field_of_view", "fieldOfView"),
                                         ("near_clip_plane", "nearClip"),
                                         ("far_clip_plane", "farClip"),
                                         ("aspect_ratio", "aspectRatio")):
        value = ops.number(arguments, argument_name)
        if value is not None and ops.set_if_present(component, property_name, float(value)):
            changed.append(argument_name)
    settings = ops.attribute(component, "post_process_settings")
    if settings is not None:
        for property_name, argument_name in (("camera_aperture", "aperture"),
                                             ("focus_distance", "focusDistance"),
                                             ("depth_of_field_blur_radius", "blurRadius"),
                                             ("motion_blur_amount", "motionBlur")):
            value = ops.number(arguments, argument_name)
            if value is not None and ops.set_if_present(settings, property_name, float(value)):
                changed.append(argument_name)
    ops.mark_world_dirty(world)
    result = ops.actor_report(camera)
    result["changed"] = changed
    result["fieldOfView"] = camera_field_of_view(component)
    result["operationId"] = ops.text(arguments, "operationId")
    return result


# Himmel, Wolken, Nebel, Licht und Weltverwaltung haben riesige oder gar keine Ausdehnung. Mit ihnen
# rückte frame die Kamera kilometerweit über die Wolken; die Karte selbst war dann nicht mehr zu sehen.
FRAME_SKIPPED_CLASSES = {
    "SkyAtmosphere", "VolumetricCloud", "ExponentialHeightFog", "SkyLight", "DirectionalLight",
    "PostProcessVolume", "WorldDataLayers", "WorldPartitionMiniMap", "WorldSettings", "CameraActor",
    "BP_Sky_Sphere_C", "AtmosphericFog", "LightmassImportanceVolume", "SphereReflectionCapture",
    "PlayerStart", "NavMeshBoundsVolume", "RecastNavMesh", "AbstractNavData", "GameplayDebuggerPlayerManager",
}
FRAME_MAX_EXTENT = 500000.0  # 5 km halbe Kantenlänge


def _frames_scene_content(actor):
    try:
        name = actor.get_class().get_name()
    except BaseException:
        return False
    if name in FRAME_SKIPPED_CLASSES or name.endswith("Volume") or name.startswith("Light"):
        return False
    try:
        return bool(actor.get_components_by_class(unreal.PrimitiveComponent))
    except BaseException:
        return True


@ops.operation("camera.frame", "Richtet eine Kamera auf die Ausdehnung einer Auswahl, eines Bereichs oder einer Karte.")
def op_camera_frame(arguments, context):
    world = ops.current_world()
    if world is None:
        raise ops.OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")
    # Ohne cameraName wird der Editor-Viewport selbst ausgerichtet; der Agent wollte im Livetest
    # genau das ("Karte ins Bild rücken") und bekam nur camera_name_required.
    camera = _find_camera(world, ops.text(arguments, "cameraName")) if ops.text(arguments, "cameraName") else None
    wanted = ops.text(arguments, "actorName")
    actors = []
    if wanted:
        actors = [ops.find_actor(world, wanted)]
    elif ops.flag(arguments, "selected", False):
        handler = ops.actor_subsystem()
        try:
            actors = list(handler.get_selected_level_actors())
        except BaseException:
            actors = []
    if not actors:
        actors = [actor for actor in ops.actors_in_world(world) if _frames_scene_content(actor)]
    points = []
    for actor in actors:
        try:
            origin, extent = actor.get_actor_bounds(False)
            if max(abs(extent.x), abs(extent.y), abs(extent.z)) > FRAME_MAX_EXTENT and len(actors) > 1:
                continue
            points.append((origin.x - extent.x, origin.y - extent.y, origin.z - extent.z))
            points.append((origin.x + extent.x, origin.y + extent.y, origin.z + extent.z))
        except BaseException:
            continue
    if not points:
        raise ops.OpsError("unreal.frame_empty", "Die angegebene Menge hat keine Ausdehnung.")
    minimum = [min(item[axis] for item in points) for axis in range(3)]
    maximum = [max(item[axis] for item in points) for axis in range(3)]
    center = unreal.Vector(*[(minimum[axis] + maximum[axis]) / 2.0 for axis in range(3)])
    radius = 0.5 * math.sqrt(sum((maximum[axis] - minimum[axis]) ** 2 for axis in range(3)))
    fov = camera_field_of_view(_camera_component(camera)) if camera is not None else viewport_fov()
    distance = radius / math.tan(math.radians(fov / 2.0)) * 1.15
    pitch = float(ops.number(arguments, "pitch", -25.0))
    yaw = float(ops.number(arguments, "yaw", 135.0))
    rotation = ops.make_rotator((pitch, yaw, 0.0))
    forward = rotation.get_forward_vector()
    position = unreal.Vector(center.x - forward.x * distance,
                             center.y - forward.y * distance,
                             center.z - forward.z * distance)
    if camera is None:
        moved, reason = set_viewport_camera_info(position, rotation)
        if not moved:
            raise ops.OpsError("unreal.viewport_uncontrolled", "Der Editor-Viewport ließ sich nicht ausrichten: " + reason)
        result = {"target": "editorViewport", "position": ops.vector_dict(position),
                  "rotation": ops.rotation_dict(rotation)}
    else:
        camera.set_actor_location(position, False, False)
        camera.set_actor_rotation(rotation, False)
        ops.mark_world_dirty(world)
        result = ops.actor_report(camera)
    result["frame"] = {
        "center": ops.vector_dict(center),
        "radius": radius,
        "distance": distance,
        "fieldOfView": fov,
        "actorCount": len(actors),
    }
    result["operationId"] = ops.text(arguments, "operationId")
    return result


# Der Pump in unreal_bootstrap ruft pro Runde genau diese Weiterführung auf.
ops.TICK_HANDLERS["advance"] = advance_captures
