using System;
using System.Collections.Generic;
using Godot;

// Global, world-scope simulation state that lives outside per-chunk voxel
// data. Things tracked per-save that don't naturally belong on a chunk or
// entity — the party roster, learned knowledge, quest flags, the stash.
//
// Owned by WorldState (worldState.SimState). When SaveGame graduates from
// its stub this is the object the save layer reads/writes for run-spanning
// player progression; the chunk delta layer covers per-chunk mutations.
//
// KNOWLEDGE IS TWO-TIER. Identified items, discovered recipes/species/regions
// and learned languages no longer live in flat sets here — they live in two
// Knowledge stores: the permanent party pool (Party.Knowledge) and the active
// member's provisional field store (Party.Active.Knowledge), banked into the
// pool when the player camps (Party.BankActive). This class stays the single
// FACADE the rest of the game talks to: writes go to the active member's store
// (gated on the combined set), reads union party + active member, so every
// existing call site keeps working while knowledge gained in the field is
// provisional until banked.
public class WorldSimState
{
    // Party equipment stash — the shared store of weapons / armor / helmets /
    // equipment the party reaches from the Stash tab of any campfire's camp screen
    // (there is no physical chest). Gear is equipped into slots from here, and a
    // piece displaced by equipping-over returns here. Persisted by SaveGame.
    public readonly List<ItemState> PartyEquipmentStash = new();

    // Party material stash — the shared store of crafting materials (loot, meat,
    // ingredients). The controlled member's carried material backpack drains into
    // this on camping, and cooking pulls ingredients from it. Persisted by SaveGame.
    public readonly List<ItemState> PartyMaterialStash = new();

    // The player's party roster — the characters they can switch between. Built
    // once at game start from WorldGenData.startingParty (GameClient.Init) and
    // persisted here alongside the other run-spanning state. Null until the
    // first build; GameClient guards on that so a future disk-load path that
    // bypasses worldgen doesn't rebuild or double-spawn the party. Also owns the
    // permanent party Knowledge pool that the facade methods below read/write.
    public Party Party;

    // The world's single lit campfire — only one burns at a time. Set when a
    // campfire is lit (Campfire.SetLit / Create) so lighting a new one can douse
    // the previous even when its chunk has unloaded. Runtime cache, not
    // serialized: each campfire's own Active bit already records its lit state,
    // and this reference is re-established as the lit campfire streams in.
    public CampfireSimState LitCampfire;

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

    // Fired the first time a recipe is discovered. With each tier authored as
    // its own recipe, this fires once per (recipe, output) the player newly
    // earns — including the high-quality tier of a dish whose standard variant
    // they already had.
    public event Action<RecipeData> onRecipeDiscovered;

    // Fired the first time a species is discovered. GameClient subscribes to
    // forward an announcement; the bestiary refreshes through its own
    // VisibilityChanged path.
    public event Action<SpeciesData> onSpeciesDiscovered;

    // The two knowledge stores the facade reads/writes. Banked = permanent party
    // pool; Active = the currently-controlled member's provisional field store
    // (null when there's no roster yet, e.g. very early boot). Writes target
    // Active; reads union both.
    Knowledge Banked => Party?.Knowledge;
    Knowledge Active => Party?.Active?.Knowledge;

    // Fold the active member's provisional field knowledge into the permanent
    // party pool. Called when the player camps (GameClient.NotifyCampedAt) — the
    // single "return to a campfire" commit — and once right after spawn so the
    // scenario's initial knowledge is party-permanent from the first frame.
    public void BankActiveKnowledge() => Party?.BankActive();

    // ---- Items -------------------------------------------------------------

    bool IdentifiedInStores(ItemData data) =>
        (Banked?.IdentifiedItems.Contains(data) ?? false)
        || (Active?.IdentifiedItems.Contains(data) ?? false);

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
        return IdentifiedInStores(data);
    }

    // Returns true on first identification; false if the item was already
    // identified (in either store) or has no placeholder name. Records into the
    // active member's store and raises onItemIdentified on first identification.
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
        if (IdentifiedInStores(data))
        {
            return false;
        }
        Knowledge store = Active;
        if (store == null)
        {
            return false;
        }
        store.IdentifiedItems.Add(data);
        onItemIdentified?.Invoke(data);
        return true;
    }

    // ---- Recipes -----------------------------------------------------------

    public bool IsRecipeDiscovered(RecipeData recipe)
    {
        if (recipe == null)
        {
            return false;
        }
        return (Banked?.DiscoveredRecipes.Contains(recipe) ?? false)
            || (Active?.DiscoveredRecipes.Contains(recipe) ?? false);
    }

    // Records a discovery and fires onRecipeDiscovered. Returns true on first
    // discovery; subsequent calls for the same recipe are silent. Pass
    // identifyOutput=true to also identify the recipe's output item silently (no
    // onItemIdentified) before the recipe banner fires — used by scrolls / NPC
    // teaching so the recipe banner reads with the real name instead of "Unknown
    // Food" and no redundant "Item Identified" banner follows. Returns true if
    // either the recipe or the output was newly recorded.
    public bool DiscoverRecipe(RecipeData recipe, bool identifyOutput = false)
    {
        if (recipe == null)
        {
            return false;
        }
        Knowledge store = Active;
        if (store == null)
        {
            return false;
        }
        bool identified = false;
        if (identifyOutput && recipe.outputItem != null
            && !string.IsNullOrEmpty(recipe.outputItem.unidentifiedDisplayName.ToString())
            && !IdentifiedInStores(recipe.outputItem))
        {
            store.IdentifiedItems.Add(recipe.outputItem);
            identified = true;
        }
        if (IsRecipeDiscovered(recipe))
        {
            return identified;
        }
        store.DiscoveredRecipes.Add(recipe);
        onRecipeDiscovered?.Invoke(recipe);
        return true;
    }

    // ---- Regions -----------------------------------------------------------

    public bool IsRegionDiscovered(RegionData region)
    {
        if (region == null)
        {
            return false;
        }
        return (Banked?.DiscoveredRegions.Contains(region) ?? false)
            || (Active?.DiscoveredRegions.Contains(region) ?? false);
    }

    // Map-display gate: a region appears on the world map only once it's been
    // recorded at a campfire (banked into the party pool). A region discovered
    // in the field sits in the active member's provisional store — known for
    // dedup (IsRegionDiscovered) but hidden from the map — until the next camp
    // banks it. Mirrors the exploration fog-of-war split (party pool only).
    public bool IsRegionBanked(RegionData region)
    {
        if (region == null)
        {
            return false;
        }
        return Banked?.DiscoveredRegions.Contains(region) ?? false;
    }

    // Reveals a named map region (region-entry commit, treasure-map scroll, NPC
    // hint). Returns true only when newly recorded. No announcement event —
    // callers own their own region banner.
    public bool DiscoverRegion(RegionData region)
    {
        if (region == null)
        {
            return false;
        }
        Knowledge store = Active;
        if (store == null || IsRegionDiscovered(region))
        {
            return false;
        }
        store.DiscoveredRegions.Add(region);
        return true;
    }

    // Combined (party + active member) discovered regions, for the world-map
    // label pass. Yields each region once even if present in both stores.
    public IEnumerable<RegionData> EnumerateDiscoveredRegions()
    {
        var seen = new HashSet<RegionData>();
        Knowledge banked = Banked;
        if (banked != null)
        {
            foreach (RegionData r in banked.DiscoveredRegions)
            {
                if (seen.Add(r)) { yield return r; }
            }
        }
        Knowledge active = Active;
        if (active != null)
        {
            foreach (RegionData r in active.DiscoveredRegions)
            {
                if (seen.Add(r)) { yield return r; }
            }
        }
    }

    // ---- Map markers -------------------------------------------------------

    // Max discovery tier of the marker at `key` across both stores (Unknown when
    // neither holds a record).
    EMapMarkerLevel GetMarkerLevel(Vector3I key)
    {
        EMapMarkerLevel level = EMapMarkerLevel.Unknown;
        if ((Banked?.DiscoveredMarkers.TryGetValue(key, out MapMarkerRecord b) ?? false) && b.Level > level)
        {
            level = b.Level;
        }
        if ((Active?.DiscoveredMarkers.TryGetValue(key, out MapMarkerRecord a) ?? false) && a.Level > level)
        {
            level = a.Level;
        }
        return level;
    }

    public EMapMarkerLevel GetMarkerLevel(Vector3 worldPos) => GetMarkerLevel(MapMarkerRecord.KeyFor(worldPos));

    public bool IsMarkerDiscovered(Vector3 worldPos) => GetMarkerLevel(worldPos) != EMapMarkerLevel.Unknown;

    // Single write path for the MapMarker node: records/raises the marker at
    // worldPos to at least `level` in the ACTIVE member's store, carrying its
    // display data (icon/name). Covers both the reveal->Sensed step and the
    // identify->Identified step. Writes the delta into Active even when the banked
    // pool already holds a lower tier, so the change banks on the next camp.
    // Returns true when the effective (union) tier actually increased. A marker
    // already at >= `level` in either store is left untouched.
    public bool RecordMarker(Vector3 worldPos, EMapMarkerLevel level, MapMarker marker)
    {
        Knowledge store = Active;
        if (store == null || marker == null || level == EMapMarkerLevel.Unknown)
        {
            return false;
        }
        Vector3I key = MapMarkerRecord.KeyFor(worldPos);
        if (GetMarkerLevel(key) >= level)
        {
            return false;
        }
        if (!store.DiscoveredMarkers.TryGetValue(key, out MapMarkerRecord record))
        {
            store.DiscoveredMarkers[key] = new MapMarkerRecord(worldPos, level, marker.Icon,
                marker.DisplayName, marker.HasActiveState, marker.IconModulate, marker.ActiveModulate);
        }
        else
        {
            record.Level = level;
            record.Icon ??= marker.Icon;
            record.DisplayName ??= marker.DisplayName;
            // Keep the two-state visual config current (cheap; sourced from the node).
            record.HasActiveState = marker.HasActiveState;
            record.IconModulate = marker.IconModulate;
            record.ActiveModulate = marker.ActiveModulate;
        }
        return true;
    }

    // True if the marker at worldPos is currently in its ACTIVE state. Presently
    // this means "the campfire here is the world's lit campfire" — campfires are
    // the only live-state markers. Read at RENDER time (never stored on the record)
    // so the map's lit/unlit tint tracks the real world even while the campfire's
    // chunk is unloaded. LitCampfire is maintained across chunk unload, so a
    // just-lit campfire far away still reads correctly here.
    public bool IsMarkerActive(Vector3 worldPos)
    {
        CampfireSimState lit = LitCampfire;
        if (lit == null)
        {
            return false;
        }
        return MapMarkerRecord.KeyFor(lit.WorldPosition) == MapMarkerRecord.KeyFor(worldPos);
    }

    // Banked (party-pool) markers for the WORLD MAP. Mirrors the region-label /
    // fog-of-war split — a marker charted in the field stays off the world map
    // until camped. Yields the party-pool records directly (renderers read,
    // never mutate).
    public IEnumerable<MapMarkerRecord> EnumerateBankedMarkers()
    {
        Knowledge banked = Banked;
        if (banked == null)
        {
            yield break;
        }
        foreach (MapMarkerRecord record in banked.DiscoveredMarkers.Values)
        {
            yield return record;
        }
    }

    // Party pool ∪ active member's provisional markers for the MINIMAP — the
    // controlled player's field-charted markers show there immediately (matching
    // the minimap's party ∪ active fog-of-war), whereas the world map is
    // banked-only. Deduped by key; the active record wins when both hold one,
    // since RecordMarker only raises Active above the union tier (so it's always
    // the higher of the two).
    public IEnumerable<MapMarkerRecord> EnumerateMarkers()
    {
        var seen = new HashSet<Vector3I>();
        Knowledge active = Active;
        if (active != null)
        {
            foreach (KeyValuePair<Vector3I, MapMarkerRecord> kv in active.DiscoveredMarkers)
            {
                seen.Add(kv.Key);
                yield return kv.Value;
            }
        }
        Knowledge banked = Banked;
        if (banked != null)
        {
            foreach (KeyValuePair<Vector3I, MapMarkerRecord> kv in banked.DiscoveredMarkers)
            {
                if (seen.Add(kv.Key))
                {
                    yield return kv.Value;
                }
            }
        }
    }

    // ---- Species / bestiary ------------------------------------------------

    public bool IsSpeciesDiscovered(SpeciesData species)
    {
        if (species == null)
        {
            return false;
        }
        return (Banked?.DiscoveredSpecies.ContainsKey(species) ?? false)
            || (Active?.DiscoveredSpecies.ContainsKey(species) ?? false);
    }

    // Records a species discovery and fires onSpeciesDiscovered. Returns true on
    // first discovery; subsequent calls for the same species are silent. Species
    // whose base MobData.appearsInBestiary is false (villagers, livestock) skip
    // the entry and the announcement — they're "common knowledge". A null species
    // is a silent no-op.
    public bool DiscoverSpecies(SpeciesData species)
    {
        if (species == null || species.mob == null || !species.mob.appearsInBestiary
            || IsSpeciesDiscovered(species))
        {
            return false;
        }
        Knowledge store = Active;
        if (store == null)
        {
            return false;
        }
        store.DiscoveredSpecies[species] = new MobBestiaryEntry();
        onSpeciesDiscovered?.Invoke(species);
        return true;
    }

    // Records a confirmed player kill against the given species into the active
    // member's store. If the species hasn't been discovered in either store yet
    // this also creates the entry and fires onSpeciesDiscovered. Species whose
    // base MobData.appearsInBestiary is false (or a null species) silently no-op.
    public void RecordSpeciesKill(SpeciesData species)
    {
        if (species == null || species.mob == null || !species.mob.appearsInBestiary)
        {
            return;
        }
        Knowledge store = Active;
        if (store == null)
        {
            return;
        }
        if (!IsSpeciesDiscovered(species))
        {
            store.DiscoveredSpecies[species] = new MobBestiaryEntry();
            onSpeciesDiscovered?.Invoke(species);
        }
        if (!store.DiscoveredSpecies.TryGetValue(species, out MobBestiaryEntry entry))
        {
            // Discovered only in the banked pool so far — start an active-store
            // delta entry; combined reads add its kills to the banked total.
            entry = new MobBestiaryEntry();
            store.DiscoveredSpecies[species] = entry;
        }
        entry.Kills++;
    }

    // Combined bestiary entry (party + active member, kills summed) for a single
    // species. Returns a FRESH entry — callers must not write it back into a
    // store. False when the species is undiscovered in both stores.
    public bool TryGetBestiaryEntry(SpeciesData species, out MobBestiaryEntry combined)
    {
        combined = null;
        if (species == null)
        {
            return false;
        }
        bool found = false;
        int kills = 0;
        if (Banked?.DiscoveredSpecies.TryGetValue(species, out MobBestiaryEntry b) ?? false)
        {
            found = true;
            kills += b.Kills;
        }
        if (Active?.DiscoveredSpecies.TryGetValue(species, out MobBestiaryEntry a) ?? false)
        {
            found = true;
            kills += a.Kills;
        }
        if (!found)
        {
            return false;
        }
        combined = new MobBestiaryEntry { Kills = kills };
        return true;
    }

    // All discovered species with their combined (summed) entries, one per
    // species even when present in both stores. Backs the bestiary screen.
    public IEnumerable<(SpeciesData species, MobBestiaryEntry entry)> EnumerateBestiary()
    {
        var seen = new HashSet<SpeciesData>();
        Knowledge banked = Banked;
        if (banked != null)
        {
            foreach (SpeciesData species in banked.DiscoveredSpecies.Keys)
            {
                if (seen.Add(species) && TryGetBestiaryEntry(species, out MobBestiaryEntry e))
                {
                    yield return (species, e);
                }
            }
        }
        Knowledge active = Active;
        if (active != null)
        {
            foreach (SpeciesData species in active.DiscoveredSpecies.Keys)
            {
                if (seen.Add(species) && TryGetBestiaryEntry(species, out MobBestiaryEntry e))
                {
                    yield return (species, e);
                }
            }
        }
    }

    // ---- Item display names ------------------------------------------------

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
