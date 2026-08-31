using Godot;

// How the world-map painter INKS the map. Purely how the document is drawn and
// picked at — nothing here reaches the bake, so editing it can change what an
// author sees and never what the world becomes.
//
// It is shared rather than per-world: a document points at one of these the way
// it points at world_finish_data.tres. The colour language of the map is a
// property of the TOOL, so every document reading the same one is what stops two
// worlds disagreeing about what a 4m step looks like.
//
// What is NOT here is how many danger levels there are
// (WorldMapData.mobLevelCount): that decodes the painted scalar layer, so it
// says what the world IS, while the swatches below only say how it is shown.
[GlobalClass]
public partial class WorldMapInkData : Resource
{
    // Swatches for the danger ramp, indexed by level. The field between levels
    // is continuous and the map lerps these linearly, so a soft brush edge reads
    // as a gradient rather than as a staircase.
    [Export] public Color[] mobLevelColors =
    {
        new Color(0.35f, 0.55f, 0.35f),
        new Color(0.65f, 0.70f, 0.30f),
        new Color(0.80f, 0.55f, 0.20f),
        new Color(0.75f, 0.30f, 0.20f),
        new Color(0.50f, 0.18f, 0.45f),
    };

    // A hand-placed entity, and the player spawn. Both are single metres on the
    // map, so they are drawn as flat marks rather than washes.
    [Export] public Color entityInk = new Color(1f, 0.55f, 0.15f);
    [Export] public Color spawnInk = new Color(0.3f, 1f, 1f);

    // Marks the entity tool picks out of the rest. A placement whose entry is
    // the one the palette has selected is inked as a MATCH, so choosing "chest"
    // lights every chest already on the map and "where are they" is answered by
    // the palette instead of by hunting; the one placement being edited is inked
    // as the SELECTION over that.
    [Export] public Color entityMatchInk = new Color(1f, 0.9f, 0.35f);
    [Export] public Color entitySelectedInk = new Color(1f, 1f, 1f);

    // How far a mark grows, in metres, when the tool is picking it out — the
    // cursor is over it, it is the selection, or it matches the palette entry. A
    // one-metre dot is smaller than the cursor that has to hit it, so growing is
    // what makes the grab predictable and what carries the palette's answer
    // across a map zoomed out past reading a colour. 0 turns the growth off.
    [Export(PropertyHint.Range, "0,4,1")] public int entityMarkHighlightRadius = 1;

    // Wash over a placed subscene's footprint. The alpha is the SELECTED
    // strength; an unselected stamp gets a fraction of it, so which one a drag
    // is about is never in doubt.
    [Export] public Color placementInk = new Color(1f, 0.85f, 0.35f, 0.7f);

    // A painted climbing route, inked over the step outline in place of its
    // height ink — so a route reads as the white edge you clicked turning
    // magenta, and nothing else about the map changes.
    [Export] public Color climbInk = new Color(1f, 0.2f, 0.9f, 1f);

    // How far a painted water type pulls the map's water colour toward that
    // block's own minimapColor. Not 1: the depth shading underneath is what says
    // how DEEP the water is, and replacing it outright would trade one answer
    // for the other. At 0 a painted type is invisible on the map, which is
    // exactly the state this exists to fix.
    [Export(PropertyHint.Range, "0,1,0.01")] public float waterTypeTintStrength = 0.62f;

    // How far the TOP metre of a band is lerped toward white. The metres between
    // spread evenly up to it, so this is the whole contrast range of a band in
    // one number: 1 would take the highest metre to pure white and lose the hue
    // that says which band it is, while 0 would flatten every metre onto the
    // authored base and lose the step. Half keeps both readable.
    [Export(PropertyHint.Range, "0,1,0.01")] public float elevationBandMaxBrightness = 0.5f;

    // Elevation palette. Height is read as bands of `metersPerBand`: the band
    // picks a colour from this cycle, and the metre within the band lifts it
    // toward white, so a step is always visible — a lift inside a band, a hue
    // change across one — without any contour trickery.
    //
    // These are BASE colours, the darkest metre of their band, so they are
    // authored at part value: a fully saturated base has no headroom left to
    // lift into and its four metres would be indistinguishable. The cycle is the
    // six primaries/secondaries then the same wheel offset by half a step;
    // append more to delay the repeat.
    [Export] public Color[] elevationBandHues =
    {
        new Color(0.4f, 0.4f, 0f), new Color(0f, 0.4f, 0f), new Color(0f, 0.4f, 0.4f),
        new Color(0f, 0f, 0.4f), new Color(0.4f, 0f, 0.4f), new Color(0.4f, 0f, 0f),
        new Color(0.2f, 0.4f, 0f), new Color(0f, 0.4f, 0.2f), new Color(0f, 0.2f, 0.4f),
        new Color(0.2f, 0f, 0.4f), new Color(0.4f, 0f, 0.2f), new Color(0.4f, 0.2f, 0f),
    };

    [Export(PropertyHint.Range, "1,16,1")] public int metersPerBand = 4;

    // Submerged ground is drawn as flat water, NOT as a tinted seabed: depth and
    // height would otherwise speak the same colour language and the eye cannot
    // separate "low" from "underwater". Two shades only — down to
    // waterDeepAtVoxels under the surface, lerped between the two stops.
    // Step outlines still draw over both, so the bed's shape is readable without
    // its height being legible.
    [Export] public Color shallowWaterColor = new Color(0.35f, 0.60f, 0.90f);
    [Export] public Color deepWaterColor = new Color(0.10f, 0.20f, 0.60f);
    // Depth at which water reaches deepWaterColor; shallower lerps toward
    // shallowWaterColor, so a shoreline is pale and anything past this is dark.
    [Export(PropertyHint.Range, "1,32,1")] public int waterDeepAtVoxels = 4;

    // Edge of water that pours over a bare drop — a waterfall. Bright, because
    // it is a thing to notice rather than to read past.
    // Waterfall edges are drawn WIDER than a contour line and at full alpha.
    // They are a warning, not a height cue: a spill is the one thing about
    // painted water the depth shading cannot show, and at the ordinary
    // edgeWidthFraction (a single pixel at 3 px/m) a bright teal reads as a
    // faint fringe on the shoreline it is trying to flag. As a fraction of a
    // metre cell, like edgeWidthFraction; 1 floods the whole cell.
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float waterfallEdgeWidthFraction = 0.67f;

    [Export] public Color waterfallInk = new Color(0.25f, 1f, 0.9f, 1f);

    // Solid rock at a cutaway view's clip level with nothing hollow anywhere
    // beneath it — the one case with no floor to draw. Flat and dark by design:
    // it is the ABSENCE of a readable surface, and anything with hue in it would
    // enter the elevation palette's colour language and start looking like a
    // height. It is also the ink a BURIED floor is dithered against, which is
    // what makes the dither mean "seen through rock" rather than merely "dark".
    [Export] public Color cutawayRockColor = new Color(0.03f, 0.03f, 0.05f);

    // Ink for the outline drawn on a voxel edge where the height changes, by how
    // big that change is: under 2m, exactly 2m, over 2m. ALPHA IS PART OF THE
    // COLOUR — a step reads louder by being both stronger and more opaque, and
    // splitting the two across a colour and a float only invites them to drift.
    //
    // edgeInkSub2m is drawn ONLY on views whose colour does not already encode
    // elevation (see IWorldMapView.ColorShowsElevation): on the elevation map it
    // would run a line along every metre of every slope, saying nothing the
    // bands have not already said.
    [Export] public Color edgeInkSub2m = new Color(0f, 0f, 0f, 0.0902f);
    [Export] public Color edgeInk2m = new Color(0f, 0f, 0f, 0.5961f);
    [Export] public Color edgeInkOver2m = new Color(1f, 1f, 1f, 0.8314f);

    // The swatch for a danger level, clamped so a colour list shorter than
    // WorldMapData.mobLevelCount still draws instead of throwing.
    public Color MobLevelColor(int level)
    {
        if (mobLevelColors == null || mobLevelColors.Length == 0)
        {
            return Colors.White;
        }
        return mobLevelColors[Mathf.Clamp(level, 0, mobLevelColors.Length - 1)];
    }
}
