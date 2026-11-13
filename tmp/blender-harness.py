"""Test harness: runs the Missum Blender bridge inside a real Blender process.

Background Blender has no window event loop, so the timer pump is driven by this
loop instead.  Everything else is the shipping add-on code: the same socket
server, the same main-thread execution and the same scene operations.

Flags (files in the flag directory) let the test driver change the situation
while Blender is running:

* ``switch``  load a second .blend file -> new session stamp, new project path
* ``stop``    stop the bridge -> Missum must report a lost connection
* ``quit``    end the harness
"""

import os
import sys
import time

import bpy

REPO = r"C:\Users\AMD\Documents\GitHub\Missum"
ADDON_PARENT = os.path.join(REPO, "extensions", "blender")
if ADDON_PARENT not in sys.path:
    sys.path.insert(0, ADDON_PARENT)

import missum_blender_bridge as bridge  # noqa: E402

PROJECT = os.environ.get("MISSUM_TEST_BLEND", os.path.join(REPO, "tmp", "test", "MissumTestScene.blend"))
OTHER = os.environ.get("MISSUM_TEST_BLEND_OTHER", os.path.join(REPO, "tmp", "test", "MissumOtherScene.blend"))
FLAGS = os.environ.get("MISSUM_TEST_FLAGS", os.path.join(REPO, "tmp", "test", "flags"))
SECONDS = float(os.environ.get("MISSUM_TEST_SECONDS", "240"))

os.makedirs(os.path.dirname(PROJECT), exist_ok=True)
os.makedirs(FLAGS, exist_ok=True)


def flag_path(name):
    return os.path.join(FLAGS, name)


def clear_flags():
    for name in ("switch", "stop", "quit", "ready", "switched", "stopped"):
        try:
            os.remove(flag_path(name))
        except OSError:
            pass


clear_flags()

# The user's normal backup setting: Missum must not create a .blend1 with this on.
bpy.context.preferences.filepaths.save_version = 1

# A first save gives the run a real project file with a real path.
fresh = not os.path.exists(PROJECT)
if fresh:
    bpy.ops.wm.save_mainfile(filepath=PROJECT)

# A second, different file used for the scene-switch test: same startup content
# but its own path and its own scene name.
if not os.path.exists(OTHER):
    bpy.context.scene.name = "AndereSzene"
    bpy.ops.wm.save_mainfile(filepath=OTHER)
    bpy.ops.wm.open_mainfile(filepath=PROJECT)

try:
    bridge._handle_load.persistent = True
except (AttributeError, TypeError):
    pass
if bridge._handle_load not in bpy.app.handlers.load_post:
    bpy.app.handlers.load_post.append(bridge._handle_load)

bridge.stop_server()
port = bridge.start_server()
print(
    "MISSUM_HARNESS "
    + f"port={port} project={PROJECT} save_version={bpy.context.preferences.filepaths.save_version} "
    + f"instance={bridge.SESSION.instance_id} objects={len(bpy.data.objects)}",
    flush=True,
)
open(flag_path("ready"), "w", encoding="utf-8").close()

deadline = time.time() + SECONDS
while time.time() < deadline:
    bridge.pump_once()
    if os.path.exists(flag_path("stop")):
        os.remove(flag_path("stop"))
        bridge.stop_server()
        open(flag_path("stopped"), "w", encoding="utf-8").close()
        print("MISSUM_HARNESS bridge stopped", flush=True)
    if os.path.exists(flag_path("switch")):
        os.remove(flag_path("switch"))
        bpy.ops.wm.open_mainfile(filepath=OTHER)
        open(flag_path("switched"), "w", encoding="utf-8").close()
        print(f"MISSUM_HARNESS switched to {OTHER}", flush=True)
    if os.path.exists(flag_path("quit")):
        os.remove(flag_path("quit"))
        print("MISSUM_HARNESS quitting", flush=True)
        break
    time.sleep(0.02)

bridge.stop_server()
print("MISSUM_HARNESS done", flush=True)
