# SPDX-License-Identifier: MIT
"""Blueprint- und Klassenwerkzeuge des Missum-Unreal-Kanals.

Trägt sich in das Register von `unreal_ops` ein. Was diese Engineversion über Epics Python
nicht erlaubt, antwortet mit einer klaren Fähigkeitsmeldung und nennt den optionalen C++-Weg,
statt eine nicht ausgeführte Änderung vorzutäuschen.
"""

import os

import unreal

import unreal_ops as ops


BLUEPRINT_KINDS = {
    "blueprint": ("Blueprint", "Actor"),
    "actorBlueprint": ("Blueprint", "Actor"),
    "componentBlueprint": ("Blueprint", "ActorComponent"),
    "gameModeBlueprint": ("Blueprint", "GameModeBase"),
    "characterBlueprint": ("Blueprint", "Character"),
    "dataAssetBlueprint": ("Blueprint", "DataAsset"),
    "widgetBlueprint": ("WidgetBlueprint", "UserWidget"),
    "animBlueprint": ("AnimBlueprint", "AnimInstance"),
}

# Knoten- und Pin-Verdrahtung ist über Epics Python nicht vorgesehen.
GRAPH_EDIT_LIMIT = (
    "Die knotengenaue Graphverdrahtung stellt diese Engineversion über Epics Python nicht zur "
    "Verfügung (keine SchemaAction- oder Knotenfabrik-Klassen). Erlaubt bleiben Erzeugen von "
    "Graphen, Variablen, Funktionsüberschreibungen und Kompilieren. Für echte Graphlogik ist "
    "ein eigenes C++-Modul im Projekt der vorgesehene Weg."
)

VARIABLE_TYPES = {
    "bool": "boolean",
    "int": "integer",
    "float": "float",
    "string": "string",
    "name": "name",
    "vector": "vector",
}

# add_member_variable erwartet einen echten Pin-Typ, keinen Typnamen als Zeichenkette.
PIN_CATEGORIES = {
    "bool": ("boolean", None),
    "int": ("integer", None),
    "float": ("float", None),
    "string": ("string", None),
    "name": ("name", None),
    "vector": ("struct", "/Script/Engine.Vector"),
}


def make_pin_type(kind):
    category, sub_object_path = PIN_CATEGORIES.get(kind, ("float", None))
    pin_type = unreal.EdGraphPinType()
    pin_type.set_editor_property("pin_category", category)
    if sub_object_path:
        sub_object = unreal.find_object(None, sub_object_path)
        if sub_object is not None:
            pin_type.set_editor_property("pin_sub_category_object", sub_object)
    return pin_type


def own_variable_names(blueprint):
    try:
        return [str(item) for item in
                unreal.BlueprintEditorLibrary.list_member_variable_names(blueprint, False) or []]
    except BaseException:
        return [item for item in ops._names(unreal.BlueprintEditorLibrary.list_member_variable_names, blueprint)
                if not item.startswith("/Script/")]


def attribute_status(blueprint):
    try:
        return str(blueprint.get_editor_property("status"))
    except BaseException:
        return "unkannt"


def _parent_class(name):
    if not name:
        return unreal.Actor
    if name.startswith("/Script/"):
        found = unreal.find_object(None, name)
        if found is None:
            raise ops.OpsError("unreal.class_missing", f"Die Elternklasse {name} wurde nicht gefunden.")
        return found
    value = getattr(unreal, name, None)
    if value is None:
        raise ops.OpsError("unreal.class_missing",
                           f"Die Elternklasse {name} ist unbekannt. unreal.class.search findet verfügbare Klassen.")
    return value


@ops.operation("class.search", "Findet verfügbare Elternklassen samt Pfad und einigen Eigenschaften.")
def op_class_search(arguments, context):
    query = ops.text(arguments, "query", "Actor").lower()
    limit = max(1, min(int(ops.number(arguments, "limit", 60)), 300))
    matches = []
    for name in dir(unreal):
        if query not in name.lower():
            continue
        if not name[0].isupper():
            continue
        value = getattr(unreal, name, None)
        if value is None or not isinstance(value, type):
            continue
        path = ""
        try:
            path = value.get_class().get_path_name() if hasattr(value, "get_class") else ""
        except BaseException:
            path = ""
        if not path:
            try:
                path = value.get_path_name()
            except BaseException:
                path = ""
        entry = {"name": name, "classPath": path}
        try:
            entry["properties"] = [item for item in dir(value)
                                   if not item.startswith("_")][:14]
        except BaseException:
            pass
        matches.append(entry)
        if len(matches) >= limit:
            break
    return {"query": query, "classes": matches, "count": len(matches),
            "operationId": ops.text(arguments, "operationId")}


def create_blueprint(name, folder, parent_class):
    """Erzeugt einen Blueprint ohne den zugehörigen Editor zu öffnen.

    Der Fabrikweg über AssetTools ist die zweite Wahl: bei manchen Elternklassen öffnet
    er den Blueprint-Editor modal. Das blockiert den Spielthread und damit den ganzen
    Kanal, weil dort auch die Antworten auf andere Anfragen laufen.
    """
    # In UE 5.8 lautet die Signatur create_blueprint_asset_with_parent(asset_path, parent_class).
    asset, form = ops.call_any(unreal.BlueprintEditorLibrary, "create_blueprint_asset_with_parent",
                               [(f"{folder}/{name}", parent_class), (name, folder, parent_class)])
    if asset is None:
        tools = unreal.AssetToolsHelpers.get_asset_tools()
        # Datenassets sind keine Blueprints. Die Python-Datenfabrik öffnet ohne gesetzte
        # Klasse einen modalen Auswahldialog und blockiert damit den Kanal; schlägt die
        # Klasse fehl, melden wir die Grenze, statt zu hängen.
        data_factory = getattr(unreal, "PythonDataAssetFactory", None)
        if data_factory is not None:
            factory = data_factory()
            try:
                factory.set_editor_property("data_asset_class", parent_class)
            except BaseException as error:
                raise ops.OpsError(
                    "unreal.dataasset_factory_unavailable",
                    "Datenassets lassen sich über Epics Python in dieser Engineversion nicht "
                    f"erzeugen: die Klasse ließ sich der Datenfabrik nicht mitgeben ({error}).")
            asset = tools.create_asset(name, folder, parent_class, factory)
            form = "AssetTools.create_asset mit Python-Datenfabrik"
        if asset is None:
            factory = unreal.BlueprintFactory()
            try:
                factory.set_editor_property("parent_class", parent_class)
            except BaseException as error:
                raise ops.OpsError("unreal.blueprint_factory_blocked",
                                   f"Die Elternklasse ließ sich der Blueprintfabrik nicht mitgeben: {error}")
            asset = tools.create_asset(name, folder, unreal.Blueprint, factory)
            form = "AssetTools.create_asset mit Blueprintfabrik"
    if asset is None:
        raise ops.OpsError("unreal.blueprint_create_failed",
                           f"Der Blueprint {folder}/{name} konnte nicht erzeugt werden.")
    return asset


@ops.operation("blueprint.create", "Erzeugt einen Blueprint mit wählbarer Elternklasse und Art.")
def op_blueprint_create(arguments, context):
    folder, name, target = ops.require_new_asset_path(arguments, "/Game/Blueprints")
    if ops.asset_exists(target) and not ops.flag(arguments, "replace", False):
        raise ops.OpsError("unreal.asset_exists", f"Der Blueprint {target} existiert bereits.")
    asset_type = ops.text(arguments, "assetType", "blueprint")
    _, default_parent = BLUEPRINT_KINDS.get(asset_type, (unreal.Blueprint, "Actor"))
    parent_name = ops.text(arguments, "parentClass") or default_parent
    parent = _parent_class(parent_name)
    blueprint = create_blueprint(name, folder, parent)
    # Speichern vor dem Kompilieren und immer über das Objekt: die Pfadspeicherung nach
    # einer Kompilierung trifft in dieser Engineversion auf ein nicht registriertes Objekt.
    ops.save_asset(target, blueprint)
    result = ops.blueprint_summary(blueprint)
    result["assetPath"] = target
    result["requestedParent"] = parent_name
    result["assetType"] = asset_type
    result["saved"] = ops.asset_exists(target)
    result["operationId"] = ops.text(arguments, "operationId")
    return result


@ops.operation("blueprint.read", "Liest Blueprint: Variablen, Bauteile, Graphen und Kompilierstatus.")
def op_blueprint_read(arguments, context):
    asset_path = ops.require_asset_path(arguments)
    blueprint = ops.load_asset(asset_path)
    if blueprint is None:
        raise ops.OpsError("unreal.blueprint_missing", f"Der Blueprint {asset_path} wurde nicht gefunden.")
    result = ops.blueprint_summary(blueprint)
    result["assetPath"] = asset_path
    result["classPath"] = ops.class_path(blueprint)
    graphs = []
    for graph_name in result.get("graphNames", []):
        entry = {"name": graph_name, "nodeCount": 0, "nodes": []}
        try:
            graph = unreal.BlueprintEditorLibrary.find_graph(blueprint, graph_name)
            if graph is not None:
                nodes = list(graph.get_editor_property("nodes") or [])
                entry["nodeCount"] = len(nodes)
                for node in nodes[:ops.MAX_ITEMS_REPORTED]:
                    title = ""
                    try:
                        title = str(unreal.BlueprintEditorLibrary.get_node_title(node))
                    except BaseException:
                        pass
                    pins = []
                    try:
                        for pin in unreal.BlueprintEditorLibrary.list_all_pins(node):
                            pins.append(str(pin))
                    except BaseException:
                        pass
                    entry["nodes"].append({"name": node.get_name(), "classPath": ops.class_path(node),
                                           "title": title, "pins": pins[:24]})
        except BaseException as error:
            entry["note"] = f"Graphinhalt nicht lesbar: {type(error).__name__}"
        graphs.append(entry)
    result["graphs"] = graphs
    result["graphEditLimit"] = GRAPH_EDIT_LIMIT
    result["operationId"] = ops.text(arguments, "operationId")
    return result


@ops.operation("blueprint.compile", "Kompiliert den Blueprint und meldet Status und Fehler.")
def op_blueprint_compile(arguments, context):
    asset_path = ops.require_asset_path(arguments)
    blueprint = ops.load_asset(asset_path)
    if blueprint is None:
        raise ops.OpsError("unreal.blueprint_missing", f"Der Blueprint {asset_path} wurde nicht gefunden.")
    try:
        compiled = bool(unreal.BlueprintEditorLibrary.compile_blueprint(blueprint))
    except BaseException as error:
        raise ops.OpsError("unreal.blueprint_compile_failed", f"{type(error).__name__}: {error}")
    status = ""
    try:
        status = str(blueprint.get_editor_property("status"))
    except BaseException:
        status = "nicht lesbar"
    generated = ""
    try:
        generated = str(unreal.BlueprintEditorLibrary.generated_class(blueprint))
    except BaseException:
        pass
    return {
        "assetPath": asset_path,
        "compiled": compiled,
        "status": status,
        "generatedClass": generated,
        "saved": ops.save_asset(asset_path, blueprint) if ops.flag(arguments, "save", True) else False,
        "operationId": ops.text(arguments, "operationId"),
    }


@ops.operation("blueprint.save", "Speichert den Blueprint im verbundenen Projekt.")
def op_blueprint_save(arguments, context):
    asset_path = ops.require_asset_path(arguments)
    if not ops.asset_exists(asset_path):
        raise ops.OpsError("unreal.blueprint_missing", f"Der Blueprint {asset_path} existiert nicht.")
    stored = ops.load_asset(asset_path)
    saved = ops.save_asset(asset_path, stored)
    return {"assetPath": asset_path, "saved": saved, "byObject": stored is not None,
            "existsOnDisk": ops.asset_exists(asset_path),
            "operationId": ops.text(arguments, "operationId")}


@ops.operation("blueprint.edit", "Bearbeitet Graphen, Variablen und Überschreibungen eines Blueprints.")
def op_blueprint_edit(arguments, context):
    asset_path = ops.require_asset_path(arguments)
    blueprint = ops.load_asset(asset_path)
    if blueprint is None:
        raise ops.OpsError("unreal.blueprint_missing", f"Der Blueprint {asset_path} wurde nicht gefunden.")
    operation = ops.text(arguments, "operation")
    library = unreal.BlueprintEditorLibrary
    name = ops.text(arguments, "name")
    done, notes = [], []

    if operation == "variable.add":
        if not name:
            raise ops.OpsError("unreal.variable_name_required", "Ein Variablenname fehlt.")
        declared = ops._names(library.list_member_variable_names, blueprint)
        if name in declared and not ops.flag(arguments, "replace", False):
            raise ops.OpsError("unreal.variable_exists", f"Die Variable {name} existiert bereits.")
        kind = ops.text(arguments, "type", "float")
        if kind not in VARIABLE_TYPES:
            raise ops.OpsError("unreal.variable_type_unsupported",
                               "Typen sind: " + ", ".join(sorted(VARIABLE_TYPES)))
        pin_type = make_pin_type(kind)
        accepted = False
        last_error = ""
        for variant in (
            lambda: bool(library.add_member_variable(blueprint, name, pin_type)),
            lambda: bool(library.add_member_variable(blueprint, name, kind)),
        ):
            try:
                if variant():
                    accepted = True
                    break
            except BaseException as error:
                last_error = f"{type(error).__name__}: {error}"
        if not accepted:
            raise ops.OpsError("unreal.variable_add_failed",
                               f"Die Variable {name} wurde nicht angenommen. Letzter Fehler: {last_error}")
        done.append(f"Variable {name} ({kind}) angelegt")
    elif operation == "variable.remove":
        try:
            library.remove_unused_variables(blueprint)
            notes.append("unbenutzte Variablen entfernt")
        except BaseException as error:
            notes.append(f"remove_unused_variables abgelehnt: {error}")
        found = name in ops._names(library.list_member_variable_names, blueprint)
        if not found:
            raise ops.OpsError("unreal.variable_missing", f"Die Variable {name} wurde nicht gefunden.")
        done.append(f"Variable {name} zum Entfernen gemeldet")
    elif operation in ("graph.create",):
        graph_name = ops.text(arguments, "graph") or name
        if not graph_name:
            raise ops.OpsError("unreal.graph_name_required", "Ein Graphname fehlt.")
        if graph_name in ops._names(library.list_graph_names, blueprint):
            raise ops.OpsError("unreal.graph_exists", f"Der Graph {graph_name} existiert bereits.")
        created = False
        last_error = ""
        for variant in (
            lambda: library.add_function_graph(blueprint, graph_name),
        ):
            try:
                variant()
                created = True
                break
            except BaseException as error:
                last_error = f"{type(error).__name__}: {error}"
        if not created:
            raise ops.OpsError("unreal.graph_create_failed",
                               f"Der Funktionsgraph {graph_name} wurde nicht angelegt. Letzter Fehler: {last_error}")
        done.append(f"Funktionsgraph {graph_name} angelegt")
    elif operation == "variable.default":
        if not set_variable_default(blueprint, name, str(arguments.get("value", ""))):
            raise ops.OpsError("unreal.variable_default_failed",
                               f"Der Standardwert für {name} wurde nicht angenommen.")
        done.append(f"Standardwert für {name} gesetzt")
    elif operation in ("graph.remove",):
        graph_name = ops.text(arguments, "graph") or name
        try:
            library.remove_graph(blueprint, graph_name)
            done.append(f"Graph {graph_name} entfernt")
        except BaseException as error:
            raise ops.OpsError("unreal.graph_remove_failed", f"{type(error).__name__}: {error}")
    elif operation in ("event.override",):
        event_name = ops.text(arguments, "eventName") or name
        spot = unreal.Vector(-1500.0, -1500.0, 0.0)
        accepted = False
        last_error = ""
        for variant in (
            lambda: library.add_event_override(blueprint, event_name, spot),
            lambda: library.add_event_override(blueprint, event_name),
        ):
            try:
                if variant() is not None:
                    accepted = True
                    break
            except BaseException as error:
                last_error = f"{type(error).__name__}: {error}"
        if not accepted:
            raise ops.OpsError("unreal.event_override_failed",
                               f"Das Ereignis {event_name} wurde nicht angenommen. Letzter Fehler: {last_error}")
        done.append(f"Ereignis {event_name} überschrieben")
    elif operation in ("node.add", "node.remove", "pin.connect", "pin.disconnect",
                       "component.add", "component.remove", "component.set"):
        raise ops.OpsError("unreal.capability_unavailable", GRAPH_EDIT_LIMIT)
    else:
        raise ops.OpsError("unreal.blueprint_operation_unknown",
                           "Erlaubt sind variable.add, variable.remove, graph.create, graph.remove, "
                           "event.override; node.*/pin.*/component.* sind über Python nicht vorgesehen.")

    notes.append("Kompilieren mit unreal.blueprint.compile")
    result = ops.blueprint_summary(blueprint)
    result["assetPath"] = asset_path
    result["changes"] = done
    result["notes"] = notes
    result["saved"] = ops.save_asset(asset_path, blueprint) if ops.flag(arguments, "save", True) else False
    result["operationId"] = ops.text(arguments, "operationId")
    return result


# Der Adapter sending pro Teilauftrag einen eigenen Kommandonamen, so wie es der frühere
# Vertrag vorsah. Diese Weiterleitungen halten diesen Vertrag, ohne ihn zu verbiegen.
SUB_OPERATIONS = {
    "blueprint.variable.add": "variable.add",
    "blueprint.variable.remove": "variable.remove",
    "blueprint.graph.create": "graph.create",
    "blueprint.graph.remove": "graph.remove",
    "blueprint.component.add": "component.add",
    "blueprint.component.remove": "component.remove",
    "blueprint.component.set": "component.set",
    "blueprint.node.add": "node.add",
    "blueprint.node.remove": "node.remove",
    "blueprint.pin.connect": "pin.connect",
    "blueprint.pin.disconnect": "pin.disconnect",
}


def _sub_operation_handler(sub_operation):
    def handler(arguments, context):
        merged = dict(arguments or {})
        merged.setdefault("operation", sub_operation)
        return op_blueprint_edit(merged, context)

    return handler


for _command_name, _sub_operation in SUB_OPERATIONS.items():
    ops.operation(_command_name, f"Blueprint-Teilauftrag {_sub_operation}.")(
        _sub_operation_handler(_sub_operation))


TEMPLATES = {
    "gameMode": {
        "assetName": "BP_MissionGameMode",
        "parentClass": "GameModeBase",
        "variables": [("objectiveCount", "int", "0"), ("missionActive", "bool", "false"),
                      ("dayLength", "float", "1200")],
        "graphs": ["BeginMission"],
    },
    "playerCharacter": {
        "assetName": "BP_PlayerCharacter",
        "parentClass": "Character",
        "variables": [("health", "float", "100"), ("cash", "int", "0"),
                      ("currentVehicle", "string", "")],
        "graphs": ["EnterVehicle"],
    },
    "npc": {
        "assetName": "BP_NPC_Citizen",
        "parentClass": "Character",
        "variables": [("alertLevel", "float", "0"), ("dialogueId", "string", "")],
        "graphs": ["ReactToPlayer"],
    },
    "questObjective": {
        "assetName": "DT_QuestObjective",
        "parentClass": "DataAsset",
        "variables": [("title", "string", ""), ("targetCount", "int", "1"),
                      ("rewardCash", "int", "500")],
        "graphs": [],
    },
    "questComponent": {
        "assetName": "BPC_QuestTracker",
        "parentClass": "ActorComponent",
        "variables": [("activeQuest", "string", ""), ("progress", "float", "0")],
        "graphs": ["TickProgress"],
    },
}


@ops.operation("blueprint.template", "Legt vordefinierte Baupläne an: gameMode, playerCharacter, npc, questObjective, questComponent.")
def op_blueprint_template(arguments, context):
    which = ops.text(arguments, "template")
    if which not in TEMPLATES:
        raise ops.OpsError("unreal.template_unknown",
                           "Baupläne sind: " + ", ".join(sorted(TEMPLATES)))
    spec = TEMPLATES[which]
    folder = ops.map_folder_for(ops.text(arguments, "folder", "/Game/Blueprints/Mission"))
    name = ops.text(arguments, "assetName") or spec["assetName"]
    target = f"{folder}/{name}"
    existing = ops.load_asset(target) if ops.asset_exists(target) else None
    if existing is not None and not ops.flag(arguments, "replace", False):
        raise ops.OpsError("unreal.asset_exists",
                           f"{target} existiert bereits. Mit replace wird ergänzt, nicht gelöscht.")
    # Vorhandene Assets werden weiterverwendet. Löschen und Neuanlegen desselben Pfads
    # hinterlässt in dieser Engineversion ungültige Referenzen und bringt den Editor beim
    # Speichern mit der Assertion Object->IsValidLowLevel() zum Absturz.
    blueprint = existing if existing is not None else create_blueprint(
        name, folder, _parent_class(spec["parentClass"]))
    library = unreal.BlueprintEditorLibrary
    created_variables, refused_variables = [], []
    for variable_name, kind, default in spec["variables"]:
        if variable_name in own_variable_names(blueprint):
            created_variables.append(f"{variable_name} (bereits vorhanden)")
            continue
        try:
            if bool(library.add_member_variable(blueprint, variable_name, make_pin_type(kind))):
                created_variables.append(f"{variable_name} ({kind})")
            else:
                refused_variables.append(f"{variable_name}: von der Engine abgelehnt")
        except BaseException as error:
            refused_variables.append(f"{variable_name}: {type(error).__name__}: {error}")
    if not created_variables and refused_variables:
        template_notes = ["Variablen wurden über add_member_variable nicht angenommen; in UE 5.8 ist die "
                          "Pin-Kategorie von EdGraphPinType für Python nicht exponiert (Fehler: Failed to find "
                          "property 'pin_category'), damit lassen sich keine Variablen über Epics Python anlegen"]
    else:
        template_notes = []
    created_graphs, refused_graphs = [], []
    for graph_name in spec["graphs"]:
        if graph_name in ops._names(library.list_graph_names, blueprint):
            created_graphs.append(f"{graph_name} (bereits vorhanden)")
            continue
        try:
            library.add_function_graph(blueprint, graph_name)
            created_graphs.append(graph_name)
        except BaseException as error:
            refused_graphs.append(f"{graph_name}: {type(error).__name__}")
    status = ""
    saved = ops.save_asset(target, blueprint)
    # Datenklassen und Bauteile lässt diese Engineversion über Python lange in der
    # Kompilierung laufen. Kompiliert wird deshalb nur bei den drei Klassen, deren
    # Kompilierung im laufenden Betrieb geprüft wurde, oder auf ausdrückliche Anforderung.
    auto_compile = which in ("gameMode", "playerCharacter", "npc")
    if ops.flag(arguments, "compile", auto_compile):
        try:
            compiled = bool(library.compile_blueprint(blueprint))
            status = "fehlerfrei" if compiled else attribute_status(blueprint)
        except BaseException as error:
            status = f"Kompilierung nicht möglich: {type(error).__name__}"
    else:
        status = "nicht kompiliert; mit compile=true ausdrücklich anfordern"
    game_mode_note = set_as_default_game_mode(target) if which == "gameMode" else None
    result = ops.blueprint_summary(blueprint)
    result.update({
        "assetPath": target,
        "template": which,
        "parentClass": spec["parentClass"],
        "variablesAdded": created_variables,
        "variablesRefused": refused_variables,
        "graphsAdded": created_graphs,
        "graphsRefused": refused_graphs,
        "compileStatus": status,
        "notes": template_notes,
        "saved": saved,
        "existsOnDisk": ops.asset_exists(target),
        "defaultGameMode": game_mode_note,
        "operationId": ops.text(arguments, "operationId"),
    })
    return result


def set_variable_default(blueprint, variable_name, value):
    """Setzt den Standardwert über die erzeugte Klasse, soweit die Engine ihn freigibt."""
    try:
        generated = unreal.BlueprintEditorLibrary.generated_class(blueprint)
        default = unreal.get_default_object(generated) if generated is not None else None
        if default is None:
            return False
        default.set_editor_property(variable_name, value)
        return True
    except BaseException:
        return False


def set_as_default_game_mode(asset_path):
    """Setzt den Spielmodus in Config/DefaultEngine.ini und liest ihn zurück."""
    generated = f"{asset_path}.{os.path.basename(asset_path)}_C"
    try:
        config = os.path.join(str(unreal.Paths.project_config_dir()), "DefaultEngine.ini")
    except BaseException as error:
        return {"set": False, "reason": f"Konfigurationsordner nicht bestimmbar: {error}"}
    values = {"GameDefaultMap": None, "EditorStartupMap": None}
    section = "/Script/EngineSettings.GameMapsSettings"
    verified = False
    try:
        verified = ops.write_ini_values(config, section, {"GameMode": generated,
                                                          "DefaultGameMode": generated})
    except BaseException as error:
        return {"set": False, "reason": f"{type(error).__name__}: {error}", "value": generated}
    return {"set": bool(verified), "file": config, "value": generated, "readBackVerified": bool(verified)}
