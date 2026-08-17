using Godot;

// Everything a waterfall is made of, shared by every cascade in the world. The
// per-site part — where the sheet hangs and how tall it is — comes from worldgen
// and is serialized with the entity; this is the authored half.
//
// One global style rather than a per-zone one: the sheet's COLOUR already
// follows the zone, because the shader reads the same water_* globals
// SkyController drives from ZoneData.WaterColor, so a swamp cascade is already
// swamp-coloured without anything here changing.
[GlobalClass]
public partial class WaterfallData : Resource
{
    // The ribbon's surface — a ShaderMaterial on shaders/waterfall.gdshader.
    // Null leaves the fall unspawned rather than untextured, the same way a roof
    // with no style stays down.
    [Export] public Material sheetMaterial;

    // Size classes, authored SMALLEST FIRST. A cascade takes the last tier whose
    // minFallHeight it clears, so a fall shorter than tiers[0] draws nothing at
    // all — that is the "too small to be a waterfall" gate.
    [Export] public WaterfallTierData[] tiers = System.Array.Empty<WaterfallTierData>();

    // How far out from the lip the jet travels before it lands, in metres. The
    // sheet leaves the edge moving outward and gravity bends it down, so this is
    // both how far the fall stands off the wall at its foot and — because the
    // reach is spent as the square root of the drop — how round the shoulder at
    // the top is. Keep it around a metre: much more and the fall arcs away from
    // the cliff it belongs to.
    [Export(PropertyHint.Range, "0,4,0.05")] public float pourReach = 1f;

    // How far BELOW the lower pool's surface the sheet is carried, in metres, so
    // it visibly enters the water instead of stopping on top of it.
    [Export(PropertyHint.Range, "0,3,0.05")] public float landingDepth = 1f;

    // How far the outer edge of the sheet is tucked in where there is no
    // neighbouring strip beside it, in metres (max 0.45 of the metre-wide step).
    // Without it a curtain ends on a hard square corner.
    [Export(PropertyHint.Range, "0,0.45,0.01")] public float edgeInset = 0.2f;

    // Spacing of spray emitters along the lip and the landing line. A five-wide
    // sheet wants more than one plume, and a one-wide sheet wants exactly one;
    // spacing rather than a count is what makes both fall out of the same rule.
    [Export(PropertyHint.Range, "1,16,0.5")] public float metersPerEmitter = 3f;

    // Hard cap on emitters per edge, so a freak 40-column sheet can't spawn
    // forty particle systems.
    [Export(PropertyHint.Range, "1,8,1")] public int maxEmittersPerEdge = 4;

    // Which tier a fall of this height belongs to, or null if it is too short to
    // draw. Heights are in voxels; the array is assumed authored ascending, and
    // an out-of-order entry simply loses to the one before it.
    public WaterfallTierData TierFor(float fallHeight)
    {
        WaterfallTierData chosen = null;
        for (int i = 0; i < tiers.Length; i++)
        {
            WaterfallTierData tier = tiers[i];
            if (tier == null || fallHeight < tier.minFallHeight) { continue; }
            chosen = tier;
        }
        return chosen;
    }
}
