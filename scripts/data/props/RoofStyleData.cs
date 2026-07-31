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

// What a roof does at the ENDS of its seam — the only place the two forms
// differ, since both are the same cross-section swept over the footprint.
//   Gable — the ends are vertical walls, and the slopes oversail them by the
//           style's rake overhang.
//   Hip   — the ends slope in to the seam at the same pitch as the sides, so
//           all four edges are eaves and nothing oversails. The seam shortens
//           to whichever footprint axis is longer, and a square one peaks at a
//           point: a pointy tower roof.
//
// APPEND new members only, for the same reason as above.
public enum ERoofForm
{
    Gable,
    Hip,
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

    // Optional palette-button art. Null falls back to the name label.
    [Export] public Texture2D icon;

    // Texture repeats per meter of roof surface. Authored per style because a
    // plank is a different real-world size than a slate tile.
    [Export(PropertyHint.Range, "0.05,4,0.01")] public float textureTilesPerMeter = 0.35f;

    // How far the roof projects past the footprint along an EAVE. Overhang is
    // what stops a roof reading as a lid sitting exactly on the walls. On a hip
    // all four edges are eaves, so this is the only overhang that applies.
    [Export(PropertyHint.Range, "0,3,0.05")] public float eaveOverhang = 0.5f;

    // GABLE ONLY: how far the sloped planes OVERSAIL the gable end. The vertical
    // end face stays flush with the footprint — on the wall it rests on — and
    // only the slab continues past it, so the roof visibly caps the wall instead
    // of stopping dead on it. The classic rake / verge overhang.
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

    // Used only when blocksSun is false: partial cover that dims rather than
    // blocks. Depth matters because the light column attenuates once per voxel
    // it crosses — at the default extinction one layer only cuts about half, so
    // a few are needed to read as real shade. (Holes are handled by `broken`
    // below, which punches real openings; this is for a roof that is uniformly
    // thin rather than perforated.)
    [Export(PropertyHint.Range, "0,1,0.01")] public float partialSunOcclusion = 0.7f;
    [Export(PropertyHint.Range, "0,32,1")] public int partialSunOcclusionDepthVoxels = 6;

    // HOW BROKEN a given roof is lives per-instance on RoofSimState, driven by
    // the Roofs panel slider — one hut can be derelict next to an intact one
    // sharing this style. What lives here is the CHARACTER of the damage, which
    // really is a property of the material: how big the holes are and how ragged
    // their edges. Procedural noise punches them, evaluated identically on the
    // GPU (roof_broken.gdshaderinc) and the CPU (RoofBrokenNoise) — which is
    // what makes a hole do all three things at once: show sky, let its shadow
    // through, and leave the voxel column beneath it lit so a god ray comes
    // down it.
    //
    // Hole frequency, in holes per meter. Lower = fewer, larger openings.
    [Export(PropertyHint.Range, "0.02,2,0.01")] public float brokenScale = 0.35f;

    // Second, much finer octave, added as a signed perturbation rather than as
    // more holes — so it only bites at a hole's rim and splinters the edge
    // instead of peppering the roof with pinholes.
    //
    // A MULTIPLE of brokenScale, not an absolute frequency: the edge octave is
    // only "fine" relative to the holes it is roughening, and an absolute value
    // silently inverts into a second, coarser set of blobs the moment someone
    // raises brokenScale past it.
    [Export(PropertyHint.Range, "1,16,0.5")] public float brokenEdgeRatio = 5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float brokenEdgeJagged = 0.35f;

    // How much smaller the hole is on the INNER shell than on the outer surface.
    // Below 1 a ring of slab interior stays visible through every opening, which
    // is what gives a discard-cut hole apparent thickness — cut both surfaces at
    // the same threshold and the shaft is clean through, so the roof reads as
    // paper however thick the slab actually is.
    [Export(PropertyHint.Range, "0.2,1,0.01")] public float brokenInnerShrink = 0.72f;

    // Contact darkening in a band just outside each hole, faking the occlusion
    // of a real cut edge. Does most of the work of selling depth at a distance.
    [Export(PropertyHint.Range, "0,1,0.01")] public float brokenRimDarken = 0.55f;
    [Export(PropertyHint.Range, "0.001,0.5,0.001")] public float brokenRimWidth = 0.09f;

    // Widens the holes the SUN pass sees, without touching the holes you see.
    //
    // Two reasons they legitimately differ: the sun occlusion is stamped one
    // sample per 1m voxel column while the surface is cut per fragment, so at a
    // fine brokenScale the CPU aliases and resolves far fewer openings than are
    // actually there; and light through a ragged gap spills into a slightly
    // wider column than the gap's own silhouette. 1 = identical to the visual.
    [Export(PropertyHint.Range, "1,4,0.05")] public float brokenSunBias = 1.8f;

    // NOTE: a roof carries no dust of its own. It marks the space beneath it as
    // interior by stamping sun occlusion, exactly as a voxel ceiling does, and
    // the space class that classification then assigns supplies the air. A roof
    // decides THAT a room is enclosed, never HOW dusty it is.
}
