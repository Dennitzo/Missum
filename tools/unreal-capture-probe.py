# SPDX-License-Identifier: MIT
"""Sonde für die Signaturen von Viewport, Szenenfänger und Bildexport in UE 5.8."""

import json
import os

import unreal


OUT = os.path.join(str(unreal.Paths.project_saved_dir()), "Missum", "capture-probe.json")
os.makedirs(os.path.dirname(OUT), exist_ok=True)


def doc(owner, name):
    value = getattr(owner, name, None)
    if value is None:
        return "fehlt"
    return (value.__doc__ or "").strip()[:1200]


report = {
    "engine": unreal.SystemLibrary.get_engine_version(),
    "level": unreal.get_editor_subsystem(unreal.LevelEditorSubsystem),
    "docs": {
        "set_level_viewport_camera_info": doc(unreal.LevelEditorSubsystem, "set_level_viewport_camera_info"),
        "get_level_viewport_camera_info": doc(unreal.LevelEditorSubsystem, "get_level_viewport_camera_info"),
        "get_level_viewport_fov": doc(unreal.LevelEditorSubsystem, "get_level_viewport_fov"),
        "set_level_viewport_fov": doc(unreal.LevelEditorSubsystem, "set_level_viewport_fov"),
        "TextureRenderTarget2D.export_to_disk": doc(unreal.TextureRenderTarget2D, "export_to_disk"),
        "SceneCaptureComponent2D.capture_scene": doc(unreal.SceneCaptureComponent2D, "capture_scene"),
        "SceneCapture2D.get_capture_component_2d": doc(unreal.SceneCapture2D, "get_capture_component_2d"),
        "SystemLibrary.execute_console_command": doc(unreal.SystemLibrary, "execute_console_command"),
        "TextureRenderTarget2D.size_x": doc(unreal.TextureRenderTarget2D, "size_x"),
    },
    "members": {
        "TextureRenderTarget2D": [name for name in dir(unreal.TextureRenderTarget2D)
                                 if any(word in name.lower() for word in ("export", "init", "size", "update", "format"))],
        "SceneCaptureComponent2D": [name for name in dir(unreal.SceneCaptureComponent2D)
                                    if any(word in name.lower() for word in ("capture", "target", "fov", "show"))],
        "SceneCapture2D": [name for name in dir(unreal.SceneCapture2D)
                           if any(word in name.lower() for word in ("capture", "component"))],
    },
}

with open(OUT, "w", encoding="utf-8") as stream:
    json.dump(report, stream, indent=1, ensure_ascii=False, default=str)

for key, value in report["docs"].items():
    print(f"--- {key}: {value[:600]}")
