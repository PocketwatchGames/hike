using Godot;

// The film floating on a water block — pond scum, algae, duckweed, lilypads.
//
// A property of the WATER, not of the region: one green-scum film is authored
// once and works in every zone, because the film's colour comes from its own
// texture and tint while the water beneath keeps whatever hue the zone gave it.
// That split is the whole reason this is not on ZoneData.
//
// Referenced from BlockData.waterFilm; null means bare water. The surface names
// an atlas layer exactly as a block's faces do, so a film costs one baked row
// and no new texture binding.
[GlobalClass]
public partial class WaterFilmData : Resource
{
    [Export] public StringName filmName;

    // The atlas layer sampled for the film's colour. Colour-only art is fine —
    // the silhouette comes from `breakup` below, not from a height map.
    [Export] public BlockSurfaceData surface;

    // Multiplied onto the sampled colour, which is what lets one scum texture
    // serve green and red without a second baked row. White = the art as-is.
    [Export] public Color tint = new Color(1f, 1f, 1f);

    // World metres per tile repeat.
    [Export(PropertyHint.Range, "0.25,32,0.25")] public float scale = 3f;

    // How much the film travels with the water current. 0 = rooted (lilypads),
    // 1 = carried at the full current speed (duckweed).
    [Export(PropertyHint.Range, "0,1,0.01")] public float drift = 1f;

    // How much of the water beneath it the film hides. 1 = an opaque mat.
    [Export(PropertyHint.Range, "0,1,0.01")] public float opacity = 1f;

    // How much of the film is CUT AWAY — 0 is an unbroken mat, 0.4 leaves 60%.
    // It is a threshold, never a scale on the result: a fragment is film or it is
    // water, and scaling instead gives a translucent wash that reads as a tint
    // over the whole pond.
    //
    // Linear, but only because the height map it thresholds is baked rank-
    // normalized. Against the procedural fallback (a layer with no height map)
    // it is approximate, the way any threshold on a bell-shaped noise is.
    [Export(PropertyHint.Range, "0,1,0.01")] public float breakup = 0.45f;

    // Metres per repeat of the PROCEDURAL fallback field. Unused where the tile
    // authors a height map, which is the better silhouette.
    [Export(PropertyHint.Range, "0.25,32,0.25")] public float breakupScale = 1.5f;

    // Width of the ramp at the film's edge. Small keeps weed looking like weed;
    // widening it fades the boundary out, which reads as thinning rather than as
    // a torn edge.
    [Export(PropertyHint.Range, "0.002,0.3,0.002")] public float edgeSoftness = 0.05f;

    // How much the tile's own HEIGHT channel is the silhouette, against the
    // procedural fallback. 1 wherever the layer bakes a height map — it is a far
    // better edge, because the fallback's boundary follows a pixel-snapped grid.
    // 0 is forced for art with no height map, since that row bakes blank.
    [Export(PropertyHint.Range, "0,1,0.01")] public float shape = 1f;
}
