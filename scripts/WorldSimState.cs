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

    // Items the player has identified by using at least once. Keyed by the
    // shared ItemData resource, so identifying any stack reveals the name
    // everywhere it appears (inventory, recipe rows, cook announcements).
    // Items whose ItemData.unidentifiedDisplayName is empty are implicitly
    // always identified and never appear in this set.
    public readonly HashSet<ItemData> IdentifiedItems = new();

    public bool IsItemIdentified(ItemData data)
    {
        if (data == null)
        {
            return true;
        }
        if (string.IsNullOrEmpty(data.unidentifiedDisplayName.ToString()))
        {
            return true;
        }
        return IdentifiedItems.Contains(data);
    }

    // Returns true on first identification; false if the item was already
    // identified or has no placeholder name. Callers can use this to fire
    // a one-time reveal effect if desired.
    public bool IdentifyItem(ItemData data)
    {
        if (data == null)
        {
            return false;
        }
        if (string.IsNullOrEmpty(data.unidentifiedDisplayName.ToString()))
        {
            return false;
        }
        return IdentifiedItems.Add(data);
    }

    // Single read-side for item names — returns the placeholder while the
    // item is unidentified, the real displayName otherwise. All UI that
    // renders an item name should route through this so the inventory,
    // recipe list, and cook announcement stay in sync.
    public string GetItemDisplayName(ItemData data)
    {
        if (data == null)
        {
            return string.Empty;
        }
        if (!IsItemIdentified(data))
        {
            return data.unidentifiedDisplayName.ToString();
        }
        // Scrolls auto-derive their identified name from the concept they
        // teach ("Scroll of <region name>", etc) so authors don't have to
        // keep the displayName field in sync with the concept ref. The
        // unidentified path above still uses the static placeholder
        // (typically "Unknown Scroll") so the reveal moment shows the
        // specific thing the scroll teaches in one go.
        if (data is ScrollData scroll)
        {
            return scroll.GetEffectiveDisplayName();
        }
        return data.displayName.ToString();
    }
}
