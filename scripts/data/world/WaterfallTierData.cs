using Godot;

// One size class of waterfall — the sound it makes, the spray it throws, and how
// heavy its curtain reads. Worldgen measures a cascade once and picks the tier;
// nothing downstream re-derives a size, so a tier IS what "a big waterfall"
// means in this world.
//
// Tiers are authored smallest-first on WaterfallData; a fall takes the LAST tier
// whose minFallHeight it clears, so the first entry's threshold is what a
// cascade has to beat to be drawn at all.
[GlobalClass]
public partial class WaterfallTierData : Resource
{
    [Export] public string displayName = "";

    // Fall height, in voxels (1 voxel = 1 m), at or above which a cascade takes
    // this tier.
    [Export(PropertyHint.Range, "0,64,1")] public float minFallHeight = 0f;

    // Looping ambience played at the BASE of the fall — the plunge pool is where
    // a waterfall is loud, not the lip. One player per site, so this wants a
    // seamless loop (loop_mode in the .wav's .import), not a one-shot.
    [Export] public AudioStream sound;

    [Export(PropertyHint.Range, "-40,12,0.5")] public float volumeDb = 0f;

    // Distance at which the fall drops out of earshot. Also the radius at which
    // the player is far enough that the stream is paused entirely.
    [Export(PropertyHint.Range, "4,120,1")] public float maxDistance = 40f;

    // Metres over which the source attenuates from full volume — Godot's
    // AudioStreamPlayer3D.UnitSize. Bigger falls should carry further before
    // they start falling off, not just be louder up close.
    [Export(PropertyHint.Range, "1,40,0.5")] public float unitSize = 8f;

    // Spray thrown off the LIP, where the water leaves the ground and starts to
    // break up. Instanced along the top edge of the sheet.
    [Export] public PackedScene lipFx;

    // Mist and splash at the LANDING. Instanced along the bottom edge. This is
    // what hides the seam where the ribbon meets the pool, so it should read as
    // a plume rather than a thin puff.
    [Export] public PackedScene baseFx;

    // How much water the curtain is carrying, in metres of optical depth. The
    // sheet has no scene geometry behind it to measure against (unlike a pool,
    // which reads its depth off the depth buffer), so this authored thickness IS
    // what drives the Beer-Lambert extinction in the shader — raise it and the
    // fall goes from a translucent veil to a solid white-green wall.
    [Export(PropertyHint.Range, "0.05,4,0.05")] public float sheetThickness = 0.5f;

    // Whitewater coverage over the sheet, 0..1. Turbulent big falls are mostly
    // foam; a trickle is mostly clear water.
    [Export(PropertyHint.Range, "0,1,0.01")] public float foam = 0.5f;
}
