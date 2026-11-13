# SPDX-License-Identifier: MIT
"""Missum Blender Bridge.

Missum talks to this add-on over a loopback socket.  A worker thread only
accepts the connection and queues the request; the work itself is carried out
in Blender's main thread by a ``bpy.app.timers`` pump, because operator calls
are only allowed there.

Every request names the running instance and the open scene, so Missum can bind
one model run to exactly this Blender window and file.  If Blender is restarted
or another file is loaded, the session stamp changes and Missum refuses to
apply pending changes instead of writing them into a different scene.

Install (legacy add-on): copy the folder ``missum_blender_bridge`` into
``%APPDATA%\\Blender Foundation\\Blender\\<version>\\scripts\\addons`` and enable
"Missum Blender Bridge" in Edit > Preferences > Add-ons.
"""

import json
import os
import socket
import threading
import time
from collections import OrderedDict

import bpy

bl_info = {
    "name": "Missum Blender Bridge",
    "author": "Missum",
    "version": (1, 0, 1),
    "blender": (4, 2, 0),
    "location": "3D-View-Seitenleiste > Missum",
    "description": (
        "Verbindet Missum mit dieser Blender-Instanz: Szene lesen, ändern, "
        "Ansicht aufnehmen, auf dieselbe .blend-Datei speichern."
    ),
    "warning": "Öffnet eine Loopback-Verbindung auf dem eigenen Rechner.",
    "category": "System",
    "support": "COMMUNITY",
}

from . import scene_ops  # noqa: E402

DEFAULT_PORT = 8765
PORT_SPAN = 12
MAX_REQUEST_BYTES = 8 * 1024 * 1024
JOB_TTL_SECONDS = 900.0
PUMP_INTERVAL = 0.1


def _now_ns():
    return time.time_ns()


class _Session:
    """Identity of this running Blender process and its currently open file."""

    def __init__(self):
        self.start_ns = _now_ns()
        self.process_id = os.getpid()
        self.load_counter = 0

    @property
    def instance_id(self):
        return f"blender-{self.process_id}-{self.start_ns % 1_000_000_000:09d}"

    @property
    def session_stamp(self):
        return f"{self.start_ns}-{self.load_counter}"


SESSION = _Session()
LEDGER = OrderedDict()


class _ServerState:
    def __init__(self):
        self.socket = None
        self.thread = None
        self.port = 0
        self.running = False
        self.last_error = ""
        self.request_count = 0
        self.last_command = ""
        self.lock = threading.Lock()
        self.queue = []


STATE = _ServerState()


# --------------------------------------------------------------------------- #
# configuration
# --------------------------------------------------------------------------- #

def _preferences():
    try:
        addon = bpy.context.preferences.addons.get(__package__)
        if addon is not None:
            return addon.preferences
    except (RuntimeError, TypeError, AttributeError):
        pass
    return None


def configured_port():
    override = os.environ.get("MISSUM_BLENDER_PORT", "").strip()
    if override.isdigit():
        return int(override)
    prefs = _preferences()
    if prefs is not None and getattr(prefs, "port", 0) > 0:
        return int(prefs.port)
    return DEFAULT_PORT


def python_allowed():
    override = os.environ.get("MISSUM_BLENDER_ALLOW_PYTHON", "").strip()
    if override:
        return override.lower() in ("1", "true", "ja", "on")
    prefs = _preferences()
    if prefs is not None:
        return bool(getattr(prefs, "allow_python", True))
    return True


# --------------------------------------------------------------------------- #
# socket worker: accept and queue, never touch bpy here
# --------------------------------------------------------------------------- #

def _listen_loop():
    while STATE.running:
        try:
            connection, _address = STATE.socket.accept()
        except socket.timeout:
            continue
        except OSError:
            break
        worker = threading.Thread(
            target=_handle_connection, args=(connection,), daemon=True, name="missum-bridge-conn"
        )
        worker.start()


def _handle_connection(connection):
    connection.settimeout(10.0)
    try:
        chunks = []
        total = 0
        while True:
            piece = connection.recv(65536)
            if not piece:
                break
            chunks.append(piece)
            total += len(piece)
            if total > MAX_REQUEST_BYTES:
                _send(connection, {
                    "ok": False,
                    "error": {"code": "blender.request_too_large", "message": "Anfrage ist zu groß."},
                })
                return
            if b"\n" in piece:
                break
        if not chunks:
            return
        raw = b"".join(chunks)
        line = raw.split(b"\n", 1)[0].decode("utf-8", errors="replace").strip()
        if not line:
            return
        try:
            request = json.loads(line)
        except ValueError:
            _send(connection, {
                "ok": False,
                "error": {"code": "blender.bad_json", "message": "Die Anfrage war kein gültiges JSON."},
            })
            return
        if not isinstance(request, dict):
            _send(connection, {
                "ok": False,
                "error": {"code": "blender.bad_request", "message": "Die Anfrage muss ein JSON-Objekt sein."},
            })
            return

        job = {
            "connection": connection,
            "cmd": str(request.get("cmd") or ""),
            "args": request.get("args") if isinstance(request.get("args"), dict) else {},
            "operationId": str(request.get("operationId") or ""),
            "target": request.get("target"),
            "workspaceRoot": str(request.get("workspaceRoot") or ""),
            "expires_at": time.time() + JOB_TTL_SECONDS,
        }
        with STATE.lock:
            STATE.queue.append(job)
            STATE.request_count += 1
            STATE.last_command = job["cmd"]
        # The main thread answers; the socket stays open until then.
    except (OSError, ValueError):
        try:
            connection.close()
        except OSError:
            pass


def _send(connection, payload):
    try:
        connection.sendall((json.dumps(payload, ensure_ascii=False) + "\n").encode("utf-8"))
    except OSError:
        pass
    finally:
        try:
            connection.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        try:
            connection.close()
        except OSError:
            pass


# --------------------------------------------------------------------------- #
# main thread work
# --------------------------------------------------------------------------- #

def hello_result():
    scene = bpy.context.scene
    return {
        "application": "blender",
        "applicationVersion": bpy.app.version_string,
        "instanceId": SESSION.instance_id,
        "sessionStamp": SESSION.session_stamp,
        "processId": SESSION.process_id,
        "port": STATE.port,
        "blendPath": bpy.data.filepath or "",
        "sceneName": scene.name if scene else "",
        "isDirty": bool(bpy.data.is_dirty),
        "backgroundMode": bool(bpy.app.background),
        "capabilities": [
            "scene.read",
            "scene.edit",
            "view.capture",
            "operation.status",
            "save.without-backup-file",
        ]
        + (["custom.python"] if python_allowed() else []),
        "operations": sorted(scene_ops.OPERATIONS),
        "objectCount": len(bpy.data.objects),
    }


def execute_job(job):
    cmd = job["cmd"]
    if cmd == "hello":
        return {"ok": True, "result": hello_result()}
    target = job.get("target")
    if target is not None:
        scene = bpy.context.scene
        if (
            not isinstance(target, dict)
            or target.get("instanceId") != SESSION.instance_id
            or target.get("sessionStamp") != SESSION.session_stamp
            or target.get("sceneName") != (scene.name if scene else "")
            or not isinstance(target.get("projectPath"), str)
            or os.path.normcase(target["projectPath"]) != os.path.normcase(bpy.data.filepath or "")
        ):
            return {
                "ok": False,
                "error": {
                    "code": "blender.rebind_required",
                    "message": "Die gebundene Blender-Szene hat sich vor der Ausführung geändert. Mit blender.status erneut binden.",
                },
            }
    context = {
        "instanceId": SESSION.instance_id,
        "sessionStamp": SESSION.session_stamp,
        "ledger": LEDGER,
        "allowPython": python_allowed(),
        "workspaceRoot": job.get("workspaceRoot", ""),
    }
    return scene_ops.handle(cmd, job["args"], job["operationId"], context)


def pump_once():
    """Runs at most one queued request; called by the timer and by tests."""
    now = time.time()
    job = None
    with STATE.lock:
        while STATE.queue:
            candidate = STATE.queue.pop(0)
            if candidate["expires_at"] < now:
                try:
                    candidate["connection"].close()
                except OSError:
                    pass
                continue
            job = candidate
            break
    if job is None:
        return 0
    reply = execute_job(job)
    _send(job["connection"], reply)
    return 1


def _pump_timer():
    try:
        pump_once()
    except Exception as error:  # noqa: BLE001 - the timer must never die silently
        STATE.last_error = f"Timer: {error!r}"
    return PUMP_INTERVAL


# --------------------------------------------------------------------------- #
# server lifecycle
# --------------------------------------------------------------------------- #

def start_server(port=None):
    if STATE.running and STATE.socket is not None:
        return STATE.port
    base = int(port) if port else configured_port()
    last_error = None
    for candidate in range(base, base + PORT_SPAN):
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        try:
            listener.bind(("127.0.0.1", candidate))
        except OSError as error:
            last_error = error
            listener.close()
            continue
        listener.listen(8)
        listener.settimeout(0.5)
        STATE.socket = listener
        STATE.port = candidate
        STATE.running = True
        STATE.last_error = ""
        STATE.thread = threading.Thread(target=_listen_loop, daemon=True, name="missum-bridge")
        STATE.thread.start()
        _ensure_timer()
        return candidate
    STATE.last_error = f"Kein freier Port ab {base}: {last_error}"
    return 0


def stop_server():
    STATE.running = False
    listener = STATE.socket
    STATE.socket = None
    if listener is not None:
        try:
            listener.close()
        except OSError:
            pass
    thread = STATE.thread
    STATE.thread = None
    if thread is not None and thread.is_alive():
        thread.join(timeout=1.5)
    with STATE.lock:
        for job in STATE.queue:
            try:
                job["connection"].close()
            except OSError:
                pass
        STATE.queue.clear()
    STATE.port = 0


def _ensure_timer():
    try:
        if not bpy.app.timers.is_registered(_pump_timer):
            bpy.app.timers.register(_pump_timer, first_interval=PUMP_INTERVAL, persistent=True)
    except (RuntimeError, TypeError, AttributeError):
        pass


def _drop_timer():
    try:
        if bpy.app.timers.is_registered(_pump_timer):
            bpy.app.timers.unregister(_pump_timer)
    except (RuntimeError, TypeError, AttributeError):
        pass


def _handle_load(dummy_scene):
    """A new file means a new session: Missum must bind again before editing."""
    SESSION.load_counter += 1
    _ensure_timer()
    if os.environ.get("MISSUM_BLENDER_AUTOSTART", "1").strip().lower() not in ("0", "false", "off"):
        if not STATE.running:
            try:
                start_server()
            except Exception as error:  # noqa: BLE001
                STATE.last_error = f"Autostart: {error!r}"
    return None


# --------------------------------------------------------------------------- #
# user interface
# --------------------------------------------------------------------------- #

class MISSUM_PT_bridge(bpy.types.Panel):
    bl_label = "Missum"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Missum"

    def draw(self, context):
        layout = self.layout
        running = STATE.running
        row = layout.row()
        icon = "CHECKMARK" if running else "ERROR"
        row.label(text=f"Verbunden: {running}", icon=icon)
        layout.label(text=f"Port: {STATE.port or '—'}")
        layout.label(text=f"Instanz: {SESSION.instance_id}")
        layout.label(text=f"Szene: {bpy.context.scene.name if bpy.context.scene else '—'}")
        layout.label(text=f"Datei: {bpy.path.basename(bpy.data.filepath) or 'ungespeichert'}")
        layout.label(text=f"Anfragen: {STATE.request_count}")
        if STATE.last_error:
            box = layout.box()
            box.label(text="Letzter Fehler:", icon="ERROR")
            box.label(text=STATE.last_error[:60])
        ops = layout.operator("missum.start_bridge", text="Bridge starten", icon="PLAY")
        ops.port = configured_port()
        layout.operator("missum.stop_bridge", text="Bridge stoppen", icon="PAUSE")
        layout.label(text="Python-Ausführung: " + ("an" if python_allowed() else "aus"))


class MISSUM_OT_start(bpy.types.Operator):
    bl_idname = "missum.start_bridge"
    bl_label = "Missum Bridge starten"
    bl_description = "Lauscht auf 127.0.0.1, damit Missum diese Szene ansprechen kann"

    port: bpy.props.IntProperty(name="Port", default=DEFAULT_PORT, min=1024, max=65535)

    def execute(self, context):
        port = start_server(self.port or None)
        if port:
            self.report({"INFO"}, f"Missum Bridge lauscht auf 127.0.0.1:{port}")
            return {"FINISHED"}
        self.report({"ERROR"}, STATE.last_error or "Port nicht verfügbar")
        return {"CANCELLED"}


class MISSUM_OT_stop(bpy.types.Operator):
    bl_idname = "missum.stop_bridge"
    bl_label = "Missum Bridge stoppen"
    bl_description = "Schließt die Loopback-Verbindung zu Missum"

    def execute(self, context):
        stop_server()
        self.report({"INFO"}, "Missum Bridge gestoppt")
        return {"FINISHED"}


class MISSUM_AE_preferences(bpy.types.AddonPreferences):
    bl_idname = __package__

    port: bpy.props.IntProperty(
        name="Port",
        description="Erster geprüfter Port; ist er belegt, werden die nächsten Ports verwendet",
        default=DEFAULT_PORT,
        min=1024,
        max=65535,
    )
    autostart: bpy.props.BoolProperty(
        name="Beim Start von Blender aktivieren",
        description="Die Bridge startet automatisch, wenn das Add-on aktiviert ist",
        default=True,
    )
    allow_python: bpy.props.BoolProperty(
        name="Python-Ausführung erlauben",
        description=(
            "Erlaubt custom.python für Modellierungsaufgaben, die keine der festen Operationen "
            "abdecken. Nur aktivieren, wenn Missum auf diesem Rechner vertraut ist"
        ),
        default=True,
    )

    def draw(self, context):
        layout = self.layout
        layout.prop(self, "port")
        layout.prop(self, "autostart")
        layout.prop(self, "allow_python")
        layout.label(text=f"Aktuell verbunden: {STATE.running} (Port {STATE.port or '—'})")


CLASSES = (
    MISSUM_AE_preferences,
    MISSUM_OT_start,
    MISSUM_OT_stop,
    MISSUM_PT_bridge,
)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    try:
        _handle_load.persistent = True
    except (AttributeError, TypeError):
        pass
    if _handle_load not in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.append(_handle_load)
    _ensure_timer()
    if os.environ.get("MISSUM_BLENDER_AUTOSTART", "1").strip().lower() not in ("0", "false", "off"):
        prefs = _preferences()
        autostart = True if prefs is None else bool(getattr(prefs, "autostart", True))
        if autostart and not STATE.running:
            try:
                start_server()
            except Exception as error:  # noqa: BLE001 - enabling must not break Blender
                STATE.last_error = f"Autostart: {error!r}"


def unregister():
    stop_server()
    _drop_timer()
    try:
        bpy.app.handlers.load_post.remove(_handle_load)
    except ValueError:
        pass
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)
