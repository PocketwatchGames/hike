# Builds assets/models/signpost/signpost_single.fbx from the RPGPP Abandoned Village
# notice board: one of its legs, plus its frame and planks squared up and shrunk into
# a single small board. The library has no post-with-one-rectangular-sign of its own.
#
#   blender -b --python tools/build_signpost_single.py -- <rpgpp_av_notice_board_01.fbx> <out.fbx>
#
# Source: AssetDump/Assets/RPGPP_AV/Models/Props/rpgpp_av_notice_board_01.fbx.
# Parts are picked by loose-island vertex count, so a different source revision fails loudly.
import bpy
import bmesh
import mathutils
import numpy as np
import sys

args = sys.argv[sys.argv.index("--") + 1:]
SRC, OUT = args[0], args[1]

BOARD_SCALE = (0.42, 0.6, 0.42)  # width, depth, height, relative to the notice board's panel
BOARD_CENTER_Z = 1.5             # metres above the post's base
PANEL_INSET = 0.03               # backing panel's margin inside the frame
FRAME_VERTS, PLANKS_VERTS, POST_VERTS = 16, 123, 18

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=SRC)
src = [o for o in bpy.data.objects if o.type == 'MESH'][0]
for o in bpy.data.objects:
    o.select_set(o == src)
bpy.context.view_layer.objects.active = src
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.mesh.separate(type='LOOSE')
bpy.ops.object.mode_set(mode='OBJECT')
parts = list(bpy.context.selected_objects)


def part(n):
    found = [p for p in parts if len(p.data.vertices) == n]
    if len(found) != 1:
        raise RuntimeError(f"expected one {n}-vertex island in {SRC}, found {len(found)}")
    return found[0]


def bounds(o):
    v = [o.matrix_world @ x.co for x in o.data.vertices]
    return (mathutils.Vector([min(a[i] for a in v) for i in range(3)]),
            mathutils.Vector([max(a[i] for a in v) for i in range(3)]))


frame, planks, post = part(FRAME_VERTS), part(PLANKS_VERTS), part(POST_VERTS)
for p in parts:
    if p not in (frame, planks, post):
        bpy.data.objects.remove(p)

# Square the board up: the source panel hangs crooked and leans back. The frame's
# principal axes give its width direction (largest) and face normal (smallest).
fv = np.array([list(v.co) for v in frame.data.vertices])
centre = fv.mean(0)
_, axes = np.linalg.eigh(np.cov((fv - centre).T))
ax_x, ax_n = axes[:, 2], axes[:, 0]
if ax_x[0] < 0:
    ax_x = -ax_x
if ax_n[1] < 0:
    ax_n = -ax_n
to_board = np.array([ax_x, ax_n, np.cross(ax_n, ax_x)])
for p in (frame, planks):
    for v in p.data.vertices:
        d = to_board @ (np.array(v.co) - centre)
        v.co = mathutils.Vector((d[0] * BOARD_SCALE[0], d[1] * BOARD_SCALE[1], d[2] * BOARD_SCALE[2]))

# Post centred on the origin, base at z = 0.
plo, phi = bounds(post)
base = mathutils.Vector(((plo.x + phi.x) / 2, (plo.y + phi.y) / 2, plo.z))
for v in post.data.vertices:
    v.co -= base

# Board hung on the post's -Y face.
plo, _ = bounds(post)
_, bhi = bounds(frame)
shift = mathutils.Vector((0, plo.y - bhi.y - 0.01, BOARD_CENTER_Z))
for p in (frame, planks):
    for v in p.data.vertices:
        v.co += shift

# The source planks have a broken gap; a solid panel behind them reads it as wood.
# One flat UV sampled off the planks' largest face (the atlas is a palette).
blo, bhi = bounds(frame)
uv_data = planks.data.uv_layers.active.data
largest = max(planks.data.polygons, key=lambda f: f.area)
uv = sum((uv_data[i].uv for i in largest.loop_indices), mathutils.Vector((0, 0))) / len(largest.loop_indices)
mesh = bpy.data.meshes.new("panel")
bm = bmesh.new()
x0, x1 = blo.x + PANEL_INSET, bhi.x - PANEL_INSET
z0, z1 = blo.z + PANEL_INSET, bhi.z - PANEL_INSET
y = bhi.y - 0.02
corners = ((x0, z0), (x1, z0), (x1, z1), (x0, z1))
front = bm.faces.new([bm.verts.new((cx, y - 0.005, cz)) for cx, cz in corners])
back = bm.faces.new(list(reversed([bm.verts.new((cx, y + 0.005, cz)) for cx, cz in corners])))
uv_layer = bm.loops.layers.uv.new("UVMap")
for f in (front, back):
    for loop in f.loops:
        loop[uv_layer].uv = uv
bm.to_mesh(mesh)
panel = bpy.data.objects.new("panel", mesh)
bpy.context.scene.collection.objects.link(panel)
panel.data.materials.append(planks.data.materials[0])

for p in (frame, planks, post, panel):
    p.select_set(True)
bpy.context.view_layer.objects.active = post
bpy.ops.object.join()
out = bpy.context.view_layer.objects.active
out.name = out.data.name = "signpost_single"
print("signpost_single bounds", bounds(out))
bpy.ops.export_scene.fbx(filepath=OUT, use_selection=True, object_types={'MESH'},
                         mesh_smooth_type='FACE', add_leaf_bones=False, bake_anim=False)
