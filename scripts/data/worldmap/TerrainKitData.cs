using Godot;

// What the ground IS, as one authored choice: the four TerrainData a column's
// voxels are stamped with, picked by where the column sits relative to water.
// The KIT is the unit an author picks ("this ground is forest"); a TerrainData
// is one member of it, and the per-voxel TerrainId byte indexes those members.
//
// Nothing about what GROWS is here, deliberately. Ground and the things standing
// on it are independent axes — a pine stand runs across mountain rock and forest
// soil alike, and a shoreline is shoreline whether or not anything grows on it.
// Bundling them is what forced a "pine stand" to be re-authored per ground.
// Worldgen's answer is ZoneGenData.foliage; the painter fills a painted region
// from a PropListData.
[GlobalClass]
public partial class TerrainKitData : Resource
{
    [Export] public string displayName = "";

    // Swatch on the painter's palette button and wash on the map, so the
    // toolbar doubles as the legend.
    [Export] public Color mapColor = new Color(0.55f, 0.5f, 0.4f);

    // Dry ground above the shore band.
    [Export] public TerrainData surfaceTerrain;

    // Ground the water stands over.
    [Export] public TerrainData submergedTerrain;

    // The band straddling the waterline.
    [Export] public TerrainData shoreTerrain;

    // Below the top few voxels — what a tunnel bored through this ground
    // exposes.
    [Export] public TerrainData caveTerrain;

    public string Label => string.IsNullOrEmpty(displayName)
        ? (string.IsNullOrEmpty(ResourcePath) ? "Ground" : ResourcePath.GetFile().GetBaseName())
        : displayName;
}
