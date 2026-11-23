# SPDX-License-Identifier: MIT
"""Freies Editor-Python für Missum: `python.run` führt Code im laufenden Unreal-Editor aus.

Das ist der Harness-Weg ohne eigenes Engine-Plugin: alles, was Epics Python-API im Editor
kann (Assets, Actors, Materialien, Subsysteme, Projekteinstellungen, Imports), steht dem
Agenten offen. Ausgaben von print und die Variable `result` kommen zurück. Änderungen laufen
in einer Editor-Transaktion und lassen sich mit Strg+Z zurücknehmen.
"""

import contextlib
import io
import json
import traceback

import unreal

import unreal_ops as ops


MAX_CODE = 65536
MAX_OUTPUT = 120000
# Prozessstarts und rekursives Löschen gehören nicht in den Editor; das gilt wie in Blender.
BLOCKED = ("subprocess", "os.system", "shutil.rmtree", "os.remove(", "os.rmdir(", "webbrowser", "ctypes")


def _json_safe(value, depth=0):
    if depth > 4:
        return repr(value)[:2000]
    if value is None or isinstance(value, (bool, int, float, str)):
        return value
    if isinstance(value, (list, tuple, set)):
        return [_json_safe(item, depth + 1) for item in list(value)[:500]]
    if isinstance(value, dict):
        return {str(key): _json_safe(item, depth + 1) for key, item in list(value.items())[:500]}
    if isinstance(value, unreal.Object):
        try:
            return {"class": value.get_class().get_name(), "path": value.get_path_name()}
        except Exception:
            return repr(value)[:2000]
    for attribute in ("x", "y", "z"):
        if not hasattr(value, attribute):
            break
    else:
        try:
            return {"x": float(value.x), "y": float(value.y), "z": float(value.z)}
        except Exception:
            pass
    return repr(value)[:2000]


@ops.operation("python.run", "Führt Python-Code mit dem Modul unreal im Editor aus; print und result kommen zurück.")
def op_python_run(arguments, context):
    code = ops.text(arguments, "code")
    if not code.strip():
        raise ops.OpsError("unreal.python_empty", "Es fehlt Python-Code.")
    if len(code) > MAX_CODE:
        raise ops.OpsError("unreal.python_too_long", "Der Python-Code ist zu lang (höchstens 64 KB).")
    lowered = code.lower()
    blocked = [token for token in BLOCKED if token in lowered]
    if blocked:
        raise ops.OpsError("unreal.python_blocked",
                           "Diese Aufrufe sind im Editor gesperrt: " + ", ".join(blocked) + ".")
    namespace = {"unreal": unreal, "result": None, "__name__": "__missum__"}
    stdout = io.StringIO()
    description = ops.text(arguments, "description") or "Missum Python"
    error = None
    suggestion = None
    try:
        with unreal.ScopedEditorTransaction(description[:120]):
            with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stdout):
                exec(compile(code, "<missum>", "exec"), namespace)
    except BaseException as failure:
        error = traceback.format_exc(limit=8)
        suggestion = _attribute_suggestion(failure)
    output = stdout.getvalue()
    if len(output) > MAX_OUTPUT:
        output = output[:MAX_OUTPUT] + "\n… (gekürzt)"
    world = ops.current_world()
    response = {
        "ok": error is None,
        "stdout": output,
        "result": _json_safe(namespace.get("result")),
        "world": world.get_path_name() if world is not None else "",
    }
    if error is not None:
        response["error"] = error[-8000:]
        response["hint"] = "Der Code warf eine Ausnahme; Änderungen bis dahin bleiben in der Transaktion und sind mit Strg+Z rücknehmbar."
        if suggestion:
            response["suggestion"] = suggestion
    try:
        json.dumps(response)
    except (TypeError, ValueError):
        response["result"] = repr(namespace.get("result"))[:2000]
    return response


def _attribute_suggestion(failure):
    """Names the real Unreal Python API when the model guessed a method or property name.

    Python 3.10+ AttributeError carries obj and name; Unreal's names often differ from the C++
    ones (set_actor_scale3d, root_component), so close matches save a blind retry.
    """
    if not isinstance(failure, AttributeError):
        return None
    name = getattr(failure, "name", None)
    target = getattr(failure, "obj", None)
    if not name or target is None:
        return None
    import difflib
    try:
        public = [entry for entry in dir(target) if not entry.startswith("_")]
    except Exception:
        return None
    parts = [part for part in name.lower().replace("get_", "").replace("set_", "").split("_") if len(part) > 2]
    close = difflib.get_close_matches(name, public, n=6, cutoff=0.6)
    related = [entry for entry in public if parts and all(part in entry.lower() for part in parts)]
    candidates = list(dict.fromkeys(close + related))[:10]
    type_name = type(target).__name__
    if not candidates:
        return f"{type_name} hat kein '{name}'. Prüfe die verfügbaren Namen mit print(dir(obj))."
    return f"{type_name} hat kein '{name}'. Passende vorhandene Namen: " + ", ".join(candidates) + "."
