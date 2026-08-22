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

    // How fast the water leaves the lip, in metres per second. The sheet is
    // thrown horizontally at this speed and gravity bends it down, which is what
    // makes the shoulder round at the top and the foot stand off the wall.
    //
    // A SPEED, not a distance. The reach used to be authored flat in metres and
    // applied whatever the drop, which inverts the physics: a fall throws its
    // water further the longer it is in the air, so a flat reach made a 1 m weir
    // arc out exactly as far as a 12 m cascade and read as a chute rather than a
    // lip. About 0.6 keeps a tall fall roughly a metre off its wall.
    [Export(PropertyHint.Range, "0,3,0.01")] public float pourSpeed = 0.64f;

    // How far BELOW the lower pool's surface the sheet is carried, in metres, so
    // it visibly enters the water instead of stopping on top of it.
    [Export(PropertyHint.Range, "0,3,0.05")] public float landingDepth = 1f;

    // How far the outer edge of the sheet is tucked in where there is no
    // neighbouring strip beside it, in metres (max 0.45 of the metre-wide step).
    // Zero by default: the sheet should fill the whole metre of lip it pours
    // over, so it lines up with the water surface feeding it.
    [Export(PropertyHint.Range, "0,0.45,0.01")] public float edgeInset = 0f;

    // How hard the sweep's samples are bunched toward the lip. The jet leaves
    // the edge HORIZONTALLY and only turns down as it accelerates, so evenly
    // spaced samples put the first polygon well past the turn and the sheet
    // reads as leaving the pool at an angle. Raising this packs several
    // polygons into the first few centimetres, where the surface is still flat
    // — which is what makes the top of the fall continuous with the pool.
    [Export(PropertyHint.Range, "1,6,0.1")] public float shoulderBias = 3f;

    // Spacing of spray emitters along the lip and the landing line. A five-wide
    // sheet wants more than one plume, and a one-wide sheet wants exactly one;
    // spacing rather than a count is what makes both fall out of the same rule.
    [Export(PropertyHint.Range, "1,16,0.5")] public float metersPerEmitter = 3f;

    // Hard cap on emitters per edge, so a freak 40-column sheet can't spawn
    // forty particle systems.
    [Export(PropertyHint.Range, "1,8,1")] public int maxEmittersPerEdge = 4;

    // The shortest fall this world draws at all — the first tier's threshold.
    // Worldgen files no entity below it, so a one-voxel step off a pool edge
    // stays the rapid it is instead of becoming thousands of invisible entities.
    public float SmallestDrawnFall()
    {
        float smallest = float.MaxValue;
        for (int i = 0; i < tiers.Length; i++)
        {
            if (tiers[i] != null && tiers[i].minFallHeight < smallest)
            {
                smallest = tiers[i].minFallHeight;
            }
        }
        return smallest == float.MaxValue ? 0f : smallest;
    }

    // Horizontal throw at the foot of a sheet swept over `drop` metres.
    //
    // x = v·t and drop = ½g·t², so x = v·√(2·drop/g) — the reach grows as the
    // SQUARE ROOT of the drop. Real gravity, so pourSpeed is a real speed rather
    // than a number tuned against an arbitrary scale.
    public float ReachFor(float drop)
    {
        return pourSpeed * Mathf.Sqrt(2f * Mathf.Max(drop, 0f) / GRAVITY);
    }

    private const float GRAVITY = 9.8f;

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
