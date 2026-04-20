using Godot;

// Per-region "palette" for AUTO terrain. The mesher passes a KitId per vertex
// and the shader uses this kit to pick between FlatTile / WallTile based on
// surface normal.y. Multiple kits let different biomes (forest, desert,
// cave) share the AUTO sentinel pipeline without hardcoding tile indices in
// the shader. KitId is authored per-voxel so caves beneath an overhang can
// use a different kit than the surface above.
//
// Overlays are *not* a property of the kit — OverlayId is authored per-voxel
// as a direct tile_array base-layer index (0 = no overlay, non-zero = any
// overlay tile in the global catalog). This lets worldgen mix dirt, clover,
// flowers, puddles, moss, etc. freely across voxels regardless of which kit
// the voxel belongs to.
[GlobalClass]
public partial class EnvironmentKitData : Resource
{
    [Export] public string KitName = "";

    // Flat / wall tile array indices. Natural terrain reads as flat until the
    // surface tilts past WallBand, then transitions to wall — no intermediate
    // "slope" tile. Dirt-style middle-ground appearance is authored via the
    // global overlay system (see OverlayId in ChunkState) rather than driven
    // by shader slope.
    [Export] public int FlatTile = VoxelTypeInfo.TILE_GRASS_TOP;
    [Export] public int WallTile = VoxelTypeInfo.TILE_STONE;

    // Smoothstep band on surface normal.y (1 = flat-up, 0 = vertical).
    //   y < WallBand.x            -> 100% WallTile
    //   WallBand.x..WallBand.y    -> blend WallTile <-> FlatTile
    //   y > WallBand.y            -> 100% FlatTile
    // A single transition keeps cliff tops sharp; the old 3-band scheme always
    // painted a dirt ring at plateau edges because per-fragment normals
    // interpolate through the middle band on smooth meshes.
    [Export] public Vector2 WallBand = new Vector2(0.40f, 0.75f);

    // Edge-jitter amplitude at boundaries with adjacent voxel types / kits.
    // 0 = straight bisector (crisp, for man-made walls). Higher = more jagged.
    [Export] public float BlendAmp = 0.55f;

    // Detail-sprite root tint for blades/flowers scattered on an AUTO-Terrain
    // voxel belonging to this kit. ChunkDetailScatter uses this to tint the
    // bottom texels of each sprite so blades read as rooted in this kit's
    // ground. Bias it ~50% darker than the authored FlatTile average so the
    // tint doubles as a fake contact-AO at the blade base. Authored-override
    // VoxelTypes (Grass/Dirt/Sand/etc.) bypass this and use
    // VoxelTypeInfo.GroundTint instead.
    [Export] public Color GroundTint = new Color(0.16f, 0.22f, 0.09f);
}
