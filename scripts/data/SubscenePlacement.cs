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

    // Voxels up (or down) from the height the footprint resolves to. A NUDGE,
    // not an absolute Y: the seat is recomputed from the ground under the
    // footprint, so a scene follows terrain that moves under it, and this says
    // "and a metre lower than that" — sunk into a hillside, raised on a plinth.
    // An absolute Y would pin the building while the hill walked out from under
    // it.
    [Export] public int yOffset;

    // What this stamp puts in the scene's marker pools (see SubsceneVariant).
    // Empty means every marker in the scene stays empty here — which is what
    // makes one `.hikescene` reusable across placements that want different
    // occupants.
    [Export] public SubsceneVariant[] variants = System.Array.Empty<SubsceneVariant>();

    // Namespace this stamp's path hints register under: a hint tagged "door" in
    // a placement named "house01" becomes the POI "house01.door", which a
    // RoadConnection can name like any other place. Naming the PLACEMENT alone
    // in a road works too — the route then ends at whichever of its hints lies
    // nearest the road's other end.
    //
    // Empty falls back to the `.hikescene`'s file base name. That is unique
    // only while the scene is stamped once, so name every placement explicitly
    // as soon as you reuse a scene (worldgen warns on a collision and drops the
    // duplicate's hints).
    [Export] public string placementName = "";

    // Auto-link this stamp's path hints that no authored road already reaches:
    // worldgen spurs a path from each one to the nearest point of the road
    // network it has laid so far (see WorldGen.ConnectPathHints), with the tread
    // chosen per hint tag from WorldGenData.pathHintProfiles. Off = the hints
    // are addressable POIs and nothing more.
    [Export] public bool connectPathHints;

    // Open ground the scene INVITES worldgen content onto — a plaza, a
    // courtyard — as opposed to a building, where a spawn inside lands in a
    // wall. Lets the one-off fixture passes (a zone's villagers, its well, a
    // POI signpost) stand on the footprint, and spares them the stamp's
    // entity eviction. Procedural scatter and roads stay off it either way:
    // this opens the ground to authored placements only.
    [Export] public bool allowFixtures;
}
