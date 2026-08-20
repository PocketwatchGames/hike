using Godot;

// What the ground IS: the four terrain kits a column's voxels are stamped with,
// picked by where the column sits relative to water.
//
// Separate from SpawnSetData on purpose. Ground and the things standing on it
// are independent axes — a pine stand runs across mountain rock and forest soil
// alike, and a shoreline is shoreline whether or not anything grows on it.
// Bundling them is what forced a "pine stand" to be re-authored inside every
// kit that wanted one.
//
// Kits still own the MATERIAL (block, detail sprites): that is what the ground
// looks like up close, as opposed to what has been placed on top of it.
[GlobalClass]
public partial class GroundSetData : Resource
{
    [Export] public string displayName = "";

    // Swatch on the painter's palette button and wash on the map, so the
    // toolbar doubles as the legend.
    [Export] public Color mapColor = new Color(0.55f, 0.5f, 0.4f);

    // Dry ground above the shore band.
    [Export] public TerrainKitData surfaceKit;

    // Ground the water stands over.
    [Export] public TerrainKitData submergedKit;

    // The band straddling the waterline.
    [Export] public TerrainKitData shoreKit;

    // Below the top few voxels — what a tunnel bored through this ground
    // exposes.
    [Export] public TerrainKitData caveKit;

    public string Label => string.IsNullOrEmpty(displayName)
        ? (string.IsNullOrEmpty(ResourcePath) ? "Ground" : ResourcePath.GetFile().GetBaseName())
        : displayName;
}
