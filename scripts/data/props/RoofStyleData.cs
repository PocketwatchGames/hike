using Godot;

// Which horizontal axis a roof's seam (the ridge two slopes meet along) runs
// down. The mesh is a cross-section extruded along this axis, so the seam is
// what decides which pair of footprint edges become eaves and which become
// gable ends.
//
// APPEND new members only. Godot renumbers on insert, and a running editor with
// a stale assembly silently drops .tres lines that end up equal to the default.
public enum ERoofSeamAxis
{
    AlongX,
    AlongZ,
}

// One roof material an author can paint with — the surface, not the shape.
// Shape (footprint, seam axis, pitch) is per-instance and comes from the drag,
// so a style is reusable across every roof in a world.
//
// The mesh generates its own UVs in world meters, which is why this carries a
// tiling rate rather than the shader: model_lit samples raw UV with no scale
// uniform, so the tiling has to be baked in at build time.
[GlobalClass]
public partial class RoofStyleData : Resource
{
    [Export] public string displayName = "";

    // A model_lit / roof_lit ShaderMaterial. Applied as a surface override on
    // the generated mesh, so the roof clips, shades and recolors like any prop.
    [Export] public Material material;

    // Optional per-style shadow proxy, overriding SimData.roofShadowCasterMaterial.
    // Only needed by a style that punches holes: the visible material's discard
    // removes those fragments from the shadow pass too, so light streams through
    // a hole ONLY if the proxy carries the same mask. Null = the shared solid one.
    [Export] public Material shadowCasterMaterial;

    // Optional palette-button art. Null falls back to the name label.
    [Export] public Texture2D icon;

    // Texture repeats per meter of roof surface. Authored per style because a
    // plank is a different real-world size than a slate tile.
    [Export(PropertyHint.Range, "0.05,4,0.01")] public float textureTilesPerMeter = 0.35f;

    // How far the roof projects past the footprint along the EAVES — the two
    // edges parallel to the ridge. Overhang is what stops a roof reading as a
    // lid sitting exactly on the walls.
    [Export(PropertyHint.Range, "0,3,0.05")] public float eaveOverhang = 0.5f;

    // How far the sloped planes OVERSAIL the gable end. The vertical end face
    // stays flush with the footprint — on the wall it rests on — and only the
    // slab continues past it, so the roof visibly caps the wall instead of
    // stopping dead on it. The classic rake / verge overhang.
    [Export(PropertyHint.Range, "0,3,0.05")] public float rakeOverhang = 0.5f;

    // Chamfer taken off the cross-section's corners: the eave edges, the ridge
    // and the soffit corners. Keeps a generated roof from presenting perfectly
    // razor edges, which read as untextured seams at grazing angles.
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float edgeBevel = 0.1f;

    // Vertical depth of the roof slab. Measured straight down rather than
    // perpendicular to the slope, so the eave presents a clean horizontal cut
    // and the underside stays parallel to the top.
    [Export(PropertyHint.Range, "0.05,2,0.05")] public float thickness = 0.4f;

    // Shingle courses per meter of horizontal run. Each course ends in a small
    // vertical lip, so a large roof reads as banded rows that catch the sun
    // rather than one flat tiled plane. 0 = a smooth, unbroken slope.
    [Export(PropertyHint.Range, "0,4,0.05")] public float coursesPerMeter = 2f;

    // Height of the lip at each course boundary — the shingle's exposed butt
    // edge. Clamped below the course's own rise, so a coarse course count can't
    // invert the profile into a staircase.
    [Export(PropertyHint.Range, "0,0.5,0.005")] public float courseLipHeight = 0.15f;

    // Fraction of a course's run the lip leans over instead of standing dead
    // vertical. Purely a look knob — a low value gives a crisp shingle butt, a
    // high one a soft overlap — but it must stay above zero: a perfectly
    // vertical lip makes its end-cap span zero-width, and the degenerate sliver
    // leaves neighbouring spans meeting the sides at a crack.
    [Export(PropertyHint.Range, "0.02,0.6,0.01")] public float courseLipRun = 0.15f;

    // How sharp a cross-section corner has to turn before edgeBevel cuts it.
    // The default deliberately sits between a course lip's turn (shallow, left
    // sharp) and an eave or ridge corner (sharp, chamfered) — lower it far and
    // the bevel starts eating the shingle courses.
    [Export(PropertyHint.Range, "5,90,1")] public float bevelMinTurnDegrees = 45f;

    // Baked tone at the eave relative to the ridge, written into vertex COLOR
    // and read by model_lit's vertex_ao_strength path. Under 1 the roof darkens
    // toward its edges, which stops a big surface reading as uniformly flat.
    // The material must set vertex_ao_strength for this to do anything.
    [Export(PropertyHint.Range, "0,1,0.01")] public float eaveShade = 0.72f;

    // Whether the roof stops sunlight dead at its base, exactly as a solid
    // voxel ceiling does. On by default, because a roof IS solid.
    //
    // This matters beyond surface shading: a roof's shadow on the floor comes
    // from the shadow atlas, but the VOLUMETRICS read sun visibility from the
    // voxel light map, which only voxels and non-voxel cover write to. Without
    // it, sun shafts pour straight through a solid roof and the air beneath
    // glows — glaring, because a roof is a large overhead occluder.
    [Export] public bool blocksSun = true;

    // Used only when blocksSun is false: partial cover (a derelict or holed
    // roof) that dims rather than blocks, stamped as canopy attenuation the way
    // foliage is. Depth matters because the light column attenuates once per
    // voxel it crosses — at the default extinction one layer only cuts about
    // half, so a few are needed to read as real shade.
    //
    // Neither path respects the hole mask: that lives in a texture the shader
    // samples per fragment, while this is a CPU pass at voxel-column
    // resolution. Dial this down to approximate a holed roof's average.
    [Export(PropertyHint.Range, "0,1,0.01")] public float partialSunOcclusion = 0.7f;
    [Export(PropertyHint.Range, "0,32,1")] public int partialSunOcclusionDepthVoxels = 6;
}
