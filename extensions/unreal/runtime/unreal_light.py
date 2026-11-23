# SPDX-License-Identifier: MIT
"""Licht-, Nebel- und Himmelswerkzeuge des Missum-Unreal-Kanals.

Trägt sich in das Register von `unreal_ops` ein. Lichtquellen werden über das
EditorActorSubsystem gesetzt; Werte, die diese Engineversion nicht annimmt, werden als
Grenze gemeldet statt still übersprungen.
"""

import math

import unreal

import unreal_ops as ops


LIGHT_KINDS = {
    "directional": "DirectionalLight",
    "point": "PointLight",
    "spot": "SpotLight",
    "rect": "RectLight",
    "sky": "SkyLight",
    "fog": "ExponentialHeightFog",
    "postProcess": "PostProcessVolume",
}


def _light_class(kind):
    class_name = LIGHT_KINDS.get(kind, kind)
    value = getattr(unreal, class_name, None)
    if value is None:
        raise ops.OpsError("unreal.light_kind_unavailable",
                           f"Die Lichtart {class_name} steht in dieser Engineversion nicht zur Verfügung. "
                           "Zur Wahl stehen: " + ", ".join(sorted(LIGHT_KINDS)))
    return value


def spawn_light(kind, name, position, rotation):
    handler = ops.actor_subsystem()
    if handler is None:
        raise ops.OpsError("unreal.actor_subsystem_missing", "Der Editor stellt kein EditorActorSubsystem zur Verfügung.")
    created, form = ops.call_any(handler, "spawn_actor_from_class",
                                 [(_light_class(kind), position, rotation)])
    if created is None:
        raise ops.OpsError("unreal.light_spawn_failed", f"Die Lichtart {kind} konnte nicht gesetzt werden.")
    if name:
        try:
            created.set_actor_label(name)
        except BaseException:
            pass
    return created


def find_light(world, wanted):
    return ops.find_actor(world, wanted)


def light_targets(actor):
    """Light settings live on the component in UE 5 (PointLight.light_component, SkyLight.
    sky_light_component, ExponentialHeightFog.component); the actor itself is only a fallback.
    Writing to the actor alone refused intensity, temperature and radius in a live run."""
    targets = []
    for name in ("light_component", "sky_light_component", "component", "root_component"):
        component = ops.attribute(actor, name)
        if component is not None and not callable(component) and all(component is not t for t in targets):
            targets.append(component)
    targets.append(actor)
    return targets


def _set_on_targets(targets, property_name, value):
    for target in targets:
        if ops.set_if_present(target, property_name, value):
            return True
    return False


def _call_on_targets(targets, method_name, value):
    """LightComponent.light_color is an FColor; set_light_color takes the LinearColor directly."""
    for target in targets:
        method = getattr(target, method_name, None)
        if callable(method):
            try:
                method(value)
                return True
            except BaseException:
                continue
    return False


def apply_photometric(actor, arguments, applied, refused):
    """Setzt Intensität, Farbe, Temperatur, Schatten und Streuung über alle bekannten Formen."""
    targets = light_targets(actor)
    settings = (
        ("intensity", ("intensity", "source_intensity", "light_intensity")),
        ("temperature", ("temperature", "color_temperature")),
        ("attenuationRadius", ("attenuation_radius", "radius")),
        ("innerCone", ("inner_cone_angle",)),
        ("outerCone", ("outer_cone_angle",)),
        ("sourceAngle", ("source_angle", "light_source_angle")),
        ("barnDoorAngle", ("barn_door_angle",)),
        ("volumeScatteringIntensity", ("volume_scattering_intensity",)),
        ("indirectLightingIntensity", ("indirect_lighting_intensity",)),
        ("lowerFogHeight", ("fog_height",)),
        ("fogDensity", ("fog_density",)),
    )
    for argument_name, property_names in settings:
        value = ops.number(arguments, argument_name)
        if value is None:
            continue
        placed = False
        for property_name in property_names:
            if _set_on_targets(targets, property_name, float(value)):
                applied.append(f"{argument_name} -> {property_name}")
                placed = True
                break
        if placed and argument_name == "temperature" and _set_on_targets(targets, "use_temperature", True):
            applied.append("use_temperature = True")
        if not placed:
            refused.append(f"{argument_name}: keine passende Eigenschaft ({', '.join(property_names)})")
    color = ops.mapping(arguments, "lightColor")
    if color:
        value = unreal.LinearColor(float(color.get("r", 1)), float(color.get("g", 1)),
                                   float(color.get("b", 1)), float(color.get("a", 1)))
        if not (_call_on_targets(targets, "set_light_color", value) or _set_on_targets(targets, "light_color", value)):
            refused.append("lightColor: light_color nicht setzbar")
        else:
            applied.append("lightColor -> light_color")
    if "castShadows" in arguments:
        if _set_on_targets(targets, "cast_shadows", ops.flag(arguments, "castShadows", True))                 or _set_on_targets(targets, "cast_dynamic_shadow", ops.flag(arguments, "castShadows", True)):
            applied.append("castShadows")
        else:
            refused.append("castShadows: cast_shadows/cast_dynamic_shadow nicht setzbar")
    if "visible" in arguments:
        try:
            actor.set_is_hidden_ed(not ops.flag(arguments, "visible", True))
            applied.append("visible")
        except BaseException:
            refused.append("visible: kein Zugriff auf die Sichtbarkeit")


def read_photometric(actor):
    report = {}
    targets = light_targets(actor)
    for name in ("intensity", "source_intensity", "temperature", "use_temperature", "attenuation_radius",
                 "inner_cone_angle", "outer_cone_angle", "source_angle",
                 "cast_shadows", "cast_dynamic_shadow", "light_color", "volume_scattering_intensity",
                 "indirect_lighting_intensity", "fog_density", "fog_height", "light_requirement"):
        value = None
        for target in targets:
            value = ops.attribute(target, name)
            if value is not None and not callable(value):
                break
        if value is None or callable(value):
            continue
        text = str(value)
        try:
            report[name] = float(value)
        except (TypeError, ValueError):
            report[name] = text
    return report


@ops.operation("light.create", "Setzt eine gerichtete, Punkt-, Spot-, Rechteck-, Himmels-, Nebel- oder Postprozesslichtquelle.")
def op_light_create(arguments, context):
    world = ops.current_world()
    if world is None:
        raise ops.OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")
    kind = ops.text(arguments, "kind", "directional")
    name = ops.text(arguments, "lightName") or ops.text(arguments, "name")
    actor = spawn_light(kind, name, ops.make_vector(ops.vector_from(arguments, "position")),
                        ops.make_rotator(ops.angles_from(arguments, "rotation")))
    applied, refused = [], []
    apply_photometric(actor, arguments, applied, refused)
    ops.mark_world_dirty(world)
    result = ops.actor_report(actor)
    result["kind"] = kind
    result["applied"] = applied
    result["refused"] = refused
    result["values"] = read_photometric(actor)
    result["operationId"] = ops.text(arguments, "operationId")
    return result


@ops.operation("light.set", "Ändert eine vorhandene Lichtquelle in ihren Werten.")
def op_light_set(arguments, context):
    world = ops.current_world()
    if world is None:
        raise ops.OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")
    actor = find_light(world, ops.text(arguments, "lightName") or ops.text(arguments, "name"))
    applied, refused = [], []
    if "position" in arguments:
        actor.set_actor_location(ops.make_vector(ops.vector_from(arguments, "position")), False, False)
        applied.append("position")
    if "rotation" in arguments:
        actor.set_actor_rotation(ops.make_rotator(ops.angles_from(arguments, "rotation")), False)
        applied.append("rotation")
    apply_photometric(actor, arguments, applied, refused)
    ops.mark_world_dirty(world)
    result = ops.actor_report(actor)
    result["applied"] = applied
    result["refused"] = refused
    result["values"] = read_photometric(actor)
    result["operationId"] = ops.text(arguments, "operationId")
    return result


@ops.operation("light.read", "Liest alle Lichtquellen der offenen Welt oder eine benannte.")
def op_light_read(arguments, context):
    world = ops.current_world()
    if world is None:
        raise ops.OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")
    wanted = ops.text(arguments, "lightName")
    classes = tuple(_light_class(kind) for kind in
                    ("directional", "point", "spot", "rect", "sky", "fog", "postProcess"))
    found = []
    for actor in ops.actors_in_world(world):
        try:
            if not isinstance(actor, classes):
                continue
        except BaseException:
            continue
        if wanted and wanted not in (actor.get_name(), actor.get_actor_label(), actor.get_path_name()):
            continue
        entry = ops.actor_report(actor)
        entry["values"] = read_photometric(actor)
        found.append(entry)
    return {
        "world": world.get_path_name(),
        "lights": found[:ops.MAX_ITEMS_REPORTED],
        "count": len(found),
        "operationId": ops.text(arguments, "operationId"),
    }


def sun_position_from_time(hour, latitude_deg=51.0):
    """Grobsonnenstand aus Tageszeit: Höhe und Azimut in Grad, ohne Jahreszeit."""
    altitude = 90.0 - abs(hour * 15.0 - 180.0)
    declination = math.radians(23.44 * math.sin(math.radians((hour - 6.0) * 3.0)))
    latitude = math.radians(latitude_deg)
    altitude_deg = math.degrees(altitude * 0.6 + declination * 0.4)
    azimuth = 90.0 + (hour - 6.0) * 15.0
    return max(-90.0, min(90.0, altitude_deg)), azimuth % 360.0


@ops.operation("sky.create", "Setzt in einem Schritt Sonne, Himmelslicht, Nebel und optional Wolken.")
def op_sky_create(arguments, context):
    world = ops.current_world()
    if world is None:
        raise ops.OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")
    hour = float(ops.number(arguments, "timeOfDay", 13.0))
    altitude, azimuth = sun_position_from_time(hour)
    altitude = float(ops.number(arguments, "sunAltitude", altitude))
    azimuth = float(ops.number(arguments, "sunAzimuth", azimuth))
    sun_intensity = float(ops.number(arguments, "sunIntensity", 4.0))
    cloudiness = float(ops.number(arguments, "cloudiness", 0.2))
    night = altitude <= 0.0
    if night:
        sun_intensity = float(ops.number(arguments, "nightSunIntensity", 0.02))

    rotation = unreal.Rotator(pitch=-altitude, yaw=(azimuth + 90.0) % 360.0, roll=0.0)
    created, notes = [], []
    sun = spawn_light("directional", ops.text(arguments, "sunName", "MissumSun"),
                      unreal.Vector(0, 0, 20000), rotation)
    applied, refused = [], []
    apply_photometric(sun, {"intensity": sun_intensity,
                            "sourceAngle": float(ops.number(arguments, "sunSourceAngle", 0.5343)),
                            "castShadows": True}, applied, refused)
    created.append({"actor": ops.actor_report(sun), "kind": "directional",
                    "applied": applied, "refused": refused})

    sky_light = spawn_light("sky", ops.text(arguments, "skyLightName", "MissumSkyLight"),
                            unreal.Vector(0, 0, 4000), unreal.Rotator(0, 0, 0))
    sky_applied, sky_refused = [], []
    apply_photometric(sky_light, {"intensity": float(ops.number(arguments, "skyIntensity",
                                                                0.35 if night else 1.0))},
                      sky_applied, sky_refused)
    created.append({"actor": ops.actor_report(sky_light), "kind": "sky",
                    "applied": sky_applied, "refused": sky_refused})

    fog = spawn_light("fog", ops.text(arguments, "fogName", "MissumFog"),
                      unreal.Vector(0, 0, 0), unreal.Rotator(0, 0, 0))
    fog_applied, fog_refused = [], []
    apply_photometric(fog, {"fogDensity": float(ops.number(arguments, "fogDensity",
                                                           0.02 if not night else 0.06))},
                      fog_applied, fog_refused)
    created.append({"actor": ops.actor_report(fog), "kind": "fog",
                    "applied": fog_applied, "refused": fog_refused})

    for optional_kind, label in (("atmosphere", "SkyAtmosphere"), ("cloud", "VolumetricCloud")):
        actor_class = getattr(unreal, label, None)
        if actor_class is None:
            notes.append(f"{label} fehlt in dieser Engineversion")
            continue
        handler = ops.actor_subsystem()
        try:
            optional = handler.spawn_actor_from_class(actor_class, unreal.Vector(0, 0, 0), unreal.Rotator(0, 0, 0))
        except BaseException as error:
            notes.append(f"{label} konnte nicht gesetzt werden: {error}")
            continue
        if optional is None:
            notes.append(f"{label} wurde nicht angenommen")
            continue
        created.append({"actor": ops.actor_report(optional), "kind": optional_kind,
                        "applied": [], "refused": []})

    ops.mark_world_dirty(world)
    return {
        "timeOfDay": hour,
        "sunAltitude": altitude,
        "sunAzimuth": azimuth,
        "isNight": night,
        "cloudiness": cloudiness,
        "lights": created,
        "count": len(created),
        "notes": notes,
        "operationId": ops.text(arguments, "operationId"),
    }


@ops.operation("sky.read", "Liest Sonne, Himmelslicht, Nebel und Wolken der offenen Welt.")
def op_sky_read(arguments, context):
    world = ops.current_world()
    if world is None:
        raise ops.OpsError("unreal.world_missing", "Es ist keine Editorwelt geöffnet.")
    labels = {
        "directionalLight": "DirectionalLight",
        "skyLight": "SkyLight",
        "fog": "ExponentialHeightFog",
        "atmosphere": "SkyAtmosphere",
        "cloud": "VolumetricCloud",
    }
    summary = {}
    for key, class_name in labels.items():
        actor_class = getattr(unreal, class_name, None)
        if actor_class is None:
            summary[key] = {"verfügbar": False}
            continue
        found = []
        for actor in ops.actors_in_world(world):
            try:
                if isinstance(actor, actor_class):
                    found.append({"name": actor.get_name(), "label": actor.get_actor_label(),
                                  "rotation": ops.rotation_dict(actor.get_actor_rotation()),
                                  "values": read_photometric(actor)})
            except BaseException:
                continue
        summary[key] = {"verfügbar": True, "count": len(found), "actors": found[:8]}
    return {"world": world.get_path_name(), "sky": summary,
            "operationId": ops.text(arguments, "operationId")}
