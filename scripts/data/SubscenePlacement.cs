using Godot;

// Whole-scene rotation about +Y, in 90° steps. The member's integer value IS
// the quarter-turn count, and the sense matches an entity's RotationY: a wall
// authored facing +Z faces +X at Deg90.
//
// APPEND new members only. Godot renumbers on insert, and a running editor with
// a stale assembly silently drops .tres lines that end up equal to the default.
public enum ESubsceneRotation
{
    Deg0,
    Deg90,
    Deg180,
    Deg270,
}

// Declarative placement of a subscene in WorldGenData. Path points at a
// `.hikescene` file; AnchorXZ is the world XZ where the subscene's anchor
// should land. Y is computed by WorldGen.FootprintPlateauY — the dominant
// plateau level across the footprint — so the cottage sits flush on the
// terrace WorldGen built underneath it, and a cave that breached the ground
// nearby can't drag it down. The scene's bottom layer lands ON that top
// terrain voxel, replacing it: an authored scene brings its own floor.
//
// Placement is intentionally dumb: no slope check, no overlap test. Use it for
// hand-curated landmarks where the authored XZ is known to land on reasonable
// terrain. Procedural placement is a separate, deferred problem.
[GlobalClass]
public partial class SubscenePlacement : Resource
{
    [Export(PropertyHint.File, "*.hikescene")] public string path;
    [Export] public Vector2I anchorXZ;

    // The scene spins about its ANCHOR: the anchored point stays at anchorXZ
    // and the footprint swings around it, so a scene anchored at a corner
    // covers different ground once turned. Author the anchor at the centre in
    // the world editor if you want a rotation to stay put.
    [Export] public ESubsceneRotation rotation;
}
