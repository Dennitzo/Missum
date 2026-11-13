"""Second real probe: capture ops, engines and the still-write path.

Run without --factory-startup so the engine extensions behave like the user's
Blender. Prints one JSON line prefixed with MISSUM_PROBE2.
"""

import json

import bpy

info = {}
info["version"] = bpy.app.version_string
info["view3d_all"] = sorted(dir(bpy.ops.view3d))
info["render_all"] = sorted(dir(bpy.ops.render))
info["screen_all"] = sorted(name for name in dir(bpy.ops.screen) if "dump" in name or "grab" in name)

render_props = {prop.identifier for prop in bpy.types.RenderSettings.bl_rna.properties}
info["has_resolution_percentage"] = "resolution_percentage" in render_props
info["current_engine"] = bpy.context.scene.render.engine
try:
    info["engine_items"] = [
        item.identifier for item in bpy.context.scene.bl_rna.properties["render_engine"].enum_items
    ]
except Exception as error:
    info["engine_items_error"] = repr(error)

# Does setting an engine identifier work in this build?
for candidate in ("BLENDER_WORKBENCH", "BLENDER_EEVEE_NEXT", "BLENDER_EEVEE", "CYCLES"):
    try:
        bpy.context.scene.render.engine = candidate
        info.setdefault("engine_set_ok", []).append(candidate)
    except Exception as error:
        info.setdefault("engine_set_failed", []).append(f"{candidate}: {error!r}")

info["engine_after"] = bpy.context.scene.render.engine

try:
    info["opengl_op_poll"] = hasattr(bpy.ops.render, "opengl")
except Exception as error:
    info["opengl_op_poll_error"] = repr(error)

# A real still write in background proves the capture path end to end.
try:
    scene = bpy.context.scene
    scene.render.filepath = r"C:\Users\AMD\Documents\GitHub\Missum\tmp\probe2-still.png"
    scene.render.image_settings.file_format = "PNG"
    scene.render.resolution_x = 320
    scene.render.resolution_y = 240
    result = bpy.ops.render.render(write_still=True)
    info["render_result"] = str(result)
except Exception as error:
    info["render_error"] = repr(error)

import os  # noqa: E402

path = r"C:\Users\AMD\Documents\GitHub\Missum\tmp\probe2-still.png"
info["still_exists"] = os.path.exists(path)
info["still_bytes"] = os.path.getsize(path) if info["still_exists"] else 0

# Backup behaviour: does saving create a .blend1 with save_version 0?
blend = r"C:\Users\AMD\Documents\GitHub\Missum\tmp\probe2-scene.blend"
prefs = bpy.context.preferences.filepaths
info["save_version_before"] = prefs.save_version
prefs.save_version = 0
try:
    saved = bpy.ops.wm.save_mainfile(filepath=blend)
    info["save_result"] = str(saved)
except Exception as error:
    info["save_error"] = repr(error)
directory = os.path.dirname(blend)
info["files_after_save"] = sorted(
    name for name in os.listdir(directory) if name.startswith("probe2-scene")
)
info["save_version_after"] = prefs.save_version

print("MISSUM_PROBE2 " + json.dumps(info, ensure_ascii=False, sort_keys=True))
