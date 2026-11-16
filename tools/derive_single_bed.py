# Derives a single bed from the Synty bunk (SM_Prop_Bed_01): the lower bunk
# with its four corner posts cut down to bedpost height. The bunk is one mesh
# on one atlas material, but its deck, mattress and pillow are separate pieces
# of geometry and the upper bunk sits wholly above the cut, so one horizontal
# bisect (posts capped) and deleting everything above it leaves the bed.
#
#   blender -b --python tools/derive_single_bed.py -- in.fbx out.fbx [post_height_m]
#
# post_height_m is measured against the bunk's own 2.077 m height (the cut is
# placed as a fraction of the bounding box, so the FBX unit scale does not
# matter). The lower mattress tops out at 0.735 m, so keep it above that.
#
# Used for assets/models/furniture/SM_Prop_Bed_Single_01.fbx.
import bpy
import bmesh
import sys
from mathutils import Vector

BUNK_HEIGHT_M = 2.077374
DEFAULT_POST_HEIGHT_M = 0.95


def main():
    args = sys.argv[sys.argv.index("--") + 1:]
    src, dst = args[0], args[1]
    post_height = float(args[2]) if len(args) > 2 else DEFAULT_POST_HEIGHT_M

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=src)

    meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
    if len(meshes) != 1:
        raise RuntimeError(f"expected one mesh in {src}, found {len(meshes)}")
    obj = meshes[0]

    # Bake the import's axis/unit transform into the vertices so world Z is up
    # and the bisect plane is a plain horizontal one.
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    bm = bmesh.new()
    bm.from_mesh(obj.data)
    zs = [v.co.z for v in bm.verts]
    z_min, z_max = min(zs), max(zs)
    cut_z = z_min + (z_max - z_min) * (post_height / BUNK_HEIGHT_M)

    geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
    result = bmesh.ops.bisect_plane(bm, geom=geom, plane_co=Vector((0, 0, cut_z)),
                                    plane_no=Vector((0, 0, 1)), clear_outer=True)
    cut_edges = [e for e in result["geom_cut"] if isinstance(e, bmesh.types.BMEdge)]
    bmesh.ops.holes_fill(bm, edges=cut_edges, sides=0)

    # The caps are tiny; give their UVs the post-top neighbourhood's UV so they
    # sample the wood rather than wherever (0,0) lands on the atlas.
    uv = bm.loops.layers.uv.active
    if uv is not None:
        for f in bm.faces:
            if all(abs(v.co.z - cut_z) < 1e-5 for v in f.verts):
                for loop in f.loops:
                    neighbours = [l for l in loop.vert.link_loops if l.face is not f]
                    if neighbours:
                        loop[uv].uv = neighbours[0][uv].uv.copy()

    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()

    bpy.ops.export_scene.fbx(filepath=dst, use_selection=True, object_types={'MESH'},
                             apply_unit_scale=True, bake_space_transform=False,
                             add_leaf_bones=False, bake_anim=False)


main()
