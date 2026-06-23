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

    // Species variants the player has discovered, keyed by the shared
    // SpeciesData resource so every individual forest-goblin contributes to one
    // bestiary row (and each biome variant of a type is tracked separately). The
    // value carries the running per-species progress (kills, future sightings /
    // drop logs). The entry persists once added — the per-mob
    // EPlayerPerceptionState can decay back to Hidden, but the bestiary entry
    // stays. ContainsKey(species) is the "is discovered?" test; the bestiary
    // groups these rows into pages by SpeciesData.mob.
    public readonly Dictionary<SpeciesData, MobBestiaryEntry> DiscoveredSpecies = new();

    // Global player stash — a single shared store the player reaches from the
    // Stash tab of any campfire's camp screen (there is no physical stash
    // chest). Capped in practice by the camp StashScreen's slot count. Persisted
    // by SaveGame alongside the other run-spanning progression here.
    public readonly List<ItemState> CampStash = new();

    // Central bank of named scripting variables — quest progress, world flags
    // (boss defeated), counters — read/written by ScriptVarCondition /
    // ScriptVarTransition / SetScriptVarAction to branch conversations and mob
    // behaviors. Seeded from SimData.ScriptVariables at world creation (the
    // WorldState constructor calls Initialize) and serialized by SaveGame.
    public readonly ScriptVariableBank ScriptVars = new();

    // Fired the first time an item is identified. GameClient subscribes to
    // forward an announcement; UI surfaces that show item names refresh
    // through their existing onChanged paths and don't need this event.
    public event Action<ItemData> onItemIdentified;

    // Fired the first time a recipe enters DiscoveredRecipes. With each
    // tier authored as its own recipe, this fires once per (recipe, output)
    // the player newly earns — including the high-quality tier of a dish
    // whose standard variant they already had.
    public event Action<RecipeData> onRecipeDiscovered;

    // Fired the first time a species enters DiscoveredSpecies. GameClient
    // subscribes to forward an announcement; the bestiary refreshes through
    // its own VisibilityChanged path.
    public event Action<SpeciesData> onSpeciesDiscovered;

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
    // Pass identifyOutput=true to also identify the recipe's output item
    // silently (no separate onItemIdentified) before the recipe banner
    // fires — used by scrolls / NPC teaching so the recipe banner reads
    // with the real name instead of "Unknown Food" and no redundant
    // "Item Identified" banner follows. Returns true if either the recipe
    // or the output was newly recorded.
    public bool DiscoverRecipe(RecipeData recipe, bool identifyOutput = false)
    {
        if (recipe == null)
        {
            return false;
        }
        bool identified = false;
        if (identifyOutput && recipe.outputItem != null && !string.IsNullOrEmpty(recipe.outputItem.unidentifiedDisplayName.ToString()))
        {
            identified = IdentifiedItems.Add(recipe.outputItem);
        }
        if (!DiscoveredRecipes.Add(recipe))
        {
            return identified;
        }
        onRecipeDiscovered?.Invoke(recipe);
        return true;
    }

    // Records a species discovery and fires onSpeciesDiscovered. Returns true
    // on first discovery; subsequent calls for the same species are silent.
    // Called from the paths that transition a mob's per-instance DiscoveryState
    // to Discovered (perception threshold in MobAI, corpse spotting, yell
    // promotion in Mob). Species whose base MobData.appearsInBestiary is false
    // (villagers, livestock) skip the bestiary entry and the announcement
    // entirely — they're "common knowledge" and shouldn't pop a banner the
    // first time the player sees one. A null species (editor-placed mob with no
    // variant) is a silent no-op.
    public bool DiscoverSpecies(SpeciesData species)
    {
        if (species == null || species.mob == null || !species.mob.appearsInBestiary
            || DiscoveredSpecies.ContainsKey(species))
        {
            return false;
        }
        DiscoveredSpecies[species] = new MobBestiaryEntry();
        onSpeciesDiscovered?.Invoke(species);
        return true;
    }

    // Records a confirmed player kill against the given species. If the species
    // hasn't been discovered yet this also creates the entry and fires
    // onSpeciesDiscovered (killing a mob you never properly spotted still earns
    // the bestiary row + the discovery announcement). Species whose base
    // MobData.appearsInBestiary is false (or a null species) silently no-op —
    // villagers killed in error don't show up in the bestiary either.
    public void RecordSpeciesKill(SpeciesData species)
    {
        if (species == null || species.mob == null || !species.mob.appearsInBestiary)
        {
            return;
        }
        if (!DiscoveredSpecies.TryGetValue(species, out MobBestiaryEntry entry))
        {
            entry = new MobBestiaryEntry();
            DiscoveredSpecies[species] = entry;
            onSpeciesDiscovered?.Invoke(species);
        }
        entry.Kills++;
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

    // State-aware overload: composes the permanent weapon-mod affixes carried by
    // the live item onto the base name (e.g. "Fragile bomb of Lightning"). Routes
    // the noun through the ItemData overload above, so an unidentified item still
    // shows only its placeholder — affixes are withheld until it's identified
    // rather than leaking the reveal.
    public string GetItemDisplayName(ItemState item)
    {
        if (item == null)
        {
            return string.Empty;
        }
        string baseName = GetItemDisplayName(item.data);
        if (!IsItemIdentified(item.data))
        {
            return baseName;
        }
        return WeaponNameGenerator.Compose(baseName, item);
    }
}
