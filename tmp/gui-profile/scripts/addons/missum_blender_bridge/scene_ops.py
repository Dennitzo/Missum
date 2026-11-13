# SPDX-License-Identifier: MIT
"""Command handlers for the Missum Blender bridge.

Everything in this module runs in Blender's main thread: the socket worker in
``__init__.py`` only queues requests and this module answers them.  The handlers
are intentionally small and composable so a model can build any modelling task
out of single changes instead of relying on a fixed recipe.

Contract with ``Missum.BlenderAdapter``:

* request  ``{"cmd": ..., "args": {...}, "operationId": "..."}``
* reply    ``{"ok": true, "result": {...}}`` or
            ``{"ok": false, "error": {"code": ..., "message": ...}}``

Argument names below must not be ``path``, ``file``, ``filePath``, ``folder``,
``directory`` or ``command``: Missum checks those names against the workspace
boundary before the request is sent, and a ``.blend`` file usually lives outside
the workspace.  ``projectFile`` and ``targetImage`` are used instead.
"""

import math
import traceback

import bpy
import mathutils


class ToolError(Exception):
    """An error the model can act on: code plus a German message."""

    def __init__(self, code, message):
        super().__init__(message)
        self.code = code
        self.message = message


# --------------------------------------------------------------------------- #
# small helpers
# --------------------------------------------------------------------------- #

def _as_float(value, default=None, name="Wert"):
    if value is None:
        return default
    try:
        return float(value)
    except (TypeError, ValueError):
        raise ToolError("blender.argument_invalid", f"{name} muss eine Zahl sein, erhalten: {value!r}")


def _as_vector(value, default, name, size=3):
    if value is None:
        return list(default)
    if not isinstance(value, (list, tuple)) or len(value) != size:
        raise ToolError(
            "blender.argument_invalid",
            f"{name} erwartet [{size} Zahlen], erhalten: {value!r}",
        )
    return [_as_float(item, 0.0, name) for item in value]


def _as_bool(value, default):
    if value is None:
        return bool(default)
    if isinstance(value, bool):
        return value
    if isinstance(value, str):
        return value.strip().lower() in ("1", "true", "ja", "wahr", "on")
    return bool(value)


def _as_str(args, name, default=""):
    value = args.get(name)
    if value is None:
        return default
    return str(value)


def _names_of(args, *keys):
    """Reads a name list from any of the given keys ('name' or 'names')."""
    for key in keys:
        value = args.get(key)
        if value is None:
            continue
        if isinstance(value, str):
            items = [part.strip() for part in value.split(",")]
        elif isinstance(value, (list, tuple)):
            items = [str(part).strip() for part in value]
        else:
            raise ToolError("blender.argument_invalid", f"{key} erwartet einen Namen oder eine Namensliste.")
        return [item for item in items if item]
    return []


def _object(name):
    if not name:
        raise ToolError("blender.object_missing", "Objektname fehlt.")
    obj = bpy.data.objects.get(name)
    if obj is None:
        available = ", ".join(sorted(item.name for item in bpy.data.objects)[:20])
        raise ToolError(
            "blender.object_missing",
            f"Es gibt kein Objekt '{name}'. Vorhanden unter anderem: {available or 'keine'}. "
            "Namen mit blender.scene.read prüfen.",
        )
    return obj


def _active_or_named(args, key="name"):
    names = _names_of(args, key, "names", "objects")
    if names:
        return [_object(name) for name in names]
    selected = [obj for obj in bpy.context.selected_objects if obj != bpy.context.view_layer.objects.active]
    active = bpy.context.view_layer.objects.active
    if active is not None:
        selected.append(active)
    if not selected:
        raise ToolError(
            "blender.object_missing",
            "Kein Objekt angegeben und keines ausgewählt. 'name' setzen oder zuerst auswählen.",
        )
    return selected


def _degrees_to_radians(values):
    return [math.radians(value) for value in values]


def _vector_to_degrees(vector):
    return [round(math.degrees(value), 3) for value in vector]


def _mode_object():
    try:
        if bpy.context.object is not None and bpy.context.object.mode != "OBJECT":
            bpy.ops.object.mode_set(mode="OBJECT")
    except (RuntimeError, TypeError):
        pass


def _collection(name):
    if not name:
        return bpy.context.scene.collection
    collection = bpy.data.collections.get(name)
    if collection is None:
        collection = bpy.data.collections.new(name)
        bpy.context.scene.collection.children.link(collection)
    return collection


def _link_to(obj, collection):
    if obj.name not in collection.objects:
        collection.objects.link(obj)


# --------------------------------------------------------------------------- #
# scene.read
# --------------------------------------------------------------------------- #

def _is_selected(obj):
    try:
        return bool(obj.select_get())
    except (RuntimeError, TypeError):
        # Objects linked to another scene cannot answer this question.
        return False


def _object_summary(obj, include_modifiers=False):
    data = {
        "name": obj.name,
        "type": obj.type,
        "location": [round(value, 4) for value in obj.location],
        "rotationDegrees": _vector_to_degrees(obj.rotation_euler),
        "scale": [round(value, 4) for value in obj.scale],
        "dimensions": [round(value, 4) for value in obj.dimensions],
        "selected": _is_selected(obj),
        "hideViewport": bool(obj.hide_viewport or obj.hide_get()),
        "hideRender": bool(obj.hide_render),
        "parent": obj.parent.name if obj.parent else None,
        "materials": [slot.material.name for slot in obj.material_slots if slot.material],
    }
    if include_modifiers:
        data["modifiers"] = [
            {"name": mod.name, "type": mod.type, "showViewport": bool(mod.show_viewport)}
            for mod in obj.modifiers
        ]
    return data


def scene_read(args, context):
    detail = _as_str(args, "detail", "summary").lower()
    if detail not in ("summary", "selection", "full"):
        detail = "summary"
    limit = int(_as_float(args.get("limit"), 120, "limit"))
    limit = max(1, min(500, limit))
    include = {str(item).lower() for item in (args.get("include") or [])}
    wanted_names = {name for name in _names_of(args, "objectNames", "objects")}

    if detail == "full":
        include = {"objects", "materials", "cameras", "lights", "collections", "world", "render", "modifiers"}
    if not include:
        include = {"objects"}

    result = {
        "sceneName": bpy.context.scene.name,
        "projectPath": bpy.data.filepath or "",
        "unsavedChanges": bool(bpy.data.is_dirty),
        "blenderVersion": bpy.app.version_string,
        "objectCount": len(bpy.data.objects),
        "instanceId": context["instanceId"],
    }

    objects = list(bpy.data.objects)
    if detail == "selection":
        objects = [obj for obj in objects if _is_selected(obj)]
    if wanted_names:
        objects = [obj for obj in objects if obj.name in wanted_names]

    if "objects" in include:
        shown = objects[:limit]
        result["objects"] = [_object_summary(obj, "modifiers" in include) for obj in shown]
        result["shownObjectCount"] = len(shown)
        result["truncated"] = len(objects) > limit
        if result["truncated"]:
            result["hint"] = (
                f"{len(objects) - limit} weitere Objekte nicht angezeigt; limit erhöhen, "
                "objectNames setzen oder detail='selection' verwenden."
            )

    if detail == "selection":
        result["selection"] = [obj.name for obj in objects]

    if "materials" in include:
        result["materials"] = [
            {
                "name": material.name,
                "baseColor": list(round(value, 4) for value in material.diffuse_color),
                "metallic": round(float(getattr(material, "metallic", 0.0)), 4),
                "roughness": round(float(getattr(material, "roughness", 0.5)), 4),
                "useNodes": bool(material.use_nodes),
                "users": material.users,
            }
            for material in bpy.data.materials
        ]

    if "cameras" in include:
        active = bpy.context.scene.camera.name if bpy.context.scene.camera else None
        result["cameras"] = [
            {
                "name": obj.name,
                "active": obj.name == active,
                "lens": round(float(obj.data.lens), 3),
                "location": [round(value, 4) for value in obj.location],
                "rotationDegrees": _vector_to_degrees(obj.rotation_euler),
            }
            for obj in bpy.data.objects
            if obj.type == "CAMERA"
        ]
        result["activeCamera"] = active

    if "lights" in include:
        result["lights"] = [
            {
                "name": obj.name,
                "lightType": obj.data.type,
                "energy": round(float(obj.data.energy), 3),
                "color": list(round(value, 4) for value in obj.data.color),
                "location": [round(value, 4) for value in obj.location],
            }
            for obj in bpy.data.objects
            if obj.type == "LIGHT"
        ]

    if "collections" in include:
        result["collections"] = [
            {"name": coll.name, "objectCount": len(coll.objects)} for coll in bpy.data.collections
        ]

    if "world" in include:
        world = bpy.context.scene.world
        entry = {"name": world.name if world else None}
        if world is not None and world.use_nodes and world.node_tree:
            background = next(
                (node for node in world.node_tree.nodes if node.type == "BACKGROUND"), None
            )
            if background is not None:
                color = background.inputs.get("Color")
                strength = background.inputs.get("Strength")
                entry["color"] = (
                    [round(float(value), 4) for value in color.default_value] if color else None
                )
                entry["strength"] = round(float(strength.default_value), 4) if strength else None
        result["world"] = entry

    if "render" in include:
        render = bpy.context.scene.render
        result["render"] = {
            "engine": bpy.context.scene.render.engine,
            "resolutionX": int(render.resolution_x),
            "resolutionY": int(render.resolution_y),
            "resolutionPercentage": int(render.resolution_percentage),
            "fps": float(render.fps),
            "frameCurrent": int(bpy.context.scene.frame_current),
            "frameStart": int(bpy.context.scene.frame_start),
            "frameEnd": int(bpy.context.scene.frame_end),
            "filmTransparent": bool(render.film_transparent),
            "filepath": render.filepath,
        }

    result["changesSinceLastRead"] = bool(bpy.data.is_dirty)
    return result


# --------------------------------------------------------------------------- #
# object operations
# --------------------------------------------------------------------------- #

_PRIMITIVES = {
    "cube": ("mesh.primitive_cube_add", {"size": 1.0}),
    "plane": ("mesh.primitive_plane_add", {"size": 1.0}),
    "grid": ("mesh.primitive_grid_add", {"x_subdivisions": 20, "y_subdivisions": 20}),
    "sphere": ("mesh.primitive_uv_sphere_add", {"segments": 32, "ring_count": 16}),
    "icosphere": ("mesh.primitive_ico_sphere_add", {"subdivisions": 2}),
    "cylinder": ("mesh.primitive_cylinder_add", {"vertices": 32}),
    "cone": ("mesh.primitive_cone_add", {"vertices": 32}),
    "torus": ("mesh.primitive_torus_add", {}),
    "circle": ("mesh.primitive_circle_add", {"vertices": 32}),
    "monkey": ("mesh.primitive_monkey_add", {}),
}


def _add_primitive(primitive, args):
    entry = _PRIMITIVES.get(primitive)
    if entry is None:
        raise ToolError(
            "blender.primitive_unknown",
            f"Unbekannter Grundkörper '{primitive}'. Erlaubt: {', '.join(sorted(_PRIMITIVES))}.",
        )
    operator_name, extra = entry
    operator = getattr(bpy.ops, operator_name.rsplit(".", 1)[0], None)
    if operator is None:
        raise ToolError("blender.operator_missing", f"Operator {operator_name} fehlt in dieser Blender-Version.")
    func = getattr(operator, operator_name.split(".", 1)[1])
    kwargs = dict(extra)
    location = _as_vector(args.get("location"), [0.0, 0.0, 0.0], "location")
    func(location=location, **kwargs)
    obj = bpy.context.view_layer.objects.active
    if obj is None:
        raise ToolError("blender.operator_failed", f"{operator_name} hat kein Objekt erzeugt.")
    return obj


def op_object_create(args, context):
    _mode_object()
    primitive = _as_str(args, "primitive", "cube").lower()
    obj = _add_primitive(primitive, args)
    name = _as_str(args, "name")
    if name:
        obj.name = name
    rotation = args.get("rotationDegrees")
    if rotation is not None:
        obj.rotation_euler = _degrees_to_radians(_as_vector(rotation, [0, 0, 0], "rotationDegrees"))
    scale = args.get("scale")
    if scale is not None:
        if isinstance(scale, (int, float)):
            obj.scale = (float(scale),) * 3
        else:
            obj.scale = _as_vector(scale, [1, 1, 1], "scale")
    collection = _collection(_as_str(args, "collection"))
    _link_to(obj, collection)
    return {
        "object": obj.name,
        "primitive": primitive,
        "location": [round(value, 4) for value in obj.location],
    }


def op_object_duplicate(args, context):
    _mode_object()
    sources = _active_or_named(args)
    copies = []
    for source in sources:
        copy = source.copy()
        if source.data is not None:
            copy.data = source.data.copy()
        copy.name = f"{source.name}.kopie"
        bpy.context.scene.collection.objects.link(copy)
        copies.append(copy.name)
    return {"duplicated": copies}


def op_object_delete(args, context):
    _mode_object()
    names = _names_of(args, "name", "names", "objects")
    if not names:
        raise ToolError("blender.object_missing", "object.delete braucht 'name' oder 'names'.")
    removed = []
    for name in names:
        obj = _object(name)
        copies = obj.users_object if hasattr(obj, "users_object") else 0
        bpy.data.objects.remove(obj, do_unlink=True)
        removed.append({"object": name, "otherUsers": int(copies or 0)})
    return {"deleted": [item["object"] for item in removed], "details": removed}


def op_object_rename(args, context):
    obj = _object(_as_str(args, "name"))
    new_name = _as_str(args, "newName")
    if not new_name:
        raise ToolError("blender.argument_invalid", "object.rename braucht 'newName'.")
    if bpy.data.objects.get(new_name) is not None:
        raise ToolError("blender.name_taken", f"'{new_name}' wird bereits verwendet.")
    old = obj.name
    obj.name = new_name
    return {"renamed": {old: new_name}}


def op_object_transform(args, context):
    targets = _active_or_named(args)
    changes = []
    for obj in targets:
        before = {
            "location": [round(value, 4) for value in obj.location],
            "rotationDegrees": _vector_to_degrees(obj.rotation_euler),
            "scale": [round(value, 4) for value in obj.scale],
        }
        if args.get("location") is not None:
            obj.location = _as_vector(args.get("location"), list(obj.location), "location")
        if args.get("translate") is not None:
            obj.location = obj.location + mathutils.Vector(
                _as_vector(args.get("translate"), [0, 0, 0], "translate")
            )
        if args.get("rotationDegrees") is not None:
            obj.rotation_euler = _degrees_to_radians(
                _as_vector(args.get("rotationDegrees"), [0, 0, 0], "rotationDegrees")
            )
        if args.get("rotateDegrees") is not None:
            delta = _degrees_to_radians(_as_vector(args.get("rotateDegrees"), [0, 0, 0], "rotateDegrees"))
            current = [obj.rotation_euler.x, obj.rotation_euler.y, obj.rotation_euler.z]
            obj.rotation_euler = (current[0] + delta[0], current[1] + delta[1], current[2] + delta[2])
        if args.get("scale") is not None:
            scale = args.get("scale")
            if isinstance(scale, (int, float)):
                obj.scale = (float(scale),) * 3
            else:
                obj.scale = _as_vector(scale, [1, 1, 1], "scale")
        if args.get("scaleBy") is not None:
            factor = _as_float(args.get("scaleBy"), 1.0, "scaleBy")
            obj.scale = (obj.scale.x * factor, obj.scale.y * factor, obj.scale.z * factor)
        changes.append(
            {
                "object": obj.name,
                "before": before,
                "after": {
                    "location": [round(value, 4) for value in obj.location],
                    "rotationDegrees": _vector_to_degrees(obj.rotation_euler),
                    "scale": [round(value, 4) for value in obj.scale],
                },
            }
        )
    return {"transformed": changes}


def op_object_parent(args, context):
    child = _object(_as_str(args, "name"))
    parent = _object(_as_str(args, "parent"))
    keep_transform = _as_bool(args.get("keepTransform"), True)
    world_matrix = child.matrix_world.copy()
    child.parent = parent
    if keep_transform:
        child.matrix_world = world_matrix
    return {"parent": {child.name: parent.name}, "keepTransform": keep_transform}


def op_object_visible(args, context):
    targets = _active_or_named(args)
    result = []
    for obj in targets:
        if args.get("hideViewport") is not None:
            obj.hide_viewport = _as_bool(args.get("hideViewport"), False)
            obj.hide_set(_as_bool(args.get("hideViewport"), False))
        if args.get("hideRender") is not None:
            obj.hide_render = _as_bool(args.get("hideRender"), False)
        if args.get("visible") is not None:
            visible = _as_bool(args.get("visible"), True)
            obj.hide_viewport = not visible
            obj.hide_render = not visible
            obj.hide_set(not visible)
        if _as_bool(args.get("select"), False):
            obj.select_set(True)
        result.append(
            {
                "object": obj.name,
                "hideViewport": bool(obj.hide_viewport or obj.hide_get()),
                "hideRender": bool(obj.hide_render),
            }
        )
    return {"visibility": result}


def op_object_shade(args, context):
    _mode_object()
    smooth = _as_str(args, "smooth", "smooth").lower() in ("smooth", "glatt", "true", "1", "ja")
    targets = _active_or_named(args)
    names = []
    for obj in targets:
        if obj.type not in {"MESH", "CURVE"}:
            continue
        for polygon in getattr(obj.data, "polygons", []):
            polygon.use_smooth = smooth
        names.append(obj.name)
    return {"shaded": names, "smooth": smooth}


def op_keyframe_insert(args, context):
    frame = int(_as_float(args.get("frame"), bpy.context.scene.frame_current, "frame"))
    bpy.context.scene.frame_set(frame)
    targets = _active_or_named(args)
    channels = [str(item) for item in (args.get("channels") or ["location", "rotation_euler", "scale"])]
    inserted = []
    for obj in targets:
        for channel in channels:
            try:
                obj.keyframe_insert(data_path=channel, frame=frame)
                inserted.append(f"{obj.name}.{channel}")
            except (RuntimeError, TypeError) as error:
                raise ToolError("blender.keyframe_failed", f"keyframe für {obj.name}.{channel}: {error}")
    return {"keyframed": inserted, "frame": frame}


# --------------------------------------------------------------------------- #
# materials
# --------------------------------------------------------------------------- #

def _ensure_principled(material):
    material.use_nodes = True
    tree = material.node_tree
    node = next((item for item in tree.nodes if item.type == "BSDF_PRINCIPLED"), None)
    if node is None:
        node = tree.nodes.new("ShaderNodeBsdfPrincipled")
        output = next((item for item in tree.nodes if item.type == "OUTPUT_MATERIAL"), None)
        if output is None:
            output = tree.nodes.new("ShaderNodeOutputMaterial")
        tree.links.new(node.outputs["BSDF"], output.inputs["Surface"])
    return node


def _set_socket(node, names, value, label):
    for socket_name in names:
        socket = node.inputs.get(socket_name)
        if socket is None:
            continue
        if isinstance(value, (list, tuple)):
            socket.default_value = list(value)
        else:
            socket.default_value = float(value)
        return True
    raise ToolError("blender.material_socket_missing", f"Shader-Eingang für {label} fehlt in dieser Version.")


def _apply_material_look(material, args):
    node = _ensure_principled(material)
    if args.get("baseColor") is not None:
        color = _as_vector(args.get("baseColor"), [0.8, 0.8, 0.8, 1.0], "baseColor", size=4)
        _set_socket(node, ["Base Color", "Base Color"], color, "baseColor")
        material.diffuse_color = color
    if args.get("metallic") is not None:
        _set_socket(node, ["Metallic"], _as_float(args.get("metallic"), 0.0, "metallic"), "metallic")
    if args.get("roughness") is not None:
        _set_socket(node, ["Roughness"], _as_float(args.get("roughness"), 0.5, "roughness"), "roughness")
    if args.get("emissionColor") is not None:
        color = _as_vector(args.get("emissionColor"), [1, 1, 1, 1], "emissionColor", size=4)
        _set_socket(node, ["Emission Color", "Emission"], color, "emissionColor")
    if args.get("emissionStrength") is not None:
        _set_socket(
            node,
            ["Emission Strength"],
            _as_float(args.get("emissionStrength"), 0.0, "emissionStrength"),
            "emissionStrength",
        )
    if args.get("alpha") is not None:
        _set_socket(node, ["Alpha"], _as_float(args.get("alpha"), 1.0, "alpha"), "alpha")


def op_material_create(args, context):
    name = _as_str(args, "name")
    if not name:
        raise ToolError("blender.argument_invalid", "material.create braucht 'name'.")
    material = bpy.data.materials.get(name)
    created = material is None
    if created:
        material = bpy.data.materials.new(name)
    _apply_material_look(material, args)
    result = {"material": material.name, "created": created}
    targets = _names_of(args, "assignTo", "object", "name2")
    if targets:
        assigned = []
        for target_name in targets:
            obj = _object(target_name)
            _assign_material(obj, material, _as_bool(args.get("replace"), True))
            assigned.append(obj.name)
        result["assignedTo"] = assigned
    return result


def _assign_material(obj, material, replace):
    if replace or not obj.material_slots:
        obj.data.materials.clear()
        obj.data.materials.append(material)
        return
    used = [slot.material for slot in obj.material_slots]
    if material in used:
        return
    obj.data.materials.append(material)


def op_material_assign(args, context):
    material_name = _as_str(args, "material")
    if not material_name:
        raise ToolError("blender.argument_invalid", "material.assign braucht 'material'.")
    material = bpy.data.materials.get(material_name)
    if material is None:
        if not _as_bool(args.get("createIfMissing"), True):
            raise ToolError("blender.material_missing", f"Material '{material_name}' existiert nicht.")
        material = bpy.data.materials.new(material_name)
        _apply_material_look(material, args)
    targets = _active_or_named(args)
    assigned = []
    for obj in targets:
        if obj.type not in {"MESH", "CURVE", "SURFACE", "FONT"}:
            continue
        _assign_material(obj, material, _as_bool(args.get("replace"), True))
        assigned.append(obj.name)
    return {"assigned": assigned, "material": material.name}


def op_material_update(args, context):
    name = _as_str(args, "material") or _as_str(args, "name")
    material = bpy.data.materials.get(name)
    if material is None:
        known = ", ".join(sorted(item.name for item in bpy.data.materials)[:20])
        raise ToolError(
            "blender.material_missing",
            f"Material '{name}' existiert nicht. Vorhanden: {known or 'keine'}.",
        )
    _apply_material_look(material, args)
    return {
        "material": material.name,
        "baseColor": [round(value, 4) for value in material.diffuse_color],
        "metallic": round(float(getattr(material, "metallic", 0.0)), 4),
        "roughness": round(float(getattr(material, "roughness", 0.5)), 4),
    }


# --------------------------------------------------------------------------- #
# camera, lights, world, render settings
# --------------------------------------------------------------------------- #

def _camera_targets():
    return [obj for obj in bpy.data.objects if obj.type == "CAMERA"]


def op_camera_create(args, context):
    _mode_object()
    name = _as_str(args, "name", "Missum Kamera")
    camera_data = bpy.data.cameras.new(name)
    obj = bpy.data.objects.new(name, camera_data)
    bpy.context.scene.collection.objects.link(obj)
    obj.location = _as_vector(args.get("location"), [7.0, -7.0, 5.0], "location")
    if args.get("lookAt") is not None:
        _aim_camera(obj, _as_vector(args.get("lookAt"), [0, 0, 0], "lookAt"))
    else:
        obj.rotation_euler = _degrees_to_radians(
            _as_vector(args.get("rotationDegrees"), [63.0, 0.0, 45.0], "rotationDegrees")
        )
    lens = _as_float(args.get("lens"), None, "lens")
    if lens is not None:
        camera_data.lens = lens
    if _as_bool(args.get("setAsActive"), True):
        bpy.context.scene.camera = obj
    return {
        "camera": obj.name,
        "active": bpy.context.scene.camera.name if bpy.context.scene.camera else None,
        "lens": round(float(camera_data.lens), 3),
    }


def _aim_camera(camera, target):
    direction = mathutils.Vector(target) - camera.location
    if direction.length < 1e-6:
        raise ToolError("blender.argument_invalid", "lookAt liegt genau auf der Kameraposition.")
    camera.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()


def op_camera_set(args, context):
    name = _as_str(args, "name")
    if name:
        camera = _object(name)
        if camera.type != "CAMERA":
            raise ToolError("blender.not_a_camera", f"'{name}' ist keine Kamera.")
    else:
        camera = bpy.context.scene.camera
        if camera is None:
            cameras = _camera_targets()
            if not cameras:
                raise ToolError(
                    "blender.no_camera",
                    "Die Szene hat keine Kamera. Erst camera.create verwenden.",
                )
            camera = cameras[0]
    if args.get("location") is not None:
        camera.location = _as_vector(args.get("location"), list(camera.location), "location")
    if args.get("lookAt") is not None:
        target = args.get("lookAt")
        if isinstance(target, str):
            other = _object(target)
            target = list(other.matrix_world.translation)
        _aim_camera(camera, _as_vector(target, [0, 0, 0], "lookAt"))
    elif args.get("rotationDegrees") is not None:
        camera.rotation_euler = _degrees_to_radians(
            _as_vector(args.get("rotationDegrees"), [0, 0, 0], "rotationDegrees")
        )
    if args.get("lens") is not None:
        camera.data.lens = _as_float(args.get("lens"), camera.data.lens, "lens")
    if args.get("clipEnd") is not None:
        camera.data.clip_end = _as_float(args.get("clipEnd"), camera.data.clip_end, "clipEnd")
    dof = args.get("depthOfField")
    if isinstance(dof, dict):
        camera.data.dof.use_dof = _as_bool(dof.get("enabled"), True)
        if dof.get("focusDistance") is not None:
            camera.data.dof.focus_distance = _as_float(dof.get("focusDistance"), 1.0, "focusDistance")
        focus_name = _as_str(dof, "focusObject")
        if focus_name:
            camera.data.dof.focus_object = _object(focus_name)
        if dof.get("apertureFStop") is not None:
            camera.data.dof.aperture_fstop = _as_float(dof.get("apertureFStop"), 2.8, "apertureFStop")
    if _as_bool(args.get("setAsActive"), True):
        bpy.context.scene.camera = camera
    return {
        "camera": camera.name,
        "location": [round(value, 4) for value in camera.location],
        "rotationDegrees": _vector_to_degrees(camera.rotation_euler),
        "lens": round(float(camera.data.lens), 3),
        "active": bpy.context.scene.camera.name if bpy.context.scene.camera else None,
    }


_LIGHT_TYPES = {"point", "sun", "spot", "area"}


def op_light_create(args, context):
    _mode_object()
    light_type = _as_str(args, "lightType", "point").lower()
    if light_type not in _LIGHT_TYPES:
        raise ToolError(
            "blender.argument_invalid",
            f"Unbekannter Lichttyp '{light_type}'. Erlaubt: {', '.join(sorted(_LIGHT_TYPES))}.",
        )
    name = _as_str(args, "name", f"Licht {light_type}")
    light_data = bpy.data.lights.new(name, light_type.upper())
    obj = bpy.data.objects.new(name, light_data)
    bpy.context.scene.collection.objects.link(obj)
    obj.location = _as_vector(args.get("location"), [4.0, -4.0, 6.0], "location")
    if args.get("energy") is not None:
        light_data.energy = _as_float(args.get("energy"), light_data.energy, "energy")
    if args.get("color") is not None:
        color = _as_vector(args.get("color"), [1, 1, 1], "color", size=3)
        light_data.color = color
    if args.get("size") is not None:
        size = _as_float(args.get("size"), 1.0, "size")
        if hasattr(light_data, "size"):
            light_data.size = size
    if args.get("angle") is not None:
        angle = _as_float(args.get("angle"), 45.0, "angle")
        if hasattr(light_data, "spot_size"):
            light_data.spot_size = math.radians(angle)
    if args.get("lookAt") is not None:
        direction = mathutils.Vector(_as_vector(args.get("lookAt"), [0, 0, 0], "lookAt")) - obj.location
        if direction.length > 1e-6:
            obj.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    return {
        "light": obj.name,
        "lightType": light_type,
        "energy": round(float(light_data.energy), 3),
    }


def op_world_set(args, context):
    world = bpy.context.scene.world
    if world is None:
        world = bpy.data.worlds.new("World")
        bpy.context.scene.world = world
    world.use_nodes = True
    tree = world.node_tree
    background = next((node for node in tree.nodes if node.type == "BACKGROUND"), None)
    if background is None:
        background = tree.nodes.new("ShaderNodeBackground")
        output = next((node for node in tree.nodes if node.type == "OUTPUT_WORLD"), None)
        if output is None:
            output = tree.nodes.new("ShaderNodeOutputWorld")
        tree.links.new(background.outputs["Background"], output.inputs["Surface"])
    if args.get("color") is not None:
        color = _as_vector(args.get("color"), [0.05, 0.05, 0.05, 1.0], "color", size=4)
        background.inputs["Color"].default_value = color
    if args.get("strength") is not None:
        background.inputs["Strength"].default_value = _as_float(args.get("strength"), 1.0, "strength")
    return {
        "world": world.name,
        "color": [round(float(value), 4) for value in background.inputs["Color"].default_value],
        "strength": round(float(background.inputs["Strength"].default_value), 4),
    }


_RENDER_ENGINES = {
    "workbench": ["BLENDER_WORKBENCH"],
    "eevee": ["BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"],
    "cycles": ["CYCLES"],
}


def _available_engines():
    """Engine identifiers as this Blender build reports them (property moved between versions)."""
    for owner in (bpy.context.scene.render, bpy.context.scene):
        properties = owner.bl_rna.properties
        try:
            if "engine" not in properties:
                continue
            return {item.identifier for item in properties["engine"].enum_items}
        except (KeyError, RuntimeError, TypeError):
            continue
    return set()


def _resolve_engine(name):
    candidates = _RENDER_ENGINES.get(str(name).lower(), [])
    if not candidates:
        raise ToolError(
            "blender.engine_unavailable",
            f"Unbekannte Render-Engine '{name}'. Erlaubt: {', '.join(sorted(_RENDER_ENGINES))}.",
        )
    valid = _available_engines()
    for candidate in candidates:
        if candidate in valid:
            return candidate
    # Engines are extensions and the reported enum can lag behind, so ask Blender itself by
    # setting and immediately restoring the value. Blender 5.2 accepts the identifiers this way.
    current = bpy.context.scene.render.engine
    for candidate in candidates:
        try:
            bpy.context.scene.render.engine = candidate
            bpy.context.scene.render.engine = current
            return candidate
        except (RuntimeError, TypeError):
            continue
    raise ToolError(
        "blender.engine_unavailable",
        f"Render-Engine '{name}' ist hier nicht verfügbar. Erlaubt: {', '.join(sorted(valid))}.",
    )


def op_render_set(args, context):
    render = bpy.context.scene.render
    applied = {}
    if args.get("engine") is not None:
        engine = _resolve_engine(args.get("engine"))
        render.engine = engine
        applied["engine"] = engine
    if args.get("resolutionX") is not None:
        render.resolution_x = int(_as_float(args.get("resolutionX"), render.resolution_x, "resolutionX"))
        applied["resolutionX"] = render.resolution_x
    if args.get("resolutionY") is not None:
        render.resolution_y = int(_as_float(args.get("resolutionY"), render.resolution_y, "resolutionY"))
        applied["resolutionY"] = render.resolution_y
    if args.get("resolutionPercentage") is not None:
        render.resolution_percentage = int(
            _as_float(args.get("resolutionPercentage"), render.resolution_percentage, "resolutionPercentage")
        )
        applied["resolutionPercentage"] = render.resolution_percentage
    if args.get("filmTransparent") is not None:
        render.film_transparent = _as_bool(args.get("filmTransparent"), False)
        applied["filmTransparent"] = render.film_transparent
    if args.get("samples") is not None:
        samples = max(1, int(_as_float(args.get("samples"), 32, "samples")))
        # Sample counts live on the engine settings, which differ per engine and version.
        for attribute in ("cycles", "eevee"):
            settings = getattr(bpy.context.scene, attribute, None)
            if settings is None:
                continue
            for name in ("samples", "taa_render_samples"):
                if hasattr(settings, name):
                    try:
                        setattr(settings, name, samples)
                        applied[f"{attribute}.{name}"] = samples
                    except (RuntimeError, TypeError):
                        pass
    if args.get("fps") is not None:
        bpy.context.scene.render.fps = int(_as_float(args.get("fps"), render.fps, "fps"))
        applied["fps"] = render.fps
    return {"renderSettings": applied}


def op_scene_frame_set(args, context):
    frame = int(_as_float(args.get("frame"), bpy.context.scene.frame_current, "frame"))
    bpy.context.scene.frame_set(frame)
    if args.get("frameStart") is not None:
        bpy.context.scene.frame_start = int(_as_float(args.get("frameStart"), 1, "frameStart"))
    if args.get("frameEnd") is not None:
        bpy.context.scene.frame_end = int(_as_float(args.get("frameEnd"), 250, "frameEnd"))
    return {"frame": bpy.context.scene.frame_current}


def op_collection_create(args, context):
    name = _as_str(args, "name")
    if not name:
        raise ToolError("blender.argument_invalid", "collection.create braucht 'name'.")
    collection = bpy.data.collections.get(name)
    created = collection is None
    if created:
        collection = bpy.data.collections.new(name)
        parent_name = _as_str(args, "parentCollection")
        parent = _collection(parent_name) if parent_name else bpy.context.scene.collection
        parent.children.link(collection)
    move_names = _names_of(args, "objects", "names")
    moved = []
    for object_name in move_names:
        obj = _object(object_name)
        _link_to(obj, collection)
        moved.append(obj.name)
    return {"collection": collection.name, "created": created, "moved": moved}


def op_modifier_add(args, context):
    modifier_type = _as_str(args, "type", "subsurf").lower()
    mapping = {
        "subsurf": "SUBSURF",
        "bevel": "BEVEL",
        "solidify": "SOLIDIFY",
        "mirror": "MIRROR",
        "array": "ARRAY",
        "decimate": "DECIMATE",
        "smooth": "SMOOTH",
    }
    identifier = mapping.get(modifier_type)
    if identifier is None:
        raise ToolError(
            "blender.modifier_unknown",
            f"Unbekannter Modifikator '{modifier_type}'. Erlaubt: {', '.join(sorted(mapping))}.",
        )
    targets = _active_or_named(args)
    added = []
    for obj in targets:
        modifier = obj.modifiers.new(name=modifier_type.capitalize(), type=identifier)
        settings = args.get("settings") or {}
        if isinstance(settings, dict):
            for key, value in settings.items():
                if key in {"path", "file", "filePath", "folder", "directory", "command"}:
                    raise ToolError(
                        "blender.argument_invalid",
                        f"Modifikator-Eigenschaft '{key}' ist aus Sicherheitsgründen nicht erlaubt.",
                    )
                if hasattr(modifier, key):
                    try:
                        setattr(modifier, key, value)
                    except (RuntimeError, TypeError) as error:
                        raise ToolError(
                            "blender.modifier_setting_failed",
                            f"{modifier_type}.{key}: {error}",
                        )
        added.append({"object": obj.name, "modifier": modifier.name, "type": identifier})
    return {"modifiers": added}


# --------------------------------------------------------------------------- #
# project handling
# --------------------------------------------------------------------------- #

def _existing_backups(blend_path):
    """Lists backup siblings such as scene.blend1 so they can be reported."""
    import os

    if not blend_path:
        return []
    directory = os.path.dirname(blend_path) or "."
    base = os.path.basename(blend_path)
    try:
        names = os.listdir(directory)
    except OSError:
        return []
    return sorted(
        name for name in names
        if name.startswith(base) and name != base and name[len(base):].strip(".").isdigit()
    )


def _save_project(save_as_path=None):
    """Saves to the current project file without creating a new .blend1 backup.

    Blender's own backup preference is lowered for the save and restored to its
    previous value afterwards, so the user's setting is untouched.  Existing
    backups stay on disk.
    """
    import os

    filepaths = bpy.context.preferences.filepaths
    previous_versions = getattr(filepaths, "save_version", None)
    target = os.path.abspath(os.path.expanduser(save_as_path)) if save_as_path else (bpy.data.filepath or "")
    if not target:
        raise ToolError(
            "blender.no_project_path",
            "Die Szene hat noch keinen Projektpfad. project.saveAs mit 'projectFile' "
            "(absoluter Pfad auf die .blend-Datei) zuerst ausführen.",
        )
    if not target.lower().endswith(".blend"):
        target = target + ".blend"

    directory = os.path.dirname(target)
    if directory and not os.path.isdir(directory):
        try:
            os.makedirs(directory, exist_ok=True)
        except OSError as error:
            raise ToolError("blender.save_failed", f"Ordner nicht anlegbar: {error}")

    backups_before = _existing_backups(target)
    try:
        filepaths.save_version = 0
        if save_as_path:
            result = bpy.ops.wm.save_mainfile(filepath=target)
        else:
            result = bpy.ops.wm.save_mainfile()
    except (RuntimeError, TypeError) as error:
        raise ToolError("blender.save_failed", f"Speichern fehlgeschlagen: {error}")
    finally:
        # Always put the user's own backup setting back.
        if previous_versions is not None:
            try:
                filepaths.save_version = previous_versions
            except (RuntimeError, TypeError):
                pass

    if str(result) != "{'FINISHED'}":
        raise ToolError("blender.save_failed", f"Blender meldete beim Speichern: {result}")

    written_backups = [name for name in _existing_backups(target) if name not in backups_before]
    return {
        "saved": True,
        "savedTo": os.path.abspath(bpy.data.filepath or target),
        "backupPreference": {"before": previous_versions, "used": 0, "restored": previous_versions},
        "newBackupFiles": written_backups,
        "existingBackupFiles": _existing_backups(target),
    }


def op_project_save_as(args, context):
    target = _as_str(args, "projectFile") or _as_str(args, "blendFile")
    if not target:
        raise ToolError(
            "blender.argument_invalid",
            "project.saveAs braucht 'projectFile' mit dem absoluten Pfad der .blend-Datei.",
        )
    saved = _save_project(target)
    saved["renamedProject"] = True
    return saved


def op_project_open(args, context):
    target = _as_str(args, "projectFile")
    if not target:
        raise ToolError("blender.argument_invalid", "project.open braucht 'projectFile'.")
    import os

    target = os.path.abspath(os.path.expanduser(target))
    if not os.path.isfile(target):
        raise ToolError("blender.project_missing", f"Diese Projektdatei existiert nicht: {target}")
    try:
        result = bpy.ops.wm.open_mainfile(filepath=target)
    except (RuntimeError, TypeError) as error:
        raise ToolError("blender.project_open_failed", f"Öffnen fehlgeschlagen: {error}")
    if str(result) != "{'FINISHED'}":
        raise ToolError("blender.project_open_failed", f"Blender meldete beim Öffnen: {result}")
    return {
        "opened": target,
        "sceneName": bpy.context.scene.name if bpy.context.scene else None,
        "note": "Sitzungsmarke geändert; der Adapter bindet die Szene neu ein.",
    }


# --------------------------------------------------------------------------- #
# view.capture
# --------------------------------------------------------------------------- #

def _scene_bounds_center():
    points = []
    for obj in bpy.data.objects:
        if obj.type in {"MESH", "CURVE", "SURFACE", "FONT"} and not obj.hide_render:
            for corner in obj.bound_box:
                points.append(obj.matrix_world @ mathutils.Vector(corner))
    if not points:
        for obj in bpy.data.objects:
            points.append(obj.matrix_world.translation)
    if not points:
        return mathutils.Vector((0.0, 0.0, 0.0)), 3.0
    center = mathutils.Vector((0.0, 0.0, 0.0))
    for point in points:
        center += point
    center /= len(points)
    radius = max((point - center).length for point in points) or 2.0
    return center, radius


def _temporary_camera(center, radius):
    """Adds a camera that frames the scene; it is removed right after the shot."""
    camera_data = bpy.data.cameras.new("Missum Ansichts-Kamera")
    camera = bpy.data.objects.new("Missum Ansichts-Kamera", camera_data)
    bpy.context.scene.collection.objects.link(camera)
    distance = max(radius * 2.6, 1.5)
    direction = mathutils.Vector((1.0, -1.0, 0.65)).normalized()
    camera.location = center + direction * distance
    camera.rotation_euler = (center - camera.location).to_track_quat("-Z", "Y").to_euler()
    camera_data.lens = 50.0
    return camera


def _viewport_window_and_area():
    """Window, 3D view area and its window region for a viewport dump."""
    for window in bpy.context.window_manager.windows:
        screen = window.screen
        if screen is None:
            continue
        for area in screen.areas:
            if area.type != "VIEW_3D":
                continue
            region = next((item for item in area.regions if item.type == "WINDOW"), area.regions[-1])
            return window, area, region
    return None, None, None


def view_capture(args, context):
    view = _as_str(args, "view", "viewport").lower()
    if view not in ("viewport", "render"):
        view = "viewport"
    engine_name = _as_str(args, "engine", "workbench").lower()
    width = int(_as_float(args.get("width"), 1280, "width"))
    height = int(_as_float(args.get("height"), 720, "height"))
    use_camera = _as_bool(args.get("cameraView"), False)
    target = _as_str(args, "targetImage")
    if not target:
        raise ToolError("blender.argument_invalid", "view.capture braucht 'targetImage' (PNG-Pfad).")

    import os

    target = os.path.abspath(os.path.expanduser(target))
    directory = os.path.dirname(target)
    if directory and not os.path.isdir(directory):
        try:
            os.makedirs(directory, exist_ok=True)
        except OSError as error:
            raise ToolError("blender.capture_failed", f"Bildordner nicht anlegbar: {error}")
    if os.path.exists(target):
        try:
            os.remove(target)
        except OSError as error:
            raise ToolError("blender.capture_failed", f"Alte Bilddatei nicht ersetzbar: {error}")

    scene = bpy.context.scene
    render = scene.render
    previous = {
        "engine": render.engine,
        "filepath": render.filepath,
        "resolution_x": render.resolution_x,
        "resolution_y": render.resolution_y,
        "resolution_percentage": render.resolution_percentage,
        "file_format": render.image_settings.file_format,
        "camera": scene.camera.name if scene.camera else None,
    }
    temp_camera = None
    method = None
    try:
        render.image_settings.file_format = "PNG"
        render.resolution_x = width
        render.resolution_y = height
        render.resolution_percentage = 100
        render.filepath = target

        if use_camera and scene.camera is None:
            center, radius = _scene_bounds_center()
            temp_camera = _temporary_camera(center, radius)
            scene.camera = temp_camera

        viewport_attempt = "skipped: background mode" if bpy.app.background else "no 3D view found"
        if view == "viewport" and not bpy.app.background:
            window, area, region = _viewport_window_and_area()
            if window is not None and area is not None and region is not None:
                try:
                    with bpy.context.temp_override(window=window, area=area, region=region, space=area.spaces.active):
                        result = bpy.ops.render.opengl(write_still=True, view_context="SCREEN")
                    viewport_attempt = str(result)
                    if str(result) == "{'FINISHED'}" and os.path.exists(target):
                        method = "viewport.opengl"
                except (RuntimeError, TypeError) as error:
                    viewport_attempt = f"failed: {error}"

        if method is None:
            render.engine = _resolve_engine("workbench" if view == "viewport" else engine_name)
            result = bpy.ops.render.render(write_still=True)
            if str(result) != "{'FINISHED'}":
                raise ToolError("blender.capture_failed", f"Render meldete: {result}")
            if not os.path.exists(target):
                # Blender sometimes appends the frame number; adopt whatever was written.
                candidates = [
                    name for name in os.listdir(directory or ".")
                    if name.startswith(os.path.basename(target).rsplit(".", 1)[0]) and name.endswith(".png")
                ]
                if len(candidates) == 1:
                    adopted = os.path.join(directory or ".", candidates[0])
                    if adopted != target:
                        os.replace(adopted, target)
            if not os.path.exists(target):
                raise ToolError("blender.capture_failed", f"Es wurde kein Bild geschrieben: {target}")
            method = "render.workbench" if view == "viewport" else f"render.{render.engine}"

        size = os.path.getsize(target)
        if size == 0:
            raise ToolError("blender.capture_failed", "Die Bilddatei ist leer geblieben.")
        return {
            "captured": True,
            "method": method,
            "viewportAttempt": viewport_attempt,
            "view": view,
            "engine": render.engine,
            "width": int(render.resolution_x),
            "height": int(render.resolution_y),
            "camera": scene.camera.name if scene.camera else None,
            "cameraWasTemporary": temp_camera is not None,
            "sceneName": scene.name,
            "projectPath": bpy.data.filepath or "",
            "unsavedChanges": bool(bpy.data.is_dirty),
            "renderedFileBytes": size,
        }
    finally:
        if temp_camera is not None:
            try:
                bpy.data.objects.remove(temp_camera, do_unlink=True)
            except (RuntimeError, TypeError):
                pass
        render.engine = previous["engine"]
        render.filepath = previous["filepath"]
        render.resolution_x = previous["resolution_x"]
        render.resolution_y = previous["resolution_y"]
        render.resolution_percentage = previous["resolution_percentage"]
        render.image_settings.file_format = previous["file_format"]
        if previous["camera"]:
            restore = bpy.data.objects.get(previous["camera"])
            if restore is not None:
                scene.camera = restore


# --------------------------------------------------------------------------- #
# custom python (escape hatch for later modelling tasks)
# --------------------------------------------------------------------------- #

def custom_python(args, context, allow):
    if not allow:
        raise ToolError(
            "blender.python_disabled",
            "custom.python ist im Add-on deaktiviert. In den Add-on-Einstellungen "
            "'Python-Ausführung erlauben' aktivieren oder die Änderung mit den "
            "vorhandenen Operationen ausführen.",
        )
    code = _as_str(args, "code")
    if not code.strip():
        raise ToolError("blender.argument_invalid", "custom.python braucht 'code'.")
    for forbidden in ("subprocess", "os.system", "shutil.rmtree", "webbrowser"):
        if forbidden in code:
            raise ToolError(
                "blender.python_blocked",
                f"Der Code enthält '{forbidden}'. Prozess- und Dateisystem-Zerstörung ist nicht erlaubt.",
            )
    objects_before = {obj.name for obj in bpy.data.objects}
    namespace = {"bpy": bpy, "mathutils": mathutils, "result": None}
    try:
        exec(compile(code, "<missum>", "exec"), namespace)  # noqa: S102 - local trusted bridge
    except ToolError:
        raise
    except Exception as error:  # noqa: BLE001 - the model needs the real reason
        raise ToolError("blender.python_failed", f"Der Code lief nicht: {error!r}") from error

    value = namespace.get("result")
    payload = value if isinstance(value, dict) else {"text": str(value) if value is not None else ""}
    answer = {
        "pythonResult": payload,
        "sceneName": bpy.context.scene.name,
        "projectPath": bpy.data.filepath or "",
        "unsavedChanges": bool(bpy.data.is_dirty),
        "newObjects": sorted({obj.name for obj in bpy.data.objects} - objects_before),
        "changedScene": bool(bpy.data.is_dirty),
    }
    if _as_bool(args.get("save"), False):
        answer["saved"] = _save_project()
    return answer


# --------------------------------------------------------------------------- #
# dispatcher
# --------------------------------------------------------------------------- #

OPERATIONS = {
    "object.create": op_object_create,
    "object.duplicate": op_object_duplicate,
    "object.delete": op_object_delete,
    "object.rename": op_object_rename,
    "object.transform": op_object_transform,
    "object.parent": op_object_parent,
    "object.visible": op_object_visible,
    "object.shade": op_object_shade,
    "keyframe.insert": op_keyframe_insert,
    "material.create": op_material_create,
    "material.assign": op_material_assign,
    "material.update": op_material_update,
    "camera.create": op_camera_create,
    "camera.set": op_camera_set,
    "light.create": op_light_create,
    "world.set": op_world_set,
    "render.set": op_render_set,
    "scene.frame.set": op_scene_frame_set,
    "collection.create": op_collection_create,
    "modifier.add": op_modifier_add,
    "project.saveAs": op_project_save_as,
    "project.open": op_project_open,
}


def scene_edit(args, context):
    operations = args.get("operations")
    if not isinstance(operations, list) or not operations:
        raise ToolError("blender.operations_missing", "scene.edit braucht eine Liste 'operations'.")
    if len(operations) > 80:
        raise ToolError("blender.operations_too_many", "Höchstens 80 Schritte pro Gruppe.")

    operation_id = _as_str(args, "operationId") or _as_str(context, "operationId")
    ledger = context["ledger"]
    if operation_id and operation_id in ledger:
        known = ledger[operation_id]
        return {
            "appliedCount": 0,
            "deduplicatedInBlender": True,
            "outcome": known.get("outcome"),
            "summary": known.get("summary"),
            "results": [],
            "sceneName": bpy.context.scene.name,
            "projectPath": bpy.data.filepath or "",
            "unsavedChanges": bool(bpy.data.is_dirty),
        }

    stop_on_error = _as_bool(args.get("stopOnError"), True)
    results = []
    errors = []
    applied_count = 0
    started_objects = {obj.name for obj in bpy.data.objects}
    for index, step in enumerate(operations):
        if not isinstance(step, dict):
            raise ToolError("blender.argument_invalid", f"Schritt {index + 1} ist kein Objekt.")
        name = _as_str(step, "op")
        handler = OPERATIONS.get(name)
        if handler is None:
            message = (
                f"Unbekannte Operation '{name}'. Erlaubt: {', '.join(sorted(OPERATIONS))} "
                "und custom.python."
            )
            if stop_on_error:
                raise ToolError("blender.operation_unknown", message)
            errors.append({"step": index + 1, "op": name, "error": message})
            continue
        try:
            outcome = handler(step, context)
        except Exception as error:  # noqa: BLE001 - a half-applied group must be reported, not hidden
            if isinstance(error, ToolError):
                code, detail = error.code, error.message
            else:
                code = "blender.operation_failed"
                detail = "".join(traceback.format_exception_only(type(error), error)).strip()
            if stop_on_error:
                if applied_count:
                    # Some steps did land. Say so loudly: the model has to inspect and repair the
                    # scene instead of sending the same group again.
                    partial = (
                        f"{applied_count} von {len(operations)} Schritten wurden schon angewendet, "
                        f"Schritt {index + 1} ({name}) nicht: {detail}. Nicht gespeichert."
                    )
                    if operation_id:
                        ledger[operation_id] = {"outcome": "partial", "summary": partial,
                                                "appliedCount": applied_count}
                    raise ToolError(
                        "blender.edit_partial",
                        partial + " Zustand mit blender.scene.read prüfen und gezielt nachbessern; "
                        "dieselbe operationId nicht erneut senden.",
                    )
                raise ToolError(code, detail)
            errors.append({"step": index + 1, "op": name, "error": detail, "code": code})
            continue
        applied_count += 1
        results.append({"step": index + 1, "op": name, "result": outcome})

    saved = None
    # A project.saveAs step reports its own save (flat report with savedTo); adopt it so the answer
    # carries one clear verdict instead of "not saved".
    for step_result in results:
        inner = step_result.get("result")
        if not isinstance(inner, dict):
            continue
        if inner.get("savedTo"):
            saved = inner
        elif isinstance(inner.get("saved"), dict):
            saved = inner["saved"]
    if _as_bool(args.get("save"), False) and applied_count > 0:
        # A failed save must not turn the applied steps into an unknown outcome: the ledger below
        # stays authoritative, otherwise the same group would be sent and applied twice.
        try:
            saved = _save_project()
        except ToolError as error:
            saved = {
                "saved": False,
                "errorCode": error.code,
                "error": error.message,
                "note": "Die Schritte sind in der offenen Szene angewendet, aber nicht gespeichert.",
            }

    new_objects = sorted({obj.name for obj in bpy.data.objects} - started_objects)
    summary = f"{applied_count} von {len(operations)} Schritten angewendet"
    if saved and saved.get("saved"):
        summary += f", gespeichert nach {saved['savedTo']}"
    elif saved:
        summary += f", Speichern fehlgeschlagen: {saved.get('error')}"
    if errors:
        summary += f", {len(errors)} Schritt(e) abgelehnt"

    if operation_id:
        ledger[operation_id] = {
            "outcome": "applied",
            "summary": summary,
            "appliedCount": applied_count,
        }
        while len(ledger) > 200:
            ledger.pop(next(iter(ledger)))

    return {
        "appliedCount": applied_count,
        "requestedCount": len(operations),
        "results": results,
        "errors": errors,
        "newObjects": new_objects,
        "saved": saved if saved else {"saved": False},
        "sceneName": bpy.context.scene.name,
        "projectPath": bpy.data.filepath or "",
        "unsavedChanges": bool(bpy.data.is_dirty),
        "summary": summary,
        "operationId": operation_id or None,
    }


def operation_status(args, context):
    operation_id = _as_str(args, "operationId") or _as_str(context, "operationId")
    entry = context["ledger"].get(operation_id) if operation_id else None
    if entry is None:
        return {
            "applied": False,
            "known": False,
            "operationId": operation_id or None,
            "sceneName": bpy.context.scene.name,
            "projectPath": bpy.data.filepath or "",
            "unsavedChanges": bool(bpy.data.is_dirty),
            "objectCount": len(bpy.data.objects),
            "hint": "Diese operationId ist dem Add-on nicht bekannt; die Szene wurde damit nicht geändert.",
        }
    return {
        "applied": entry.get("outcome") == "applied",
        "known": True,
        "operationId": operation_id,
        "summary": entry.get("summary"),
        "sceneName": bpy.context.scene.name,
        "projectPath": bpy.data.filepath or "",
        "unsavedChanges": bool(bpy.data.is_dirty),
    }


def handle(cmd, args, operation_id, context):
    """Runs one command in the main thread and returns the reply envelope."""
    args = args if isinstance(args, dict) else {}
    context = dict(context)
    context["operationId"] = operation_id or context.get("operationId", "")
    try:
        if cmd == "scene.read":
            return {"ok": True, "result": scene_read(args, context)}
        if cmd == "scene.edit":
            return {"ok": True, "result": scene_edit(args, context)}
        if cmd == "view.capture":
            return {"ok": True, "result": view_capture(args, context)}
        if cmd == "operation.status":
            return {"ok": True, "result": operation_status(args, context)}
        if cmd == "custom.python":
            return {"ok": True, "result": custom_python(args, context, context.get("allowPython", False))}
        raise ToolError(
            "blender.command_unknown",
            f"Unbekanntes Kommando '{cmd}'. Zulässig: hello, scene.read, scene.edit, view.capture, "
            "operation.status, custom.python.",
        )
    except ToolError as error:
        return {"ok": False, "error": {"code": error.code, "message": error.message}}
    except Exception as error:  # noqa: BLE001 - never kill Blender, report instead
        detail = "".join(traceback.format_exception_only(type(error), error)).strip()
        return {
            "ok": False,
            "error": {
                "code": "blender.internal_error",
                "message": f"Unerwarteter Fehler im Add-on: {detail}",
            },
        }
