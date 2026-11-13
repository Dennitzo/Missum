# SPDX-License-Identifier: MIT
"""Connect the Blender process started by Missum to its workspace tools.

Blender runs this script once at launch. The socket thread authenticates and
queues requests; Blender's main thread applies them. Nothing is registered as
an add-on or saved to Blender's preferences.
"""

import hmac
import json
import os
import select
import socket
import sys
import tempfile
import threading
import time
from collections import OrderedDict
from pathlib import Path
from queue import Empty, Full, Queue

import bpy
from bpy.app.handlers import persistent


MAX_REQUEST_BYTES = 8 * 1024 * 1024
PUMP_INTERVAL = 0.1
REQUEST_LIFETIME_SECONDS = 240


def _launch_path(name):
    value = Path(os.environ.pop(name))
    if not value.is_absolute():
        raise ValueError(f"{name} must be absolute")
    return value.resolve()


READY_FILE = _launch_path("MISSUM_BLENDER_SESSION_FILE")
WORKSPACE = _launch_path("MISSUM_BLENDER_WORKSPACE")
TOKEN = os.environ.pop("MISSUM_BLENDER_TOKEN").encode("utf-8")
if len(TOKEN) < 32:
    raise ValueError("Blender session token is missing or too short")
ALLOW_PYTHON = os.environ.pop("MISSUM_BLENDER_ALLOW_PYTHON", "1").strip().lower() in ("1", "true", "ja", "on")
CAPTURES = (Path(tempfile.gettempdir()) / "missum-program-integrations" / "captures").resolve()

sys.path.insert(0, str(Path(__file__).resolve().parent))
previous_bytecode_setting = sys.dont_write_bytecode
try:
    # The signed runtime directory must stay identical to the extracted package.
    sys.dont_write_bytecode = True
    import scene_ops
finally:
    sys.dont_write_bytecode = previous_bytecode_setting
    sys.path.pop(0)

PROCESS_ID = os.getpid()
START_NS = time.time_ns()
INSTANCE_ID = f"blender-{PROCESS_ID}-{START_NS % 1_000_000_000:09d}"
LOAD_COUNT = 0
LEDGER = OrderedDict()
REQUESTS = Queue(maxsize=16)


def _session_stamp():
    return f"{START_NS}-{LOAD_COUNT}"


def _error(code, message):
    return {"ok": False, "error": {"code": code, "message": message}}


def _reply(connection, payload):
    with connection:
        try:
            response = (json.dumps(payload, ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8")
        except (TypeError, ValueError, RecursionError):
            response = (json.dumps(_error(
                "blender.reply_invalid",
                "Die Blender-Antwort konnte nicht kodiert werden. Die Szene vor einer Wiederholung prüfen.",
            )) + "\n").encode("utf-8")
        try:
            connection.sendall(response)
        except OSError:
            pass


def _receive(connection):
    """Read and authenticate without accessing Blender objects from a worker thread."""
    try:
        connection.settimeout(5)
        with connection.makefile("rb") as stream:
            line = stream.readline(MAX_REQUEST_BYTES + 1)
        if not line:
            connection.close()
            return
        if len(line) > MAX_REQUEST_BYTES:
            _reply(connection, _error("blender.request_too_large", "Anfrage ist zu groß."))
            return
        request = json.loads(line)
        supplied = request.get("token") if isinstance(request, dict) else None
        if not isinstance(supplied, str) or not hmac.compare_digest(supplied.encode("utf-8"), TOKEN):
            _reply(connection, _error("blender.unauthorized", "Die Anfrage gehört nicht zu dieser Missum-Instanz."))
            return
        request.pop("token")
        REQUESTS.put_nowait((connection, request, time.monotonic() + REQUEST_LIFETIME_SECONDS))
    except Full:
        _reply(connection, _error("blender.busy", "Die Warteschlange dieser Blender-Instanz ist voll."))
    except (ValueError, UnicodeError):
        _reply(connection, _error("blender.bad_json", "Die Anfrage war kein gültiges JSON."))
    except OSError:
        connection.close()


def _listen(listener):
    while True:
        try:
            connection, _ = listener.accept()
        except OSError:
            return
        threading.Thread(target=_receive, args=(connection,), daemon=True).start()


def _hello(scene):
    return {
        "application": "blender",
        "applicationVersion": bpy.app.version_string,
        "instanceId": INSTANCE_ID,
        "sessionStamp": _session_stamp(),
        "processId": PROCESS_ID,
        "port": PORT,
        "blendPath": bpy.data.filepath or "",
        "sceneName": scene.name if scene else "",
        "isDirty": bool(bpy.data.is_dirty),
        "backgroundMode": bool(bpy.app.background),
        "capabilities": [
            "scene.read", "scene.edit", "view.capture", "operation.status", "save.without-backup-file",
        ] + (["custom.python"] if ALLOW_PYTHON else []),
        "operations": sorted(scene_ops.OPERATIONS),
        "objectCount": len(bpy.data.objects),
    }


def _execute(request):
    scene = bpy.context.scene
    command = request.get("cmd")
    if command == "hello":
        return {"ok": True, "result": _hello(scene)}
    target = request.get("target")
    current_file = bpy.data.filepath or ""
    if (
        not isinstance(target, dict)
        or target.get("instanceId") != INSTANCE_ID
        or target.get("sessionStamp") != _session_stamp()
        or target.get("sceneName") != (scene.name if scene else "")
        or not isinstance(target.get("projectPath"), str)
        or os.path.normcase(target["projectPath"]) != os.path.normcase(current_file)
    ):
        return _error("blender.rebind_required", "Die gebundene Blender-Szene hat sich geändert. Mit blender.status erneut binden.")
    if current_file and not Path(current_file).resolve().is_relative_to(WORKSPACE):
        return _error("blender.project_outside_workspace", "Die geöffnete Blender-Datei liegt außerhalb des Missum-Projekts.")
    arguments = request.get("args")
    if not isinstance(arguments, dict):
        arguments = {}
    if command == "view.capture":
        target_image = arguments.get("targetImage")
        path = Path(target_image) if isinstance(target_image, str) else Path()
        if not path.is_absolute() or path.suffix.lower() != ".png" or not path.resolve().is_relative_to(CAPTURES):
            return _error("blender.capture_path_invalid", "Die Aufnahme benötigt einen PNG-Pfad im Missum-Aufnahmeverzeichnis.")
    return scene_ops.handle(command, arguments, str(request.get("operationId") or ""), {
        "instanceId": INSTANCE_ID,
        "sessionStamp": _session_stamp(),
        "ledger": LEDGER,
        "allowPython": ALLOW_PYTHON,
        "workspaceRoot": str(WORKSPACE),
    })


def _pump():
    try:
        connection, request, expires = REQUESTS.get_nowait()
    except Empty:
        return PUMP_INTERVAL
    if time.monotonic() > expires:
        _reply(connection, _error("blender.request_expired", "Die Anfrage wurde vor der Ausführung zu alt."))
        return PUMP_INTERVAL
    try:
        # A client that timed out while queued must not trigger a delayed mutation.
        if select.select([connection], [], [], 0)[0] and not connection.recv(1, socket.MSG_PEEK):
            connection.close()
            return PUMP_INTERVAL
    except OSError:
        connection.close()
        return PUMP_INTERVAL
    try:
        payload = _execute(request)
    except (Exception, SystemExit) as error:  # Keep the timer registered after failed scripts.
        payload = _error("blender.command_failed", str(error))
    _reply(connection, payload)
    return PUMP_INTERVAL


@persistent
def _file_loaded(_unused):
    global LOAD_COUNT
    LOAD_COUNT += 1
    LEDGER.clear()


def _start():
    global PORT
    new_project = os.environ.pop("MISSUM_BLENDER_NEW_PROJECT", "")
    if new_project and not bpy.data.filepath:
        target = Path(scene_ops._workspace_project_file(new_project, str(WORKSPACE)))
        # Reserve this exact path without replacing a file created during startup.
        with target.open("xb"):
            pass
        try:
            for obj in list(bpy.context.scene.objects):
                bpy.data.objects.remove(obj, do_unlink=True)
            scene_ops._save_project(str(target), str(WORKSPACE))
        except BaseException:
            if target.is_file() and target.stat().st_size == 0:
                target.unlink()
            raise
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        listener.bind(("127.0.0.1", 0))
        listener.listen(8)
        PORT = listener.getsockname()[1]
        bpy.app.handlers.load_post.append(_file_loaded)
        if not bpy.app.background:
            bpy.app.timers.register(_pump, first_interval=PUMP_INTERVAL, persistent=True)
        threading.Thread(target=_listen, args=(listener,), daemon=True).start()
        temporary = READY_FILE.with_suffix(".tmp")
        temporary.write_text(json.dumps({"port": PORT, "processId": PROCESS_ID}), encoding="utf-8")
        temporary.replace(READY_FILE)
        if bpy.app.background:
            while True:
                time.sleep(_pump())
    except BaseException:
        listener.close()
        raise


_start()
