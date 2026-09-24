using System;
using System.Collections.Generic;
using System.IO;
using Godot;

// Global, world-scope simulation state that lives outside per-chunk voxel
// data. Things tracked per-save that don't naturally belong on a chunk or
// entity — the party roster, learned knowledge, quest flags, the stash.
//
// Owned by WorldState (worldState.SimState). When SaveGame graduates from
// its stub this is the object the save layer reads/writes for run-spanning
// player progression; the chunk delta layer covers per-chunk mutations.
//
// KNOWLEDGE IS TWO-TIER. Identified items, discovered recipes/species and
// learned languages no longer live in flat sets here — they live in two
// Knowledge stores: the permanent party pool (Party.Knowledge) and the active
// member's provisional field store (Party.Active.Knowledge), banked into the
// pool when the player camps (Party.BankActive). This class stays the single
// FACADE the rest of the game talks to: writes go to the active member's store
// (gated on the combined set), reads union party + active member, so every
// existing call site keeps working while knowledge gained in the field is
// provisional until banked. The map (fog, regions, markers) is the exception:
// it is one permanent Party.Chart, written directly.
public class SimState
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

    // Age the shared party stashes at the sunrise day rollover: prune each stack's
    // spoiled cohorts (meat, mushrooms) in place and drop any stack that empties,
    // mirroring the backpack sweep in Player.TickItemExpiry. Called from
    // Sim.AdvanceToNextSunrise. The equipment stash is swept too for symmetry;
    // equipment carries no perishable cohorts, so it's a no-op there.
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
            if (item == null)
            {
                continue;
            }
            // Spoiled food cohorts drop in place; the stack leaves the stash only
            // when it empties out, or when a non-food timed drop's whole-item
            // lifespan (removeOnDay) elapses.
            item.PruneExpired(dayNumber);
            bool lifespanElapsed = item.removeOnDay != 0 && dayNumber >= item.removeOnDay;
            if (item.stackCount <= 0 || lifespanElapsed)
            {
                stash.RemoveAt(i);
            }
        }
    }

    // The player's party roster — the characters they can switch between. Built
    // once at game start from WorldStartData.startingParty (GameClient.Init) and
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

    // Spend one full `inputs` cost from the party material stash. All-or-nothing:
    // returns false (spending nothing) when the stash can't cover the cost.
    // Matches reagents up each stack's ItemData.parent chain, the same identity
    // rule Cooking.TryMatch / CountAffordable use.
    public bool TrySpendMaterials(IReadOnlyList<RecipeInput> inputs)
    {
        if (inputs == null || inputs.Count == 0)
        {
            return false;
        }
        if (Cooking.CountAffordable(inputs, PartyMaterialStash) <= 0)
        {
            return false;
        }
        for (int i = 0; i < inputs.Count; i++)
        {
            RecipeInput r = inputs[i];
            if (r?.item == null || r.count <= 0)
            {
                continue;
            }
            int need = r.count;
            for (int s = PartyMaterialStash.Count - 1; s >= 0 && need > 0; s--)
            {
                ItemState stack = PartyMaterialStash[s];
                if (stack?.data == null || stack.stackCount <= 0 || !Cooking.Satisfies(stack.data, r.item))
                {
                    continue;
                }
                int take = stack.Consume(need);
                need -= take;
                if (stack.stackCount <= 0)
                {
                    PartyMaterialStash.RemoveAt(s);
                }
            }
        }
        return true;
    }

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

    // The run's active quests (Rescue, hunt counters, Return to Camp, language
    // learning). World-scope like ScriptVars — GameClient ticks it and feeds it
    // triggers, the HUD surfaces it, and SaveGame serializes it (v4). See QuestLog.
    public readonly QuestLog QuestLog = new();

    // Collected treasure maps — each a pre-rolled dig location + heading pointing
    // at a buried payload, shown as switchable tabs on the world-map screen and
    // removed when the treasure is dug up (Sim.TryDig). Persisted by SaveGame (v5).
    public readonly List<TreasureMapState> TreasureMaps = new();

    // Fired when a treasure map is added or removed so the map screen rebuilds
    // its selector.
    public event Action onTreasureMapsChanged;

    public void AddTreasureMap(TreasureMapState map)
    {
        if (map == null)
        {
            return;
        }
        TreasureMaps.Add(map);
        onTreasureMapsChanged?.Invoke();
    }

    public bool RemoveTreasureMap(TreasureMapState map)
    {
        if (map == null || !TreasureMaps.Remove(map))
        {
            return false;
        }
        onTreasureMapsChanged?.Invoke();
        return true;
    }

    // Remove the treasure map (if any) that points at the buried object just
    // unearthed at worldPos — the map's self-destruction when its treasure is
    // dug up. Matched on the quantized position key, as maps and spots share the
    // same dig location.
    // True when a map for this dig site is already collected. Keyed the same
    // quantized way RemoveTreasureMapAt matches, so "do we have this one" and
    // "remove the one for this hole" can never disagree.
    public bool HasTreasureMapAt(Vector3 worldPos)
    {
        Vector3I key = MapMarkerRecord.KeyFor(worldPos);
        for (int i = 0; i < TreasureMaps.Count; i++)
        {
            if (MapMarkerRecord.KeyFor(TreasureMaps[i].DigLocation) == key)
            {
                return true;
            }
        }
        return false;
    }

    public bool RemoveTreasureMapAt(Vector3 worldPos)
    {
        Vector3I key = MapMarkerRecord.KeyFor(worldPos);
        for (int i = 0; i < TreasureMaps.Count; i++)
        {
            if (MapMarkerRecord.KeyFor(TreasureMaps[i].DigLocation) == key)
            {
                TreasureMaps.RemoveAt(i);
                onTreasureMapsChanged?.Invoke();
                return true;
            }
        }
        return false;
    }

    // Both party stashes, inside a shared EntitySerializer table (SaveGame). An
    // item whose ItemData no longer exists reads back null and is dropped.
    public void SerializeStashes(BinaryWriter w)
    {
        WriteStash(w, PartyEquipmentStash);
        WriteStash(w, PartyMaterialStash);
    }

    public void DeserializeStashes(BinaryReader r)
    {
        ReadStash(r, PartyEquipmentStash);
        ReadStash(r, PartyMaterialStash);
    }

    private static void WriteStash(BinaryWriter w, List<ItemState> stash)
    {
        w.Write(stash.Count);
        foreach (ItemState item in stash)
        {
            EntitySerializer.WriteItem(w, item);
        }
    }

    private static void ReadStash(BinaryReader r, List<ItemState> stash)
    {
        stash.Clear();
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            ItemState item = EntitySerializer.ReadItem(r);
            if (item != null)
            {
                stash.Add(item);
            }
        }
    }

    public void SerializeTreasureMaps(BinaryWriter w)
    {
        w.Write(TreasureMaps.Count);
        for (int i = 0; i < TreasureMaps.Count; i++)
        {
            TreasureMaps[i].Serialize(w);
        }
    }

    public void DeserializeTreasureMaps(BinaryReader r)
    {
        TreasureMaps.Clear();
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            TreasureMaps.Add(TreasureMapState.Deserialize(r));
        }
        onTreasureMapsChanged?.Invoke();
    }

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

    // Fired the first time an alchemy spell is learned. GameClient subscribes to
    // forward an announcement; the alchemy screen refreshes through its own
    // VisibilityChanged path.
    public event Action<SpellData> onSpellLearned;

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

    // The party's map (fog, regions, markers). Not two-tier: every write lands
    // here directly and is never lost.
    MapChart Chart => Party?.Chart;

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

    // A spell is attunable at the alchemy screen once LEARNED (recorded into a
    // Knowledge store's KnownSpells, e.g. via a SpellTeachable in
    // WorldStartData.initialKnowledge or a spell scroll). This is the single "known"
    // axis for spells — deliberately NOT item-identification: a spell is cast, not
    // found and identified as a physical item, so learning is the only gate.
    public bool IsSpellKnown(SpellData spell)
    {
        if (spell == null)
        {
            return false;
        }
        return (Banked?.KnownSpells.Contains(spell) ?? false)
            || (Active?.KnownSpells.Contains(spell) ?? false);
    }

    // Records a spell as learned in the active member's store and fires
    // onSpellLearned. Returns true only on first learn. Because a spell's name IS
    // its output name (no separate identification step), this also silently
    // identifies the spell item so the alchemy screen reads with the real name
    // instead of the "Unknown Potion" placeholder — mirroring DiscoverRecipe's
    // identifyOutput, and without a redundant "Item Identified" banner.
    public bool LearnSpell(SpellData spell)
    {
        if (spell == null)
        {
            return false;
        }
        Knowledge store = Active;
        if (store == null)
        {
            return false;
        }
        if (!string.IsNullOrEmpty(spell.unidentifiedDisplayName.ToString())
            && !IdentifiedInStores(spell))
        {
            store.IdentifiedItems.Add(spell);
        }
        if (IsSpellKnown(spell))
        {
            return false;
        }
        store.KnownSpells.Add(spell);
        onSpellLearned?.Invoke(spell);
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
    // discovery; subsequent calls for the same recipe are silent. Recipes have
    // no identification phase — a recipe is either undiscovered (shown nowhere)
    // or discovered under its real name.
    public bool DiscoverRecipe(RecipeData recipe)
    {
        if (recipe == null)
        {
            return false;
        }
        Knowledge store = Active;
        if (store == null || IsRecipeDiscovered(recipe))
        {
            return false;
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
        return Chart?.DiscoveredRegions.Contains(region) ?? false;
    }

    // Charts a named map region (region-entry, treasure-map scroll, NPC hint).
    // Returns true only when newly recorded. No announcement event — callers own
    // their own region banner.
    public bool DiscoverRegion(RegionData region)
    {
        if (region == null)
        {
            return false;
        }
        return Chart?.DiscoveredRegions.Add(region) ?? false;
    }

    // ---- Map markers -------------------------------------------------------

    public EMapMarkerLevel GetMarkerLevel(Vector3 worldPos)
    {
        MapChart chart = Chart;
        if (chart != null && chart.DiscoveredMarkers.TryGetValue(MapMarkerRecord.KeyFor(worldPos), out MapMarkerRecord record))
        {
            return record.Level;
        }
        return EMapMarkerLevel.Unknown;
    }

    public bool IsMarkerDiscovered(Vector3 worldPos) => GetMarkerLevel(worldPos) != EMapMarkerLevel.Unknown;

    // Single write path for the MapMarker node: records/raises the marker at
    // worldPos to at least `level`, carrying its display data (icon/name). Covers
    // both the reveal->Sensed step and the identify->Identified step. Returns true
    // when the tier actually increased.
    public bool RecordMarker(Vector3 worldPos, EMapMarkerLevel level, MapMarker marker)
    {
        MapChart chart = Chart;
        if (chart == null || marker == null || level == EMapMarkerLevel.Unknown)
        {
            return false;
        }
        Vector3I key = MapMarkerRecord.KeyFor(worldPos);
        if (!chart.DiscoveredMarkers.TryGetValue(key, out MapMarkerRecord record))
        {
            chart.DiscoveredMarkers[key] = new MapMarkerRecord(worldPos, level, marker.Icon,
                marker.DisplayName, marker.HasActiveState, marker.IconModulate, marker.ActiveModulate);
            return true;
        }
        if (record.Level >= level)
        {
            return false;
        }
        record.Level = level;
        record.Icon ??= marker.Icon;
        record.DisplayName ??= marker.DisplayName;
        // Keep the two-state visual config current (cheap; sourced from the node).
        record.HasActiveState = marker.HasActiveState;
        record.IconModulate = marker.IconModulate;
        record.ActiveModulate = marker.ActiveModulate;
        return true;
    }

    // Every charted marker, for both the minimap and the world map. Renderers
    // read the records, never mutate them.
    public IEnumerable<MapMarkerRecord> EnumerateMarkers()
    {
        MapChart chart = Chart;
        if (chart == null)
        {
            yield break;
        }
        foreach (MapMarkerRecord record in chart.DiscoveredMarkers.Values)
        {
            yield return record;
        }
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
            return (Sim.Current?.DayNumber ?? 0) >= forge.ReactivateDay;
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
        Player player = Sim.Current?.player;
        foreach (TeachableConcept concept in concepts)
        {
            if (concept != null && !concept.IsKnown(player))
            {
                return true;
            }
        }
        return false;
    }

    // ---- Character names ---------------------------------------------------

    public bool IsNameKnown(ConversationData character)
    {
        if (character == null)
        {
            return false;
        }
        return (Banked?.KnownNames.Contains(character) ?? false)
            || (Active?.KnownNames.Contains(character) ?? false);
    }

    // Returns true only on a new introduction.
    public bool LearnName(ConversationData character)
    {
        if (character == null || IsNameKnown(character))
        {
            return false;
        }
        Knowledge store = Active;
        if (store == null)
        {
            return false;
        }
        store.KnownNames.Add(character);
        return true;
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
