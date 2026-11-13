"""GUI startup script for the shipping check of the Missum Blender bridge.

Blender is started as a normal windowed application with a private user-scripts
directory, so nothing of the user's own configuration is touched. The add-on is
enabled the ordinary way; from here on only ``bpy.app.timers`` drives the
bridge, exactly as in daily use.

Environment:
* ``MISSUM_GUI_SECONDS``  hard cap after which Blender quits (default 90)
* ``MISSUM_GUI_FLAG``     file that makes Blender quit early when it appears
* ``MISSUM_GUI_LOG``       progress lines (also printed to stdout)
"""

import os
import time

import addon_utils
import bpy

LOG = os.environ.get("MISSUM_GUI_LOG", "")
FLAG = os.environ.get("MISSUM_GUI_FLAG", "")
CAP_SECONDS = float(os.environ.get("MISSUM_GUI_SECONDS", "90"))
STARTED = time.time()


def log(message):
    line = f"MISSUM_GUI {message}"
    print(line, flush=True)
    if LOG:
        try:
            with open(LOG, "a", encoding="utf-8") as handle:
                handle.write(line + "\n")
        except OSError:
            pass


module = None
try:
    module = addon_utils.enable("missum_blender_bridge", default_set=False, persistent=True)
except Exception as error:  # noqa: BLE001
    log(f"enable failed: {error!r}")

if module is None:
    log("enabled=False reason=addon not found or registration refused")
else:
    state = module.STATE
    log(
        "enabled=True "
        f"port={state.port} running={state.running} instance={module.SESSION.instance_id} "
        f"timer_registered={bpy.app.timers.is_registered(module._pump_timer)} "
        f"python_allowed={module.python_allowed()} objects={len(bpy.data.objects)}"
    )


def _watch_for_quit():
    if FLAG and os.path.exists(FLAG):
        try:
            os.remove(FLAG)
        except OSError:
            pass
        log("quit requested by flag")
        _shutdown()
        return None
    if time.time() - STARTED > CAP_SECONDS:
        log("quit after cap")
        _shutdown()
        return None
    return 0.4


def _shutdown():
    try:
        if module is not None:
            module.stop_server()
            log(f"server stopped, requests served={module.STATE.request_count}")
    except Exception as error:  # noqa: BLE001
        log(f"stop failed: {error!r}")
    try:
        bpy.ops.wm.quit_blender()
    except Exception as error:  # noqa: BLE001
        log(f"quit operator failed: {error!r}")


bpy.app.timers.register(_watch_for_quit, first_interval=1.0)
log("ready")
