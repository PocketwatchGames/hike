# Derives a variant animation FBX from an existing single-clip animation FBX,
# for the cases the Godot importer cannot express: a time-REVERSED clip, a
# single-pose HOLD, and stripping ROOT MOTION. Everything else (loop flag,
# playback speed, events) is authored on the PlayerAnimManifest instead - use
# this only for these three.
#
#   blender -b --python tools/derive_anim_clips.py -- reverse         in.fbx out.fbx
#   blender -b --python tools/derive_anim_clips.py -- hold            in.fbx out.fbx
#   blender -b --python tools/derive_anim_clips.py -- inplace         in.fbx out.fbx
#   blender -b --python tools/derive_anim_clips.py -- reverse,inplace in.fbx out.fbx
#
# Modes compose, left to right, comma separated.
#
# Exports the armature only (the manifest reads nothing but the clip), so the
# result is a fraction of the source's size and binds to the same Synty
# skeleton. A HOLD export must also have `animation/remove_immutable_tracks`
# turned OFF in its .fbx.import: every track of a static pose is constant, so
# the importer otherwise prunes nearly all of them and the clip only re-poses a
# handful of bones.
#
# Used for the three climb clips, all derived from anims/_source/climb.fbx.
import bpy
import sys


def fcurves_of(action):
    out = []
    for layer in action.layers:
        for strip in layer.strips:
            for bag in strip.channelbags:
                out.extend(bag.fcurves)
    return out


def reverse(arm, action, first, last):
    # Mirror every key time about the clip; update() re-sorts them.
    span = first + last
    for fc in fcurves_of(action):
        for k in fc.keyframe_points:
            k.co.x = span - k.co.x
            k.handle_left.x = span - k.handle_left.x
            k.handle_right.x = span - k.handle_right.x
            k.handle_left, k.handle_right = k.handle_right.copy(), k.handle_left.copy()
        fc.update()
    return last


def hold(arm, action, first, last):
    # Keep the first pose only, keyed twice so the clip still has a length.
    for fc in fcurves_of(action):
        points = fc.keyframe_points
        value = points[0].co.y
        for i in range(len(points) - 1, -1, -1):
            points.remove(points[i], fast=True)
        points.insert(first, value)
        points.insert(first + 1, value)
        for k in points:
            k.interpolation = 'LINEAR'
        fc.update()
    return first + 1


def inplace(arm, action, first, last):
    # Drop the armature OBJECT's translation - the root motion. Bone channels
    # (data_path 'pose.bones[...]') are left alone, so the body still moves; it
    # just stops travelling away from wherever the game put the character.
    for layer in action.layers:
        for strip in layer.strips:
            for bag in strip.channelbags:
                for fc in [f for f in bag.fcurves if f.data_path == 'location']:
                    bag.fcurves.remove(fc)
    # Removing the curves leaves the object wherever it was last evaluated, and
    # that pose offset exports as the node's transform - which the importer bakes
    # into a constant track on any clip not pruning immutable tracks (a HOLD).
    # The player rigs author rootSkeleton at the origin, so match them.
    arm.location = (0.0, 0.0, 0.0)
    return last


MODES = {'reverse': reverse, 'hold': hold, 'inplace': inplace}


def main():
    modes, src, dst = sys.argv[-3:]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=src, use_anim=True)
    arm = [o for o in bpy.data.objects if o.type == 'ARMATURE'][0]
    action = arm.animation_data.action
    first, last = action.frame_range

    for mode in modes.split(','):
        if mode not in MODES:
            raise SystemExit(f"unknown mode '{mode}' (expected one of {sorted(MODES)})")
        last = MODES[mode](arm, action, first, last)

    scene = bpy.context.scene
    scene.frame_start, scene.frame_end = int(first), int(last)
    bpy.ops.export_scene.fbx(
        filepath=dst,
        use_selection=False,
        object_types={'ARMATURE'},
        add_leaf_bones=False,
        bake_anim=True,
        bake_anim_use_all_bones=True,
        bake_anim_use_nla_strips=False,
        bake_anim_use_all_actions=False,
        bake_anim_force_startend_keying=True,
        bake_anim_step=1.0,
        bake_anim_simplify_factor=0.0,
    )
    print(f"derive_anim_clips: {modes} {src} -> {dst} frames {first}..{last}")


main()
