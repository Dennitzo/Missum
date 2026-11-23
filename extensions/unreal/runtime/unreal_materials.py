# SPDX-License-Identifier: MIT
"""Materialwerkzeuge des Missum-Unreal-Kanals.

Dieses Modul trägt sich in das Operationsregister von `unreal_ops` ein und wird von
`unreal_bootstrap` nach dem Kern geladen. Alle Aufrufe geschehen, wie im Kern, nur auf dem
Spielthread. Was die Engine über Python nicht freigibt, wird als Grenze gemeldet, statt als
nicht ausgeführte Änderung zurückzukommen.
"""

import unreal

import unreal_ops as ops


EXPRESSION_KINDS = {
    "texture": "MaterialExpressionTextureObjectParameter",
    "uv": "MaterialExpressionTextureCoordinate",
    "multiply": "MaterialExpressionMultiply",
    "vector": "MaterialExpressionVectorParameter",
    "scalar": "MaterialExpressionScalarParameter",
    "constant": "MaterialExpressionConstant",
    "constant3": "MaterialExpressionConstant3Vector",
    "fresnel": "MaterialExpressionFresnel",
    "noise": "MaterialExpressionNoise",
    "panner": "MaterialExpressionPanner",
    "add": "MaterialExpressionAdd",
    "oneMinus": "MaterialExpressionOneMinus",
    "lerp": "MaterialExpressionLinearInterpolate",
}

MATERIAL_PROPERTIES = {
    "baseColor": "MP_BASE_COLOR",
    "metallic": "MP_METALLIC",
    "roughness": "MP_ROUGHNESS",
    "specular": "MP_SPECULAR",
    "emissive": "MP_EMISSIVE_COLOR",
    "opacity": "MP_OPACITY",
    "opacityMask": "MP_OPACITYMASK",
    "normal": "MP_NORMAL",
    "ambientOcclusion": "MP_AMBIENT_OCCLUSION",
    "subsurfaceColor": "MP_SUBSURFACE_COLOR",
}


def library():
    return unreal.MaterialEditingLibrary


def expression_class(kind):
    class_name = ops.EXPRESSION_KINDS.get(kind, kind) if hasattr(ops, "EXPRESSION_KINDS") else EXPRESSION_KINDS.get(kind, kind)
    if not class_name.startswith("MaterialExpression"):
        raise ops.OpsError("unreal.expression_unknown",
                           "Ausdrucksknoten sind: " + ", ".join(sorted(EXPRESSION_KINDS)))
    value = getattr(unreal, class_name, None)
    if value is None:
        raise ops.OpsError("unreal.expression_unavailable",
                           f"Diese Engineversion stellt {class_name} nicht über Python zur Verfügung.")
    return value


def create_expression(material, kind, x=0, y=0, label=""):
    node_class = expression_class(kind)
    try:
        expression = library().create_material_expression(material, node_class, int(x), int(y))
    except BaseException as error:
        raise ops.OpsError("unreal.expression_create_failed",
                           f"{type(error).__name__} beim Anlegen von {node_class}: {error}")
    if expression is None:
        raise ops.OpsError("unreal.expression_create_failed", f"{node_class} wurde nicht erzeugt.")
    if label:
        ops.set_if_present(expression, "parameter_name", label)
    return expression


def material_property(key):
    name = MATERIAL_PROPERTIES.get(key, key)
    value = getattr(unreal.MaterialProperty, name, None)
    if value is None:
        raise ops.OpsError("unreal.material_property_unknown",
                           f"Materialproperty {key} ist unbekannt. Zur Wahl stehen: "
                           + ", ".join(sorted(MATERIAL_PROPERTIES)))
    return value


def connect_expression_to_property(expression, property_key, material=None, output_name="output"):
    """Verdrahtet einen Ausdruck auf eine Materialeigenschaft.

    Die Engine antwortet hier mit nichts; ein Durchlauf ohne Ausnahme gilt als_success.
    Nur eine abgelehnte Form wird als Misserfolg gemeldet.
    """
    prop = material_property(property_key)
    variants = [(expression, output_name, prop)]
    if material is not None:
        variants.insert(0, (material, expression, output_name, prop))
    _value, used = ops.call_any(library(), "connect_material_property", variants)
    return True


def connect_two_expressions(source, target, source_output="output", target_input="input"):
    _value, used = ops.call_any(library(), "connect_material_expressions",
                                [(source, source_output, target, target_input),
                                 (target, target_input, source, source_output)])
    return True


def recompile_and_report(material):
    notes = []
    try:
        library().recompile_material(material)
        notes.append("recompile_material ausgeführt")
    except BaseException as error:
        notes.append(f"recompile_material abgelehnt: {type(error).__name__}: {error}")
    return notes


def _extend_existing_material(existing, arguments, target, kind):
    """Ergänzt ein vorhandenes Material um Startwerte, ohne es zu löschen."""
    applied, refused = [], []
    if kind == "MaterialInstanceConstant":
        parent_path = ops.text(arguments, "parentMaterial")
        if parent_path:
            parent = ops.load_asset(parent_path)
            if parent is not None:
                try:
                    library().set_material_instance_parent(existing, parent)
                    applied.append(f"Elternmaterial {parent_path}")
                except BaseException as error:
                    refused.append(f"Elternmaterial: {error}")
        for key, setter, form in (("scalars", "set_material_instance_scalar_parameter_value", "float"),
                                  ("vectors", "set_material_instance_vector_parameter_value", "color"),
                                  ("staticSwitches", "set_material_instance_static_switch_parameter_value", "flag")):
            for parameter, value in ops.mapping(arguments, key).items():
                function = getattr(unreal.MaterialEditingLibrary, setter, None)
                if function is None:
                    refused.append(f"{setter} fehlt")
                    continue
                try:
                    if form == "color":
                        function(existing, parameter, linear_color(value if isinstance(value, dict) else {}))
                    elif form == "flag":
                        function(existing, parameter, bool(value))
                    else:
                        function(existing, parameter, float(value))
                    applied.append(f"{parameter} ({key})")
                except BaseException as error:
                    refused.append(f"{parameter}: {type(error).__name__}")
    else:
        # replace on a plain Material used to ignore every start value (applied: []).
        apply_material_values(existing, arguments, applied, refused)
    notes = recompile_and_report(existing)
    saved = ops.save_asset(target, existing) if ops.flag(arguments, "save", True) else False
    return {
        "assetPath": target,
        "assetClass": kind,
        "reusedExistingAsset": True,
        "applied": applied,
        "refused": refused,
        "notes": notes,
        "saved": saved,
        "existsOnDisk": ops.asset_exists(target),
        "operationId": ops.text(arguments, "operationId"),
    }


def linear_color(values):
    return unreal.LinearColor(float(values.get("r", 1.0)), float(values.get("g", 1.0)),
                              float(values.get("b", 1.0)), float(values.get("a", 1.0)))


def color_value(arguments, name):
    """Reads a color as {r,g,b[,a]}, [r,g,b[,a]] or "#RRGGBB[AA]"; None when absent.

    A color without any of r, g, b is refused: it used to become plain white silently, which
    left every material of a live run without its intended look.
    """
    raw = arguments.get(name) if isinstance(arguments, dict) else None
    if raw is None:
        return None
    if isinstance(raw, str):
        text_value = raw.strip().lstrip("#")
        if len(text_value) in (6, 8) and all(c in "0123456789abcdefABCDEF" for c in text_value):
            channels = [int(text_value[i:i + 2], 16) / 255.0 for i in range(0, len(text_value), 2)]
            return dict(zip("rgba", channels + [1.0] * (4 - len(channels))))
    elif isinstance(raw, (list, tuple)) and 3 <= len(raw) <= 4 and all(isinstance(v, (int, float)) for v in raw):
        values = [float(v) for v in raw]
        return dict(zip("rgba", values + [1.0] * (4 - len(values))))
    elif isinstance(raw, dict) and any(key in raw for key in ("r", "g", "b")):
        return {key: float(raw.get(key, 0.0 if key != "a" else 1.0)) for key in "rgba"}
    raise ops.OpsError("unreal.material_color_invalid",
                       f"{name} braucht r, g und b (0–1), z. B. {{\"r\":0.1,\"g\":0.4,\"b\":0.9}}; "
                       f"erhalten: {str(raw)[:80]}.")


# Parameter nodes Missum creates for its start values; edits and replace update them in place.
VALUE_PARAMETERS = (
    ("baseColor", "vector", "BaseColor", "baseColor"),
    ("metallic", "scalar", "Metallic", "metallic"),
    ("roughness", "scalar", "Roughness", "roughness"),
    ("specular", "scalar", "Specular", "specular"),
    ("opacity", "scalar", "Opacity", "opacity"),
    ("emissiveColor", "vector", "EmissiveColor", None),
    ("emissiveIntensity", "scalar", "EmissiveIntensity", None),
)


def _parameter_node(material, parameter):
    """The material's own parameter node with this name; never a global object lookup."""
    for item in library().get_material_expressions(material) or []:
        if str(ops.attribute(item, "parameter_name") or "") == parameter:
            return item
    return None


def apply_material_values(material, arguments, applied, refused):
    """Sets start values on an existing Material: updates its parameter node or adds one."""
    missing_emissive = {}
    for position, (key, kind, parameter, property_key) in enumerate(VALUE_PARAMETERS):
        value = color_value(arguments, key) if kind == "vector" else ops.number(arguments, key)
        if value is None:
            continue
        converted = linear_color(value) if kind == "vector" else float(value)
        expression = _parameter_node(material, parameter)
        if expression is not None:
            ops.set_if_present(expression, "default_value", converted)
            applied.append(f"{parameter} aktualisiert")
            continue
        if property_key is None:
            missing_emissive[parameter] = converted
            continue
        try:
            created = create_expression(material, kind, -400, 220 * position, parameter)
            ops.set_if_present(created, "default_value", converted)
            if connect_expression_to_property(created, property_key, material):
                applied.append(f"{parameter} -> {property_key}")
            else:
                refused.append(f"{parameter} -> {property_key}")
        except BaseException as error:
            refused.append(f"{parameter}: {error}")
    if missing_emissive:
        _add_emissive_chain(material, missing_emissive, applied, refused)


def _add_emissive_chain(material, missing, applied, refused):
    """Adds EmissiveColor x EmissiveIntensity -> Emissive when the material has neither node yet."""
    existing = []
    for parameter in ("EmissiveColor", "EmissiveIntensity"):
        if _parameter_node(material, parameter) is not None:
            existing.append(parameter)
    if existing:
        for parameter in missing:
            refused.append(f"{parameter}: Emissive ist schon teilweise verdrahtet ({', '.join(existing)}); "
                           "fehlenden Knoten mit edit=expression.add ergänzen")
        return
    try:
        color = create_expression(material, "vector", -400, 1600, "EmissiveColor")
        ops.set_if_present(color, "default_value", missing.get("EmissiveColor", linear_color({})))
        intensity = create_expression(material, "scalar", -400, 1710, "EmissiveIntensity")
        ops.set_if_present(intensity, "default_value", float(missing.get("EmissiveIntensity", 1.0)))
        multiplier = create_expression(material, "multiply", -100, 1600, "")
        connect_two_expressions(color, multiplier, "output", "a")
        connect_two_expressions(intensity, multiplier, "output", "b")
        connect_expression_to_property(multiplier, "emissive", material)
        applied.append("EmissiveColor x EmissiveIntensity -> emissive")
    except BaseException as error:
        refused.append(f"emissive: {error}")


def resolve_expression(material, handle):
    """Findet einen Ausdruck über Objektlaufwerk, Parameternamen oder Klasse#Index."""
    if not handle:
        raise ops.OpsError("unreal.expression_reference_required",
                           "Ein Ausdruck wird über expressionPath angegeben: Parametername, "
                           "Laufwerkpfad oder Klassenname#Index aus unreal.material.read.")
    try:
        direct = unreal.find_object(None, handle)
        if direct is not None:
            return direct
    except BaseException:
        pass
    expressions = library().get_material_expressions(material) or []
    if "#" in handle:
        class_name, _, index_text = handle.rpartition("#")
        matches = [item for item in expressions if class_path_ends_with(item, class_name)]
        try:
            return matches[int(index_text)]
        except (IndexError, ValueError):
            raise ops.OpsError("unreal.expression_missing", f"{handle} passt zu keinem Ausdrucksknoten.")
    for item in expressions:
        if str(ops.attribute(item, "parameter_name") or "") == handle:
            return item
    for item in expressions:
        if item.get_path_name() == handle or item.get_name() == handle:
            return item
    raise ops.OpsError("unreal.expression_missing", f"{handle} passt zu keinem Ausdrucksknoten.")


def class_path_ends_with(actor_object, class_name):
    path = ops.class_path(actor_object)
    return path.lower().endswith(class_name.lower())


def users_of_material(material_path):
    """Welche Schauspieler nutzen dieses Material, und in welchem Feld?"""
    users = []
    world = ops.current_world()
    if world is None:
        return users
    for actor in ops.actors_in_world(world):
        try:
            components = list(actor.get_components_by_class(unreal.PrimitiveComponent))
        except BaseException:
            components = []
        for component in components:
            try:
                materials = component.get_materials()
            except BaseException:
                continue
            for index, material in enumerate(materials or []):
                if material is None:
                    continue
                path = material.get_path_name()
                parent = str(ops.attribute(material, "parent") or "")
                if path == material_path or path.startswith(material_path + ".") or parent == material_path:
                    users.append({
                        "actor": actor.get_name(),
                        "label": actor.get_actor_label(),
                        "component": component.get_name(),
                        "slot": index,
                        "material": path,
                    })
                if len(users) >= ops.MAX_ITEMS_REPORTED:
                    return users
    return users


@ops.operation("material.create", "Erzeugt ein Material oder eine Materialinstanz mit Startwerten.")
def op_material_create(arguments, context):
    folder, name, target = ops.require_new_asset_path(arguments, "/Game/Materials")
    existing = ops.load_asset(target) if ops.asset_exists(target) else None
    if existing is not None and not ops.flag(arguments, "replace", False):
        raise ops.OpsError("unreal.asset_exists",
                           f"Das Material {target} existiert bereits. Mit replace wird es ergänzt, nicht gelöscht.")

    # Vorhandene Assets werden weiterverwendet statt gelöscht und neu erzeugt: das
    # Löschen desselben Pfads hinterlässt hier ungültige Referenzen im Editor.
    if existing is not None:
        kind = "MaterialInstanceConstant" if ops.text(arguments, "parentMaterial") else "Material"
        return _extend_existing_material(existing, arguments, target, kind)

    parent_path = ops.text(arguments, "parentMaterial")
    tools = unreal.AssetToolsHelpers.get_asset_tools()
    created = None
    kind = "Material"
    if parent_path:
        ops.valid_asset_path(parent_path)
        parent = ops.load_asset(parent_path)
        if parent is None:
            raise ops.OpsError("unreal.material_parent_missing",
                               f"Das Elternmaterial {parent_path} wurde nicht gefunden.")
        factory_class = getattr(unreal, "MaterialInstanceConstantFactoryNew", None)
        created = tools.create_asset(name, folder, unreal.MaterialInstanceConstant,
                                     factory_class() if factory_class is not None else None)
        kind = "MaterialInstanceConstant"
        if created is not None:
            try:
                library().set_material_instance_parent(created, parent)
            except BaseException as error:
                raise ops.OpsError("unreal.material_parent_failed", f"Die Elternzuweisung misslang: {error}")
    else:
        created = tools.create_asset(name, folder, unreal.Material, unreal.MaterialFactoryNew())

    if created is None:
        raise ops.OpsError("unreal.material_create_failed", f"Das Material {target} konnte nicht erzeugt werden.")

    applied, refused = [], []
    if kind == "Material":
        recipe = []
        base_color = color_value(arguments, "baseColor")
        if base_color:
            recipe.append(("baseColor", "vector", "BaseColor", base_color))
        for key, parameter in (("metallic", "Metallic"), ("roughness", "Roughness"),
                               ("specular", "Specular"), ("opacity", "Opacity")):
            value = ops.number(arguments, key)
            if value is not None:
                recipe.append((key, "scalar", parameter, float(value)))
        emissive_color = color_value(arguments, "emissiveColor")
        emissive_intensity = ops.number(arguments, "emissiveIntensity")

        # Alle Zieleigenschaften vor dem ersten Ausdruck prüfen: sonst bliebe ein halbfertiges Asset.
        for property_key, _, _, _ in recipe:
            material_property(property_key)
        if emissive_color or emissive_intensity is not None:
            material_property("emissive")

        for position, (property_key, expression_kind, parameter_name, value) in enumerate(recipe):
            expression = create_expression(created, expression_kind, 0, 220 * position, parameter_name)
            ops.set_if_present(expression, "default_value",
                               linear_color(value) if expression_kind == "vector" else float(value))
            try:
                if connect_expression_to_property(expression, property_key, created):
                    applied.append(f"{parameter_name} -> {property_key}")
            except (ops.OpsError, BaseException) as error:
                refused.append(f"{parameter_name} -> {property_key}: {error}")

        if emissive_color or emissive_intensity is not None:
            row = 220 * max(len(recipe), 1)
            color_expression = None
            intensity_expression = None
            if emissive_color:
                color_expression = create_expression(created, "vector", 0, row, "EmissiveColor")
                ops.set_if_present(color_expression, "default_value", linear_color(emissive_color))
            if emissive_intensity is not None:
                intensity_expression = create_expression(created, "scalar", 0, row + 110, "EmissiveIntensity")
                ops.set_if_present(intensity_expression, "default_value", float(emissive_intensity))
            try:
                if color_expression is not None and intensity_expression is not None:
                    multiplier = create_expression(created, "multiply", 320, row, "")
                    connect_two_expressions(color_expression, multiplier, "output", "a")
                    connect_two_expressions(intensity_expression, multiplier, "output", "b")
                    connect_expression_to_property(multiplier, "emissive", created)
                    applied.append("EmissiveColor x EmissiveIntensity -> emissive")
                elif color_expression is not None:
                    connect_expression_to_property(color_expression, "emissive", created)
                    applied.append("EmissiveColor -> emissive")
                else:
                    connect_expression_to_property(intensity_expression, "emissive", created)
                    applied.append("EmissiveIntensity -> emissive")
            except BaseException as error:
                refused.append(f"emissive: {error}")
        texture_path = ops.text(arguments, "texture")
        if texture_path:
            ops.valid_asset_path(texture_path)
            texture = ops.load_asset(texture_path)
            if texture is None:
                refused.append(f"Textur {texture_path} nicht gefunden")
            else:
                expression = create_expression(created, "texture", -420, -220, "BaseColorTexture")
                ops.set_if_present(expression, "texture", texture)
                uv = create_expression(created, "uv", -720, -100)
                connect_two_expressions(uv, expression, "output", "uv_coordinates")
                if connect_expression_to_property(expression, "baseColor", created):
                    applied.append("BaseColorTexture -> baseColor")
                else:
                    refused.append("BaseColorTexture -> baseColor")
    else:
        setters = (
            ("scalars", "set_material_instance_scalar_parameter_value", "float"),
            ("vectors", "set_material_instance_vector_parameter_value", "color"),
            ("staticSwitches", "set_material_instance_static_switch_parameter_value", "flag"),
        )
        for key, setter, form in setters:
            for parameter, value in ops.mapping(arguments, key).items():
                function = getattr(unreal.MaterialEditingLibrary, setter, None)
                if function is None:
                    refused.append(f"{setter} fehlt in dieser Engineversion")
                    continue
                try:
                    if form == "color":
                        function(created, parameter, linear_color(value if isinstance(value, dict) else {}))
                    elif form == "flag":
                        function(created, parameter, bool(value))
                    else:
                        function(created, parameter, float(value))
                    applied.append(f"{parameter} ({key})")
                except BaseException as error:
                    refused.append(f"{parameter}: {type(error).__name__}")

    notes = recompile_and_report(created)
    saved = ops.save_asset(target, created) if ops.flag(arguments, "save", True) else False
    return {
        "assetPath": target,
        "assetClass": kind,
        "parentMaterial": parent_path or None,
        "applied": applied,
        "refused": refused,
        "notes": notes,
        "saved": saved,
        "existsOnDisk": ops.asset_exists(target),
        "operationId": ops.text(arguments, "operationId"),
    }


@ops.operation("material.read", "Parameter, Ausdrucksknoten, Eigenschaften und die Nutzer eines Materials.")
def op_material_read(arguments, context):
    asset_path = ops.require_asset_path(arguments)
    asset = ops.load_asset(asset_path)
    if asset is None:
        raise ops.OpsError("unreal.material_missing", f"Das Material {asset_path} wurde nicht gefunden.")
    summary = ops.material_summary(asset)
    if isinstance(asset, unreal.MaterialInstanceConstant):
        values = {}
        for parameter in summary.get("scalarParameters", []):
            try:
                values[parameter] = float(library().get_material_instance_scalar_parameter_value(asset, parameter))
            except BaseException:
                pass
        for parameter in summary.get("vectorParameters", []):
            try:
                values[parameter] = ops.vector_dict(
                    library().get_material_instance_vector_parameter_value(asset, parameter))
            except BaseException:
                pass
        for parameter in summary.get("textureParameters", []):
            try:
                texture = library().get_material_instance_texture_parameter_value(asset, parameter)
                values[parameter] = texture.get_path_name() if texture is not None else None
            except BaseException:
                pass
        if values:
            summary["instanceValues"] = values
    connections = {}
    for property_key in ("baseColor", "metallic", "roughness", "emissive", "opacity"):
        try:
            node = library().get_material_property_input_node(asset, material_property(property_key))
            if node is not None:
                connections[property_key] = ops.class_path(node)
        except BaseException:
            continue
    if connections:
        summary["propertyInputs"] = connections
    using = users_of_material(asset_path)
    return {
        "assetPath": asset_path,
        "classPath": ops.class_path(asset),
        "material": summary,
        "usedBy": using,
        "usedByCount": len(using),
        "operationId": ops.text(arguments, "operationId"),
    }


@ops.operation("material.edit", "Setzt Parameter, ergänzt Ausdrucksknoten, verdrahtet und recompiliert.")
def op_material_edit(arguments, context):
    asset_path = ops.require_asset_path(arguments)
    asset = ops.load_asset(asset_path)
    if asset is None:
        raise ops.OpsError("unreal.material_missing", f"Das Material {asset_path} wurde nicht gefunden.")
    operation = ops.text(arguments, "operation")
    done, notes = [], []

    if operation == "values":
        # Direct start values (baseColor, metallic, ...) as the model naturally sends them.
        if isinstance(asset, unreal.MaterialInstanceConstant):
            raise ops.OpsError("unreal.material_values_instance",
                               "Materialinstanzen ändern ihre Werte mit edit=parameter (type scalar/vector).")
        refused = []
        apply_material_values(asset, arguments, done, refused)
        if not done and not refused:
            raise ops.OpsError("unreal.material_values_missing",
                               "Keine Werte angegeben: baseColor, emissiveColor, metallic, roughness, specular, opacity oder emissiveIntensity.")
        notes.extend(f"nicht übernommen: {item}" for item in refused)

    elif operation == "parameter":
        kind = ops.text(arguments, "type", "scalar")
        parameter = ops.text(arguments, "name")
        if not parameter:
            raise ops.OpsError("unreal.parameter_name_required", "Ein Parametername fehlt.")
        if isinstance(asset, unreal.MaterialInstanceConstant):
            if kind == "scalar":
                library().set_material_instance_scalar_parameter_value(
                    asset, parameter, float(ops.number(arguments, "value", 0.0)))
            elif kind == "vector":
                library().set_material_instance_vector_parameter_value(
                    asset, parameter, linear_color(ops.mapping(arguments, "value")))
            elif kind == "texture":
                library().set_material_instance_texture_parameter_value(
                    asset, parameter, ops.load_asset(ops.text(arguments, "texturePath")))
            elif kind == "staticSwitch":
                library().set_material_instance_static_switch_parameter_value(
                    asset, parameter, ops.flag(arguments, "value", False))
            else:
                raise ops.OpsError("unreal.parameter_type_unknown",
                                   "Typen sind scalar, vector, texture und staticSwitch.")
            try:
                library().update_material_instance(asset)
            except BaseException:
                pass
            done.append(f"Parameter {parameter} ({kind}) gesetzt")
        else:
            target_expression = resolve_expression(asset, parameter)
            if kind == "vector":
                ops.set_if_present(target_expression, "default_value", linear_color(ops.mapping(arguments, "value")))
            elif kind == "texture":
                ops.set_if_present(target_expression, "texture", ops.load_asset(ops.text(arguments, "texturePath")))
            else:
                ops.set_if_present(target_expression, "default_value", float(ops.number(arguments, "value", 0.0)))
            done.append(f"Ausdruck {parameter} ({kind}) geändert")

    elif operation == "expression.add":
        expression = create_expression(asset, ops.text(arguments, "kind"),
                                       int(ops.number(arguments, "x", 0)),
                                       int(ops.number(arguments, "y", 0)),
                                       ops.text(arguments, "name"))
        done.append(f"{ops.class_path(expression)} angelegt")
        notes.append(f"Kennung für weitere Bearbeitung: {expression.get_path_name()}")

    elif operation == "expression.remove":
        target_expression = resolve_expression(asset, ops.text(arguments, "expressionPath"))
        try:
            library().delete_material_expression(asset, target_expression)
            done.append("Ausdruck entfernt")
        except BaseException as error:
            raise ops.OpsError("unreal.expression_remove_failed", f"{type(error).__name__}: {error}")

    elif operation == "expression.connect":
        source = resolve_expression(asset, ops.text(arguments, "fromExpression"))
        target = resolve_expression(asset, ops.text(arguments, "toExpression"))
        if connect_two_expressions(source, target, ops.text(arguments, "fromOutput", "output"),
                                  ops.text(arguments, "toInput", "input")):
            done.append("Ausdrücke verbunden")
        else:
            notes.append("Die Verbindung wurde von der Engine abgelehnt")

    elif operation == "property.connect":
        expression = resolve_expression(asset, ops.text(arguments, "expressionPath"))
        if connect_expression_to_property(expression, ops.text(arguments, "property"), asset,
                                         ops.text(arguments, "output", "output")):
            done.append(f"Ausdruck auf {ops.text(arguments, 'property')} gelegt")
        else:
            notes.append("Die Zuordnung auf die Eigenschaft wurde abgelehnt")

    elif operation in ("blendMode", "shadingModel", "usage"):
        value = ops.text(arguments, "value")
        if operation == "usage":
            flag_name = ops.text(arguments, "name")
            member = getattr(unreal.MaterialUsage, flag_name, None)
            if member is None:
                raise ops.OpsError("unreal.material_usage_unknown", f"{flag_name} ist keine Materialnutzung.")
            try:
                library().set_material_usage(asset, member, ops.flag(arguments, "value", True))
                done.append(f"Nutzungsmarke {flag_name} gesetzt")
            except BaseException as error:
                raise ops.OpsError("unreal.material_usage_failed", f"{type(error).__name__}: {error}")
        else:
            enum_type = unreal.BlendMode if operation == "blendMode" else unreal.ShadingModels
            member = getattr(enum_type, value, None)
            if member is None:
                raise ops.OpsError("unreal.material_enum_unknown", f"{value} ist kein Wert von {enum_type}.")
            property_name = "blend_mode" if operation == "blendMode" else "shading_model"
            if not ops.set_if_present(asset, property_name, member):
                raise ops.OpsError("unreal.material_flag_unassigned",
                                   f"{property_name} lässt sich in dieser Engineversion nicht über Python setzen.")
            done.append(f"{operation} = {value}")

    elif operation == "recompile":
        pass
    else:
        raise ops.OpsError("unreal.material_operation_unknown",
                           "Operationen sind parameter, expression.add, expression.remove, "
                           "expression.connect, property.connect, blendMode, shadingModel, usage, recompile.")

    notes.extend(recompile_and_report(asset))
    saved = ops.save_asset(asset_path, asset) if ops.flag(arguments, "save", True) else False
    return {
        "assetPath": asset_path,
        "operation": operation,
        "changes": done,
        "notes": notes,
        "saved": saved,
        "material": ops.material_summary(asset),
        "operationId": ops.text(arguments, "operationId"),
    }


@ops.operation("material.assign", "Weist ein Material einem Feld, allen Feldern oder einem Blueprint zu.")
def op_material_assign(arguments, context):
    material_path = ops.require_asset_path(arguments, "materialPath")
    material = ops.load_asset(material_path)
    if material is None:
        raise ops.OpsError("unreal.material_missing", f"Das Material {material_path} wurde nicht gefunden.")
    applied, refused = [], []
    actor_name = ops.text(arguments, "actorName")
    mesh_path = ops.text(arguments, "meshAssetPath")
    blueprint_path = ops.text(arguments, "blueprintPath")
    slot_name = ops.text(arguments, "slotName")
    slot_index = ops.number(arguments, "slot")
    every = ops.flag(arguments, "allSlots", False)

    if actor_name:
        world = ops.current_world()
        if world is None:
            raise ops.OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")
        actor = ops.find_actor(world, actor_name)
        component_path = ops.text(arguments, "componentPath")
        try:
            components = list(actor.get_components_by_class(unreal.PrimitiveComponent))
        except BaseException:
            components = []
        if component_path:
            components = [item for item in components if item.get_name() == component_path]
        for component in components:
            try:
                count = int(component.get_num_materials())
            except BaseException:
                count = 0
            if count == 0:
                refused.append(f"{component.get_name()} meldet keine Materialfelder")
                continue
            for index in range(count):
                if not every and slot_index is not None and index != int(slot_index):
                    continue
                try:
                    if slot_name:
                        component.set_material_by_name(slot_name, material)
                    else:
                        component.set_material(index, material)
                    applied.append({"ziel": f"{actor_name}/{component.get_name()}", "slot": index})
                except BaseException as error:
                    refused.append(f"{component.get_name()}[{index}]: {type(error).__name__}")
        ops.mark_world_dirty(world)
    elif mesh_path:
        ops.valid_asset_path(mesh_path)
        mesh = ops.load_asset(mesh_path)
        if mesh is None:
            raise ops.OpsError("unreal.asset_missing", f"Das Netz {mesh_path} wurde nicht gefunden.")
        try:
            count = int(mesh.get_num_sections(0))
        except BaseException:
            count = 0
        for index in range(count):
            if not every and slot_index is not None and index != int(slot_index):
                continue
            _value, used = ops.call_any(mesh, "set_material",
                                        [(0, index, material), (index, material)],
                                        error_code="unreal.mesh_material_assign_failed")
            applied.append({"ziel": mesh_path, "slot": index, "form": str(used)})
        ops.save_asset(mesh_path, mesh)
    elif blueprint_path:
        ops.valid_asset_path(blueprint_path)
        blueprint = ops.load_asset(blueprint_path)
        if blueprint is None:
            raise ops.OpsError("unreal.blueprint_missing", f"Der Blueprint {blueprint_path} wurde nicht gefunden.")
        try:
            script = blueprint.get_editor_property("simple_construction_script")
            nodes = script.get_all_nodes() if script is not None else []
        except BaseException as error:
            raise ops.OpsError("unreal.blueprint_components_unreadable",
                               f"Die Bauteile des Blueprints sind über Python nicht erreichbar: {error}")
        for node in nodes:
            template = node.get_editor_property("component_template")
            if template is None:
                continue
            try:
                count = int(template.get_num_materials())
            except BaseException:
                continue
            for index in range(count):
                if not every and slot_index is not None and index != int(slot_index):
                    continue
                try:
                    template.set_material(index, material)
                    applied.append({"ziel": f"{blueprint_path}/{node.get_name()}", "slot": index})
                except BaseException as error:
                    refused.append(f"{node.get_name()}[{index}]: {type(error).__name__}")
        ops.save_asset(blueprint_path, blueprint)
    else:
        raise ops.OpsError("unreal.assign_target_required", "actorName, meshAssetPath oder blueprintPath fehlt.")

    using = users_of_material(material_path)
    return {
        "materialPath": material_path,
        "assigned": applied,
        "assignedCount": len(applied),
        "refused": refused,
        "usedBy": using,
        "usedByCount": len(using),
        "operationId": ops.text(arguments, "operationId"),
    }
