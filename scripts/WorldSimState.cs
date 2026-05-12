using System.Collections.Generic;

// Global, world-scope simulation state that lives outside per-chunk voxel
// data. Things tracked per-save that don't naturally belong on a chunk or
// entity — discovered regions today, quest progress and world flags later.
//
// Owned by WorldState (worldState.SimState). When SaveGame graduates from
// its stub this is the object the save layer reads/writes for run-spanning
// player progression; the chunk delta layer covers per-chunk mutations.
public class WorldSimState
{
    // Regions the player has entered at least once during this run.
    // GameClient.UpdateRegion adds to this on each region-entry commit;
    // WorldMapScreen reads it to gate which region-name labels are
    // visible. Keyed by the shared RegionData resource instance.
    public readonly HashSet<RegionData> DiscoveredRegions = new();

    // Recipes the player has crafted at least once during this run.
    // Keyed by the shared RecipeData resource instance; the value tracks
    // whether the high-quality output has been produced and the minimum
    // ingredient counts that have yielded a standard-quality success.
    public readonly Dictionary<RecipeData, DiscoveredRecipeState> DiscoveredRecipes = new();
}
