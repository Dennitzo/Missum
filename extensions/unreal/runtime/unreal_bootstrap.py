# SPDX-License-Identifier: MIT
"""Missum-Kanal im Unreal Editor, ausschließlich über Epics Python.

Der Editor wird von Missum mit `-ExecutePythonScript` auf diese Datei gestartet. Die
Datei öffnet einen Loopback-TCP-Kanal, beantwortet zeilenweise JSON-Aufträge und
schreibt die Bereitschaftsquittung an den in `MISSUM_UNREAL_READY_PATH` genannten Ort.
Es wird kein eigener C++-Code gebaut und kein Plugin installiert.

Ausführung liegt vollständig auf dem Spielthread. Der Annahmekanal ist nichtblockierend
und wird über `unreal.register_ticker_callback` gepumpt; falls diese Anmeldung in einer
Engineversion fehlt, dient `register_slate_post_tick_callback` als Rückfall. Ein eigener
Thread, der Unreal-Objekte berührt, existiert nicht.

Protokoll, identisch zum bisherigen Adaptervertrag:

    Anfrage  {"token": "...", "requestId": "...", "command": "...", "arguments": {}}
    Antwort  {"requestId": "...", "ok": true,  "result": {}}
             {"requestId": "...", "ok": false, "error": {"code": "...", "message": "..."}}

Umgebungsvariablen des Starts:
    MISSUM_UNREAL_TOKEN            sechzehnstelliges Hex-Token, Pflicht
    MISSUM_UNREAL_READY_PATH       Pfad der Bereitschaftsquittung, absolut, Pflicht
    MISSUM_UNREAL_WORKSPACE        Grenze des Missum-Arbeitsverzeichnisses
    MISSUM_UNREAL_PROJECT_PATH     die geöffnete .uproject-Datei
    MISSUM_UNREAL_CAPTURE_ROOT     Ablage für Bildaufnahmen
    MISSUM_UNREAL_INITIALIZE_BLANK_PROJECT  "1" erzeugt die leere Startkarte
"""

import hmac
import json
import os
import socket
import sys
import time
import traceback
import uuid

import unreal


# Der Schalter `-ExecutePythonScript` lässt den Editor nach dem Skript automatisch
# beenden (EditorPythonExecuter ruft QUIT_EDITOR). Die Engine stellt dafür ausdrücklich
# eine Abstellmöglichkeit zur Verfügung: solange KeepPythonScriptAlive gesetzt ist,
# bleibt der Editor offen und der Kanal bedienbar. Der Aufruf muss vor allem anderen
# erfolgen, weil die Engine unmittelbar nach dem Skript prüft.
try:
    unreal.EditorPythonScripting.set_keep_python_script_alive(True)
except BaseException as error:  # Ältere Engineversionen ohne diese Funktion.
    unreal.log_warning(
        "Missum-Kanal: KeepPythonScriptAlive ist in dieser Engineversion nicht verfügbar "
        f"({error}). Der Editor kann nach dem Startskript automatisch schließen.")


PROTOCOL = 1
PUMP_SECONDS = 0.01
MAX_REQUEST_BYTES = 4 * 1024 * 1024
MAX_CONNECTIONS = 8
MAX_BYTES_PER_PUMP = 2 * 1024 * 1024
CONNECTION_LIFETIME_SECONDS = 60.0
BLANK_MAP_ASSET = "/Game/Maps/Main"
MAPS_FOLDER = "/Game/Maps"


# ---------------------------------------------------------------------------
# Startumgebung
# ---------------------------------------------------------------------------

def _environment(name, required=False, allow_empty=False):
    value = os.environ.get(name)
    if value is None or value == "":
        if required:
            raise RuntimeError(f"{name} fehlt; der Missum-Kanal startet nicht.")
        return ""
    return value


def _absolute(name, required=False):
    value = _environment(name, required=required)
    if not value:
        return ""
    if not os.path.isabs(value):
        raise RuntimeError(f"{name} muss ein absoluter Pfad sein.")
    return os.path.normpath(value)


TOKEN = _environment("MISSUM_UNREAL_TOKEN", required=True).encode("utf-8")
if len(TOKEN) < 32:
    raise RuntimeError("MISSUM_UNREAL_TOKEN ist zu kurz.")
READY_PATH = _absolute("MISSUM_UNREAL_READY_PATH", required=True)
WORKSPACE = _absolute("MISSUM_UNREAL_WORKSPACE")
CAPTURE_ROOT = _absolute("MISSUM_UNREAL_CAPTURE_ROOT")
DECLARED_PROJECT = _absolute("MISSUM_UNREAL_PROJECT_PATH")
INITIALIZE_BLANK = _environment("MISSUM_UNREAL_INITIALIZE_BLANK_PROJECT") == "1"

PROCESS_ID = os.getpid()
INSTANCE_ID = uuid.uuid4().hex

RUNTIME_DIRECTORY = os.path.normpath(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, RUNTIME_DIRECTORY)
_previous_bytecode = sys.dont_write_bytecode
try:
    # Das Signierte Erweiterungspaket darf durch den Start nicht verändert werden.
    sys.dont_write_bytecode = True
    import unreal_ops as ops
    # Zusatzmodule tragen sich in dasselbe Register ein; fehlt eines, arbeitet der Kern weiter.
    for optional_module in ("unreal_materials", "unreal_capture", "unreal_light", "unreal_blueprint", "unreal_python"):
        try:
            __import__(optional_module)
        except BaseException as module_error:
            print(f"Missum-Kanal: Zusatzmodul {optional_module} nicht geladen: {module_error}")
finally:
    sys.dont_write_bytecode = _previous_bytecode


CONTEXT = {
    "instanceId": INSTANCE_ID,
    "processId": PROCESS_ID,
    "projectPath": DECLARED_PROJECT,
    "projectDirectory": "",
    "workspace": WORKSPACE,
    "captureRoot": CAPTURE_ROOT,
    "engineVersion": "",
    "python": sys.version.split()[0],
    "pump": "",
    "protocol": PROTOCOL,
    "ledger": {},
    "operations": {},
    "packages": {},
}

LISTENER = None
PORT = 0
CONNECTIONS = {}
TICKER_HANDLE = None
TICKER_NAME = f"MissumChannel-{INSTANCE_ID}"
STATE = {"blankPending": INITIALIZE_BLANK, "blankError": "", "receiptWritten": False,
         "pumpFailures": 0, "lastPumpError": ""}


# ---------------------------------------------------------------------------
# Bereitschaftsquittung
# ---------------------------------------------------------------------------

def _resolve_project_path():
    """Bestimmt die geöffnete .uproject-Datei, zuerst aus der eigenen Angabe der Engine."""
    try:
        if unreal.Paths.is_project_file_path_set():
            reported = unreal.Paths.get_project_file_path()
            if reported and os.path.isfile(str(reported)):
                return os.path.normpath(str(reported))
    except BaseException:
        pass
    if DECLARED_PROJECT and os.path.isfile(DECLARED_PROJECT):
        return DECLARED_PROJECT
    try:
        directory = str(unreal.Paths.project_dir())
    except BaseException:
        return DECLARED_PROJECT
    try:
        matches = [name for name in os.listdir(directory) if name.lower().endswith(".uproject")]
    except OSError:
        return DECLARED_PROJECT
    if matches:
        return os.path.normpath(os.path.join(directory, matches[0]))
    return DECLARED_PROJECT


def _write_receipt(initialized, initialization_error=""):
    """Schreibt die Quittung atomar; Missum akzeptiert die Verbindung erst danach."""
    receipt = {
        "port": PORT,
        "processId": PROCESS_ID,
        "token": TOKEN.decode("ascii"),
        "instanceId": INSTANCE_ID,
        "projectPath": CONTEXT["projectPath"],
        "initialized": bool(initialized),
        "channel": {
            "type": ops.CHANNEL,
            "protocol": PROTOCOL,
            "runtimeVersion": ops.RUNTIME_VERSION,
            "pump": CONTEXT["pump"],
            "transport": "loopback-tcp",
        },
        "capabilities": sorted(ops.OPERATIONS),
    }
    if initialization_error:
        receipt["initializationError"] = initialization_error
    temporary = f"{READY_PATH}.tmp"
    with open(temporary, "w", encoding="utf-8") as stream:
        json.dump(receipt, stream, ensure_ascii=False)
    os.replace(temporary, READY_PATH)
    STATE["receiptWritten"] = True


# ---------------------------------------------------------------------------
# Leere Startkarte, bisherige Aufgabe des eigenen Plugins
# ---------------------------------------------------------------------------

def _ini_write(path, section, values):
    """Setzt Schlüssel in einem ini-Abschnitt und schreibt atomar ohne BOM zurück."""
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
            current = lines[index].lstrip()
            if current.lower().startswith(f"{key.lower()}="):
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
    return True


def _initialize_blank_project():
    """Erzeugt die leere Startkarte, wie sie das frühere Plugin für Neuprojekte tat."""
    subsystem = ops.level_subsystem()
    if subsystem is None:
        return "Der Editor stellt kein LevelEditorSubsystem zur Verfügung."
    try:
        map_file = ops.package_file(BLANK_MAP_ASSET)
    except ops.OpsError as error:
        return error.message
    directory = os.path.dirname(map_file)
    receipt_directory = os.path.join(str(unreal.Paths.project_saved_dir()), "Missum")
    intent_path = os.path.join(receipt_directory, "blank-main.intent")
    digest_path = os.path.join(receipt_directory, "blank-main.md5")
    os.makedirs(receipt_directory, exist_ok=True)
    exists = os.path.isfile(map_file)
    if exists:
        if not os.path.isfile(intent_path):
            return ("Die Startkarte /Game/Maps/Main existiert bereits und gehört nicht nachweisbar "
                    "zu dieser Einrichtung. Sie wurde nicht überschrieben.")
        if ops.file_digest(map_file) != _read_text(digest_path):
            return ("Die Startkarte /Game/Maps/Main wurde außerhalb von Missum verändert. "
                    "Sie wurde nicht überschrieben.")
        try:
            if not subsystem.load_level(BLANK_MAP_ASSET):
                return "Die bereits erstellte leere Startkarte konnte nicht erneut geöffnet werden."
        except BaseException as error:
            return f"Die bereits erstellte leere Startkarte konnte nicht erneut geöffnet werden: {error}"
    else:
        os.makedirs(directory, exist_ok=True)
        _write_text(intent_path, BLANK_MAP_ASSET)
        try:
            # UE 5.8: new_level(asset_path, is_partitioned_world). The old (name, folder) form passed the
            # folder string as the bool flag and failed in every new project.
            ops.settle_unsaved_changes({})
            created, _ = ops.call_any(subsystem, "new_level", [
                (BLANK_MAP_ASSET, bool(ops.project_uses_world_partition())),
                (BLANK_MAP_ASSET,),
            ])
            if not created:
                return "Die leere Startkarte konnte nicht erstellt werden."
            if not subsystem.save_current_level():
                return "Die leere Startkarte wurde erstellt, konnte aber nicht gespeichert werden."
        except BaseException as error:
            return f"Die leere Startkarte konnte nicht erstellt werden: {error}"
        if not os.path.isfile(map_file):
            return "Die leere Startkarte wurde gespeichert, ist aber nicht auf der Platte nachweisbar."
        _write_text(digest_path, ops.file_digest(map_file))

    config = os.path.join(str(unreal.Paths.project_config_dir()), "DefaultEngine.ini")
    full_map = f"{BLANK_MAP_ASSET}.{os.path.basename(BLANK_MAP_ASSET)}"
    _ini_write(config, "/Script/EngineSettings.GameMapsSettings",
               {"EditorStartupMap": full_map, "GameDefaultMap": full_map})
    with open(config, "r", encoding="utf-8-sig", errors="replace") as stream:
        content = stream.read()
    if full_map not in content:
        return "Die Startkarteneinstellungen konnten nach dem Speichern nicht bestätigt werden."
    return ""


def _read_text(path):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as stream:
            return stream.read().strip()
    except OSError:
        return ""


def _write_text(path, value):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as stream:
        stream.write(value)


# ---------------------------------------------------------------------------
# Kanal
# ---------------------------------------------------------------------------

def _open_listener():
    global LISTENER, PORT
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.bind(("127.0.0.1", 0))
    listener.listen(MAX_CONNECTIONS)
    listener.setblocking(False)
    LISTENER = listener
    PORT = listener.getsockname()[1]


def _accept_new():
    accepted = 0
    while accepted < MAX_CONNECTIONS:
        try:
            connection, _ = LISTENER.accept()
        except (BlockingIOError, InterruptedError, TimeoutError):
            return
        except OSError:
            return
        try:
            connection.setblocking(False)
            connection.settimeout(0.5)
        except OSError:
            connection.close()
            continue
        CONNECTIONS.setdefault(connection.fileno(), {
            "socket": connection,
            "buffer": bytearray(),
            "expires": time.monotonic() + CONNECTION_LIFETIME_SECONDS,
        })
        accepted += 1


def _reply(connection, payload):
    try:
        connection.sendall((json.dumps(payload, ensure_ascii=False, allow_nan=False, default=str) + "\n")
                           .encode("utf-8"))
    except OSError:
        pass
    except (TypeError, ValueError, RecursionError):
        try:
            connection.sendall((json.dumps({
                "ok": False,
                "error": {"code": "unreal.reply_invalid",
                          "message": "Die Editorantwort konnte nicht kodiert werden."}},
                ensure_ascii=False) + "\n").encode("utf-8"))
        except OSError:
            pass


def _execute(request):
    request_id = request.get("requestId")
    supplied = request.get("token")
    if not isinstance(supplied, str) or not hmac.compare_digest(supplied.encode("utf-8"), TOKEN):
        return {"requestId": request_id, "ok": False,
                "error": {"code": "unreal.unauthorized",
                          "message": "Die Anfrage gehört nicht zu dieser Missum-Instanz."}}
    command = request.get("command")
    arguments = request.get("arguments")
    if not isinstance(arguments, dict):
        arguments = {}
    try:
        result = ops.handle(command, arguments, CONTEXT)
        if not isinstance(result, dict):
            result = {"value": result}
        return {"requestId": request_id, "ok": True, "result": result}
    except ops.OpsError as error:
        return {"requestId": request_id, "ok": False,
                "error": {"code": error.code, "message": error.message}}
    except BaseException as error:  # Ein Einzelfehler darf den Editor nicht mitreißen.
        STATE["lastPumpError"] = traceback.format_exc(limit=4)
        return {"requestId": request_id, "ok": False,
                "error": {"code": "unreal.command_failed",
                          "message": f"{type(error).__name__}: {error}"}}


def _service_connection(entry):
    """Liest im Rahmen eines Lesebudgets und beantwortet eine vollständige Zeile."""
    connection = entry["socket"]
    read_budget = MAX_BYTES_PER_PUMP
    while read_budget > 0:
        try:
            chunk = connection.recv(65536)
        except (BlockingIOError, InterruptedError, TimeoutError):
            return False
        except OSError:
            return True
        if not chunk:
            return True
        read_budget -= len(chunk)
        newline = chunk.find(b"\n")
        if newline >= 0:
            entry["buffer"].extend(chunk[:newline])
            line = bytes(entry["buffer"])
            entry["buffer"].clear()
            if len(line) > MAX_REQUEST_BYTES:
                _reply(connection, {"requestId": None, "ok": False,
                                    "error": {"code": "unreal.request_too_large",
                                              "message": "Die Anfrage ist zu groß."}})
                return True
            try:
                request = json.loads(line.decode("utf-8"))
                if not isinstance(request, dict):
                    raise ValueError("kein Objekt")
            except (ValueError, UnicodeDecodeError):
                _reply(connection, {"requestId": None, "ok": False,
                                    "error": {"code": "unreal.bad_json",
                                              "message": "Die Anfrage war kein gültiges JSON."}})
                return True
            _reply(connection, _execute(request))
            return True
        entry["buffer"].extend(chunk)
        if len(entry["buffer"]) > MAX_REQUEST_BYTES:
            _reply(connection, {"requestId": None, "ok": False,
                                "error": {"code": "unreal.request_too_large",
                                          "message": "Die Anfrage ist zu groß."}})
            return True
    return False


def _pump(delta):
    """Läuft auf dem Spielthread; nimmt Anfragen an und beantwortet sie sofort."""
    try:
        if STATE["blankPending"] and ops.current_world() is not None:
            STATE["blankPending"] = False
            error = _initialize_blank_project()
            STATE["blankError"] = error
            if STATE["receiptWritten"]:
                _write_receipt(error == "", error)
        _accept_new()
        expired = time.monotonic()
        for descriptor in list(CONNECTIONS):
            entry = CONNECTIONS.pop(descriptor, None)
            if entry is None:
                continue
            connection = entry["socket"]
            keep_open = False
            try:
                if expired > entry["expires"]:
                    _reply(connection, {"requestId": None, "ok": False,
                                        "error": {"code": "unreal.request_expired",
                                                  "message": "Die Anfrage wurde zu alt vor der Antwort."}})
                    continue
                keep_open = not _service_connection(entry)
            finally:
                if keep_open:
                    CONNECTIONS[descriptor] = entry
                else:
                    try:
                        connection.close()
                    except OSError:
                        pass
        ops.tick(CONTEXT)
    except BaseException:
        STATE["pumpFailures"] += 1
        STATE["lastPumpError"] = traceback.format_exc(limit=4)
        if STATE["pumpFailures"] == 1:
            unreal.log_warning("Missum-Kanal: Pump-Fehler, der Kanal läuft weiter. "
                               + STATE["lastPumpError"].splitlines()[-1])
    return True


def _register_pump():
    """Meldet den Pump an; Spielthread ist Pflicht, deshalb kein eigener Thread."""
    try:
        handle = unreal.register_ticker_callback(_pump, PUMP_SECONDS)
        CONTEXT["pump"] = "register_ticker_callback"
        return handle
    except BaseException as error:
        unreal.log_warning(f"Missum-Kanal: register_ticker_callback nicht nutzbar ({error}).")
    try:
        handle = unreal.register_slate_post_tick_callback(_pump)
        CONTEXT["pump"] = "register_slate_post_tick_callback"
        return handle
    except BaseException as error:
        raise RuntimeError(
            "Diese Engineversion stellt keinen Spielthread-Eingang für den Missum-Kanal zur Verfügung. "
            f"Ursache: {error}") from error


def _shutdown(_unused=None):
    global LISTENER
    try:
        if TICKER_HANDLE is not None:
            if CONTEXT["pump"] == "register_ticker_callback":
                unreal.unregister_ticker_callback(TICKER_HANDLE)
            elif CONTEXT["pump"] == "register_slate_post_tick_callback":
                unreal.unregister_slate_post_tick_callback(TICKER_HANDLE)
    except BaseException:
        pass
    for entry in list(CONNECTIONS.values()):
        try:
            entry["socket"].close()
        except OSError:
            pass
    CONNECTIONS.clear()
    if LISTENER is not None:
        try:
            LISTENER.close()
        except OSError:
            pass
        LISTENER = None


def start():
    global TICKER_HANDLE
    try:
        CONTEXT["engineVersion"] = str(unreal.SystemLibrary.get_engine_version())
    except BaseException:
        CONTEXT["engineVersion"] = ""
    try:
        CONTEXT["projectDirectory"] = os.path.normpath(str(unreal.Paths.project_dir()))
    except BaseException:
        CONTEXT["projectDirectory"] = ""
    CONTEXT["projectPath"] = _resolve_project_path()
    _open_listener()
    TICKER_HANDLE = _register_pump()
    try:
        unreal.register_python_shutdown_callback(_shutdown)
    except BaseException as error:
        unreal.log_warning(f"Missum-Kanal: Shutdown-Meldung nicht verfügbar ({error}).")
    if CONTEXT["projectPath"] and not os.path.isfile(CONTEXT["projectPath"]):
        _write_receipt(False, "Das geöffnete Unreal-Projekt ist nicht als Datei nachweisbar.")
        raise RuntimeError("Missum-Kanal:-Projektpfad unplausibel: " + CONTEXT["projectPath"])
    _write_receipt(not INITIALIZE_BLANK)
    unreal.log(f"Missum-Kanal aktiv: Port {PORT}, Pump {CONTEXT['pump']}, "
               f"{len(ops.OPERATIONS)} Operationen, Python {sys.version.split()[0]}.")


start()
