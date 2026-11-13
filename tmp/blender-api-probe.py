"""Real API probe against the installed Blender (no model, no network).

Answers the open questions for the Missum bridge add-on: legacy bl_info support,
the backup-file preference, available render engines, viewport capture ops and
the dirty/modified flags used for the connection status.
"""

import json

import bpy

info = {}
info["version"] = bpy.app.version_string
info["version_tuple"] = list(bpy.app.version)
info["background"] = bool(bpy.app.background)

scene = bpy.context.scene
info["scene_name"] = scene.name if scene else None
info["blend_path"] = bpy.data.filepath or ""
info["data_has_is_dirty"] = hasattr(bpy.data, "is_dirty")
info["scene_has_is_modified"] = hasattr(scene, "is_modified") if scene else False
info["scene_rna_is_modified"] = (
    "is_modified" in {p.identifier for p in scene.bl_rna.properties} if scene else False
)

filepaths = bpy.context.preferences.filepaths
info["filepaths_save_attrs"] = sorted(
    attr for attr in dir(filepaths)
    if any(word in attr.lower() for word in ("save", "backup", "version", "autopack"))
)
info["save_version"] = getattr(filepaths, "save_version", "MISSING")
info["save_subdir"] = getattr(filepaths, "save_subdir", "MISSING")

info["has_app_timers"] = hasattr(bpy.app, "timers")
info["handlers"] = sorted(
    name for name in dir(bpy.app.handlers)
    if "save" in name or "load" in name
)

try:
    engine_prop = bpy.types.Scene.bl_rna.properties.get("render_engine")
    info["engines"] = [item.identifier for item in engine_prop.enum_items] if engine_prop else []
except Exception as error:  # pragma: no cover - probing only
    info["engines_error"] = repr(error)

try:
    info["render_write_still"] = "write_still" in bpy.types.RenderSettings.bl_rna.properties
    info["view3d_ops"] = sorted(
        op for op in dir(bpy.ops.view3d) if "render" in op or op.startswith("view_")
    )
except Exception as error:  # pragma: no cover - probing only
    info["view_ops_error"] = repr(error)
try:
    info["image_file_formats"] = [
        item.identifier for item in bpy.types.ImageRenderSettings.bl_rna.properties["file_format"].enum_items
    ]
except Exception as error:  # pragma: no cover - probing only
    info["image_file_formats_error"] = repr(error)

try:
    import addon_utils  # noqa: E402

    modules = [mod.__name__ for mod in addon_utils.modules()]
    info["addon_module_count"] = len(modules)
    info["addon_default_module_example"] = modules[:3]
    info["addon_utils_file"] = addon_utils.__file__
except Exception as error:  # pragma: no cover - probing only
    info["addon_utils_error"] = repr(error)

print("MISSUM_PROBE " + json.dumps(info, ensure_ascii=False, sort_keys=True))
