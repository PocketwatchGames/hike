using Godot;

// Per-region "palette" for AUTO terrain. The mesher passes a KitId per vertex
// and the shader uses this kit to pick between FlatTile / SlopeTile / WallTile
// based on surface normal.y. Multiple kits let different biomes (forest,
// desert, cave) share the AUTO sentinel pipeline without hardcoding tile
// indices in the shader. KitId is authored per-voxel so caves beneath an
// overhang can use a different kit than the surface above.
[GlobalClass]
public partial class EnvironmentKitData : Resource
{
    [Export] public string KitName = "";

    // Flat / wall tile array indices. Natural terrain reads as flat until the
    // surface tilts past WallBand, then transitions to wall — no intermediate
    // "slope" tile. Dirt-style middle-ground appearance is authored via the
    // overlay system (EdgeOverlayTile below) rather than driven by shader slope.
    [Export] public int FlatTile = VoxelTypeInfo.TILE_GRASS_TOP;
    [Export] public int WallTile = VoxelTypeInfo.TILE_STONE;

    // Tile used for OverlayId=edge (1-voxel bumps, walkable ramps, embankments
    // — features the smooth mesh normal can't see but worldgen marks per-voxel).
    // Per-kit so each biome decides what "authored edge" looks like (dirt in
    // temperate, crusted sand in desert, rubble in caves).
    [Export] public int EdgeOverlayTile = VoxelTypeInfo.TILE_DIRT;

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
}
