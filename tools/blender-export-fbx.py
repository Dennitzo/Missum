# SPDX-License-Identifier: MIT
"""Erzeugt ein unterscheidbares Netz in Blender und legt es für den Unreal-Import ab.

Hilfsmittel für die Abnahme des Importwegs. Blender läuft kopflos; das Netz bekommt eine
eigene Grundfarbe, damit der Materialimport nachweisbar ist.

    blender -b --factory-startup --python tools/blender-export-fbx.py -- --output <pfad.fbx>
"""

import os
import sys


def argument(name, default=None):
    """Liest benannte Argumente hinter dem Trennstrich der Blender-Kommandozeile."""
    argv = sys.argv
    if "--" in argv:
        argv = argv[argv.index("--") + 1:]
    else:
        argv = []
    for index, value in enumerate(argv):
        if value == f"--{name}" and index + 1 < len(argv):
            return argv[index + 1]
    return default


def build_scene():
    import bpy

    for object_name in list(bpy.data.objects):
        bpy.data.objects.remove(object_name, do_unlink=True)

    # Eine achteckige, nach oben schlanke Säule mit Sockel: klar von einer Grundform zu unterscheiden.
    bpy.ops.mesh.primitive_cone_add(vertices=8, radius1=0.85, radius2=0.42, depth=4.2, location=(0, 0, 2.6))
    pillar = bpy.context.active_object
    pillar.name = "MissumPillar"

    bpy.ops.mesh.primitive_cube_add(size=1.0, location=(0, 0, 0.2))
    base = bpy.context.active_object
    base.name = "MissumPillarBase"
    base.scale = (1.35, 1.35, 0.4)

    bpy.ops.object.select_all(action="DESELECT")
    pillar.select_set(True)
    base.select_set(True)
    bpy.context.view_layer.objects.active = pillar
    bpy.ops.object.join()
    joined = bpy.context.active_object
    joined.name = "SM_MissumPillar"

    material = bpy.data.materials.new("M_MissumPillar")
    material.use_nodes = True
    principled = material.node_tree.nodes.get("Principled BSDF")
    if principled is not None:
        for input_name in ("Base Color", "Basisfarbe"):
            if input_name in principled.inputs:
                principled.inputs[input_name].default_value = (0.82, 0.18, 0.12, 1.0)
                break
        for input_name in ("Roughness", "Rauheit"):
            if input_name in principled.inputs:
                principled.inputs[input_name].default_value = 0.35
                break
    joined.data.materials.clear()
    joined.data.materials.append(material)
    return joined


def export(object_value, output_path):
    import bpy

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    object_value.select_set(True)
    bpy.context.view_layer.objects.active = object_value

    if hasattr(bpy.ops.export_scene, "fbx"):
        bpy.ops.export_scene.fbx(
            filepath=output_path,
            use_selection=True,
            apply_unit_scale=True,
            apply_scale_options="FBX_SCALE_ALL",
            bake_space_transform=True,
            object_types={"MESH"},
            path_mode="COPY",
            embed_textures=True,
            add_leaf_bones=False,
            mesh_smooth_type="FACE",
        )
        return "fbx"
    if output_path.lower().endswith(".fbx"):
        output_path = os.path.splitext(output_path)[0] + ".glb"
    bpy.ops.export_scene.gltf(filepath=output_path, export_format="GLB", use_selection=True)
    return "gltf"


def main():
    output_path = argument("output", os.path.join(os.getcwd(), "SM_MissumPillar.fbx"))
    object_value = build_scene()
    used = export(object_value, output_path)
    if used == "fbx" and not os.path.isfile(output_path):
        output_path = os.path.splitext(output_path)[0] + ".glb"
        used = export(object_value, output_path)
    print(f"BLENDER-EXPORT: {used} -> {output_path} ({os.path.getsize(output_path)} Bytes)")


main()
