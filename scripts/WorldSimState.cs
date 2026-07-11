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

    // Drop any stashed item whose spoil deadline (ItemState.removeOnDay) has been
    // reached — perishables (meat, mushrooms) vanish from the shared party stashes
    // at the sunrise their day arrives, mirroring the backpack sweep in
    // Player.TickItemExpiry. Called from World.AdvanceToNextSunrise on the day
    // rollover. The equipment stash is swept too for symmetry; equipment never
    // sets removeOnDay, so it's a no-op there.
    public void PruneExpiredPerishables(int dayNumber)
    {
        PruneExpiredStash(PartyMaterialStash, dayNumber);
        PruneExpiredStash(PartyEquipmentStash, dayNumber);
    }

    private static void PruneExpiredStash(List<ItemState> stash, int dayNumber)
    {
        for (int i = stash.Count - 1; i >= 0; i--)
        {
            ItemState item = stash[i];
            if (item != null && item.removeOnDay != 0 && dayNumber >= item.removeOnDay)
            {
                stash.RemoveAt(i);
            }
        }
    }

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

    // World position of the climbable tree the player is currently perched in, or
    // null when not climbing. Drives the active (red) tint on that tree's map
    // marker (IsMarkerActive). Runtime-only; set/cleared by Player.EnterClimbableTree
    // / OnBirdsEyeReturnComplete.
    public Vector3? ActiveClimbTreePosition;

    // Per-forge marker cache (reactivation day + level), keyed by quantized world
    // position, so the map can tint a forge marker ready/inert (see IsMarkerActive),
    // pick its slot icon, and stamp its level even while the forge's chunk is
    // unloaded. A forge registers itself here on stream-in and on use. Runtime
    // cache, not serialized: each forge's own state rides the chunk data and
    // re-registers on stream-in.
    public readonly Dictionary<Vector3I, ForgeMarkerInfo> ForgeMarkers = new();

    // Register a forge's reactivation day (0 = ready), level, and slot for map display.
    public void SetForgeReactivate(Vector3 worldPos, int reactivateDay, int level, EUpgradeSlot slot)
    {
        ForgeMarkers[MapMarkerRecord.KeyFor(worldPos)] = new ForgeMarkerInfo(reactivateDay, level, slot);
    }

    // Forge marker state for the map, if a forge is registered at this position.
    public bool TryGetForgeMarker(Vector3 worldPos, out ForgeMarkerInfo info)
    {
        return ForgeMarkers.TryGetValue(MapMarkerRecord.KeyFor(worldPos), out info);
    }

    // Per-knowledge-stone concept list, keyed by quantized world position, so the
    // map can dim a stone's marker once the party has learned everything it
    // teaches (IsMarkerActive) even while the stone's chunk is unloaded. A stone
    // registers on stream-in (KnowledgeStone.OnSpawned). Runtime cache, not
    // serialized: each stone re-registers on stream-in, and known-ness is derived
    // live from the (serialized) Knowledge stores.
    public readonly Dictionary<Vector3I, Godot.Collections.Array<TeachableConcept>> KnowledgeStoneMarkers = new();

    // Register the concepts a knowledge stone teaches for map-marker dimming.
    public void SetKnowledgeStoneConcepts(Vector3 worldPos, Godot.Collections.Array<TeachableConcept> concepts)
    {
        KnowledgeStoneMarkers[MapMarkerRecord.KeyFor(worldPos)] = concepts;
    }

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
    public EKnowledgeCategory BankActiveKnowledge() => Party?.BankActive() ?? EKnowledgeCategory.None;

    // Drop the provisional tree-climb world-map snapshots so the world map reverts
    // to the banked party pool only. Called from Minimap.RebuildExplorationDisplay —
    // the single choke point hit whenever the fog display is reseeded from the party
    // pool (camp bank, member switch, revive). This keeps the region/marker snapshots
    // PLAYER-TIED exactly like the fog: a member's un-banked survey graduates onto the
    // world map at a tree climb but is lost when that field knowledge is (death /
    // permanent destroy / switching away), leaving only what the party actually banked.
    public void ClearWorldMapSnapshots()
    {
        _worldMapRegionSnapshot.Clear();
        _worldMapMarkerSnapshot.Clear();
    }

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

    // Frozen "what the world map shows" snapshots: everything banked, PLUS a
    // provisional snapshot captured each time the player scouts from a climbable
    // tree (SnapshotWorldMapReveal). Walking around afterwards does NOT add to
    // them — only the next tree climb re-snapshots, matching the exploration fog
    // snapshot. Camp banks the field knowledge and clears these (the banked pool
    // then covers everything). Regions gate labels; markers gate icons.
    readonly HashSet<RegionData> _worldMapRegionSnapshot = new();
    readonly Dictionary<Vector3I, MapMarkerRecord> _worldMapMarkerSnapshot = new();

    public bool IsRegionShownOnWorldMap(RegionData region)
    {
        if (region == null)
        {
            return false;
        }
        return IsRegionBanked(region) || _worldMapRegionSnapshot.Contains(region);
    }

    // Graduate the field-discovered knowledge ("as discovered up until this point")
    // onto the world map as a frozen snapshot — regions (labels) and markers
    // (icons). Called from the tree-climb scout; unlike a campfire bank it leaves
    // the provisional store untouched, so the knowledge stays un-banked until the
    // player actually returns to a fire.
    public void SnapshotWorldMapReveal()
    {
        foreach (RegionData r in EnumerateDiscoveredRegions())
        {
            _worldMapRegionSnapshot.Add(r);
        }
        // Markers: capture the current union (party ∪ active) so field-charted
        // landmarks show on the world map without waiting for a camp bank.
        foreach (MapMarkerRecord record in EnumerateMarkers())
        {
            _worldMapMarkerSnapshot[MapMarkerRecord.KeyFor(record.WorldPosition)] = record;
        }
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

    // True if the marker at worldPos is currently in its ACTIVE state — read at
    // RENDER time (never stored on the record) so the map's tint tracks the real
    // world even while the host's chunk is unloaded. Both caches (LitCampfire,
    // ForgeMarkers) are maintained across chunk unload / re-established on
    // stream-in, so a distant host still reads correctly.
    //   - Campfire: active = this is the world's single lit campfire.
    //   - Forge: active = usable (past its reactivation deadline; inert while on
    //     its sunrise cooldown).
    public bool IsMarkerActive(Vector3 worldPos)
    {
        Vector3I key = MapMarkerRecord.KeyFor(worldPos);
        // The climbable tree the player is currently perched in reads as active
        // (its marker draws in the active/red tint). Set in Player.EnterClimbableTree.
        if (ActiveClimbTreePosition.HasValue && MapMarkerRecord.KeyFor(ActiveClimbTreePosition.Value) == key)
        {
            return true;
        }
        CampfireSimState lit = LitCampfire;
        if (lit != null && MapMarkerRecord.KeyFor(lit.WorldPosition) == key)
        {
            return true;
        }
        if (ForgeMarkers.TryGetValue(key, out ForgeMarkerInfo forge))
        {
            return (World.Current?.DayNumber ?? 0) >= forge.ReactivateDay;
        }
        // Knowledge stone: active (bright) while the party still has something to
        // learn from it; inactive (dim) once every concept it teaches is known in
        // either store. Derived live so learning the same concept elsewhere (a
        // scroll, another stone) dims this one even while its chunk is unloaded.
        if (KnowledgeStoneMarkers.TryGetValue(key, out Godot.Collections.Array<TeachableConcept> concepts))
        {
            return KnowledgeStoneHasUnlearned(concepts);
        }
        return false;
    }

    // True if any concept in `concepts` is not yet known (party ∪ active store).
    // A null/empty list has nothing left to teach, so it reads as fully learned.
    bool KnowledgeStoneHasUnlearned(Godot.Collections.Array<TeachableConcept> concepts)
    {
        if (concepts == null || concepts.Count == 0)
        {
            return false;
        }
        Player player = World.Current?.player;
        foreach (TeachableConcept concept in concepts)
        {
            if (concept != null && !concept.IsKnown(player))
            {
                return true;
            }
        }
        return false;
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

    // WORLD-MAP markers = banked pool ∪ the frozen tree-climb snapshot. The snapshot
    // graduates field-charted landmarks onto the world map at a tree climb and holds
    // them frozen there until banked (walking never adds), mirroring the region-label
    // and fog snapshots. Snapshot record wins on a key collision (it's the union
    // capture, so at least the banked tier). Each record is reported at its LIVE
    // identification tier (see WithLiveMarkerLevel): the SET of world-map markers
    // stays frozen, but a marker already shown as "?" upgrades to its real icon the
    // moment it's identified in the field, without waiting for a camp bank.
    public IEnumerable<MapMarkerRecord> EnumerateWorldMapMarkers()
    {
        var seen = new HashSet<Vector3I>();
        foreach (KeyValuePair<Vector3I, MapMarkerRecord> kv in _worldMapMarkerSnapshot)
        {
            seen.Add(kv.Key);
            yield return WithLiveMarkerLevel(kv.Key, kv.Value);
        }
        Knowledge banked = Banked;
        if (banked != null)
        {
            foreach (KeyValuePair<Vector3I, MapMarkerRecord> kv in banked.DiscoveredMarkers)
            {
                if (seen.Add(kv.Key))
                {
                    yield return WithLiveMarkerLevel(kv.Key, kv.Value);
                }
            }
        }
    }

    // Report a world-map marker at the CURRENT union (party ∪ active) tier so a
    // field identification promotes an already-shown "?" to its real icon
    // provisionally, before the change banks. The display data (icon/name/tints)
    // already rides on the frozen record — it's stamped at Sensed — so only the
    // Level needs bumping; return a shallow copy so the shared store/snapshot
    // record is never mutated (that would silently persist the identification past
    // an un-banked field death, breaking the provisional split).
    MapMarkerRecord WithLiveMarkerLevel(Vector3I key, MapMarkerRecord record)
    {
        EMapMarkerLevel live = GetMarkerLevel(key);
        if (live <= record.Level)
        {
            return record;
        }
        return new MapMarkerRecord(record.WorldPosition, live, record.Icon, record.DisplayName,
            record.HasActiveState, record.IconModulate, record.ActiveModulate);
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
        return (Banked?.DiscoveredSpecies.Contains(species) ?? false)
            || (Active?.DiscoveredSpecies.Contains(species) ?? false);
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
        store.DiscoveredSpecies.Add(species);
        onSpeciesDiscovered?.Invoke(species);
        return true;
    }

    // All discovered species, one per species even when present in both stores
    // (party pool ∪ active member). Backs the bestiary screen.
    public IEnumerable<SpeciesData> EnumerateBestiary()
    {
        var seen = new HashSet<SpeciesData>();
        Knowledge banked = Banked;
        if (banked != null)
        {
            foreach (SpeciesData species in banked.DiscoveredSpecies)
            {
                if (seen.Add(species))
                {
                    yield return species;
                }
            }
        }
        Knowledge active = Active;
        if (active != null)
        {
            foreach (SpeciesData species in active.DiscoveredSpecies)
            {
                if (seen.Add(species))
                {
                    yield return species;
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
