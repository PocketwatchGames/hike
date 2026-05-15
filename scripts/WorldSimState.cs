using System;
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
    // Each output tier (standard / high-quality) is its own RecipeData so
    // the set is flat — no per-recipe state needed.
    public readonly HashSet<RecipeData> DiscoveredRecipes = new();

    // Items the player has identified by using at least once. Keyed by the
    // shared ItemData resource, so identifying any stack reveals the name
    // everywhere it appears (inventory, recipe rows, cook announcements).
    // Items whose ItemData.unidentifiedDisplayName is empty are implicitly
    // always identified and never appear in this set.
    public readonly HashSet<ItemData> IdentifiedItems = new();

    // Mob types the player has perceived to the Discovered threshold at
    // least once. Keyed by the shared MobData resource so every individual
    // goblin contributes to one bestiary entry. The bestiary lists this set;
    // the entry persists once added (the per-mob DiscoveryState can decay
    // back to Hidden, the bestiary entry doesn't).
    public readonly HashSet<MobData> DiscoveredMobs = new();

    // Fired the first time an item is identified. GameClient subscribes to
    // forward an announcement; UI surfaces that show item names refresh
    // through their existing onChanged paths and don't need this event.
    public event Action<ItemData> onItemIdentified;

    // Fired the first time a recipe enters DiscoveredRecipes. With each
    // tier authored as its own recipe, this fires once per (recipe, output)
    // the player newly earns — including the high-quality tier of a dish
    // whose standard variant they already had.
    public event Action<RecipeData> onRecipeDiscovered;

    // Fired the first time a mob type enters DiscoveredMobs. GameClient
    // subscribes to forward an announcement; the bestiary refreshes through
    // its own VisibilityChanged path.
    public event Action<MobData> onMobDiscovered;

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
    // a one-time reveal effect if desired. Also raises onItemIdentified
    // on first identification so the announcement bus picks it up.
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
        if (!IdentifiedItems.Add(data))
        {
            return false;
        }
        onItemIdentified?.Invoke(data);
        return true;
    }

    // Records a discovery and fires onRecipeDiscovered. Returns true on
    // first discovery; subsequent calls for the same recipe are silent.
    public bool DiscoverRecipe(RecipeData recipe)
    {
        if (recipe == null || !DiscoveredRecipes.Add(recipe))
        {
            return false;
        }
        onRecipeDiscovered?.Invoke(recipe);
        return true;
    }

    // Records a mob-type discovery and fires onMobDiscovered. Returns true
    // on first discovery; subsequent calls for the same mob type are silent.
    // Called from the two paths that transition a mob's per-instance
    // DiscoveryState to Discovered (perception threshold in MobAI, yell
    // promotion in Mob). Mobs whose MobData.appearsInBestiary is false
    // (villagers, livestock) skip the bestiary entry and the announcement
    // entirely — they're "common knowledge" and shouldn't pop a banner the
    // first time the player sees one.
    public bool DiscoverMob(MobData mob)
    {
        if (mob == null || !mob.appearsInBestiary || !DiscoveredMobs.Add(mob))
        {
            return false;
        }
        onMobDiscovered?.Invoke(mob);
        return true;
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
