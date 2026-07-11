using System.Collections.Generic;
using Godot;

// Which categories of knowledge a merge/bank freshly added to the destination
// store. Returned by Knowledge.MergeFrom so a campfire bank can announce exactly
// the kinds of knowledge that were newly committed to the party pool.
[System.Flags]
public enum EKnowledgeCategory
{
    None = 0,
    Map = 1 << 0,       // fog-of-war reveal, discovered regions, or landmark markers
    Recipe = 1 << 1,
    Bestiary = 1 << 2,  // per-species discovery
    Language = 1 << 3,
    Item = 1 << 4,      // identified items
}

// A store of "learned" knowledge — identified items, discovered recipes, revealed
// map regions, bestiary progress, and learned language pieces. Two instances
// exist per run: one PERMANENT party-shared pool (Party.Knowledge) and one
// PROVISIONAL per-member pool (PlayerState.Knowledge) holding what the currently
// active character has learned in the field since the last campfire "bank".
//
// Reads combine the two (see WorldSimState / Player): away from a campfire, "do
// we know X?" is party ∪ active-member. Banking (Party.BankActive, fired when the
// player camps) folds the active member's store into the party pool via MergeFrom
// and then Clears it. Plain class (like Party) — runtime state SaveGame persists
// alongside the roster.
public class Knowledge
{
    public readonly HashSet<ItemData> IdentifiedItems = new();
    public readonly HashSet<RecipeData> DiscoveredRecipes = new();
    public readonly HashSet<RegionData> DiscoveredRegions = new();
    // Per-species bestiary discovery — the set of species this store has charted.
    // Unioned across party+individual on read and on merge.
    public readonly HashSet<SpeciesData> DiscoveredSpecies = new();
    // Per-language learned component bitset; a missing key = fully unknown.
    public readonly Dictionary<LanguageData, ELanguageComponents> LearnedLanguages = new();

    // Per-instance discovered map markers (landmarks charted on the world / minimap),
    // keyed by quantized world position (MapMarkerRecord.KeyFor). Unioned by MAX
    // Level on merge so banking keeps the most-known tier of each marker.
    public readonly Dictionary<Vector3I, MapMarkerRecord> DiscoveredMarkers = new();

    // Fog-of-war minimap reveal (outdoor + per-slice R8 buffers). The active
    // member reveals into their own; the minimap composites max(party, active)
    // for display; banking merges it like the sets below. Lazily allocated by the
    // minimap on first reveal, so an unexplored store costs nothing.
    public readonly ExplorationMask Exploration = new();

    // Fold `other` into this store: union the sets, OR language component bits.
    // Used to bank a member's field knowledge into the permanent
    // party pool. Returns the categories that gained something new here, so the
    // campfire bank can announce exactly what was committed.
    public EKnowledgeCategory MergeFrom(Knowledge other)
    {
        if (other == null)
        {
            return EKnowledgeCategory.None;
        }
        EKnowledgeCategory changed = EKnowledgeCategory.None;

        int itemsBefore = IdentifiedItems.Count;
        IdentifiedItems.UnionWith(other.IdentifiedItems);
        if (IdentifiedItems.Count > itemsBefore) { changed |= EKnowledgeCategory.Item; }

        int recipesBefore = DiscoveredRecipes.Count;
        DiscoveredRecipes.UnionWith(other.DiscoveredRecipes);
        if (DiscoveredRecipes.Count > recipesBefore) { changed |= EKnowledgeCategory.Recipe; }

        int regionsBefore = DiscoveredRegions.Count;
        DiscoveredRegions.UnionWith(other.DiscoveredRegions);
        if (DiscoveredRegions.Count > regionsBefore) { changed |= EKnowledgeCategory.Map; }

        int speciesBefore = DiscoveredSpecies.Count;
        DiscoveredSpecies.UnionWith(other.DiscoveredSpecies);
        if (DiscoveredSpecies.Count > speciesBefore) { changed |= EKnowledgeCategory.Bestiary; }
        foreach (KeyValuePair<LanguageData, ELanguageComponents> kv in other.LearnedLanguages)
        {
            if (kv.Key == null)
            {
                continue;
            }
            LearnedLanguages.TryGetValue(kv.Key, out ELanguageComponents existing);
            ELanguageComponents merged = existing | kv.Value;
            if (merged != existing) { changed |= EKnowledgeCategory.Language; }
            LearnedLanguages[kv.Key] = merged;
        }
        foreach (KeyValuePair<Vector3I, MapMarkerRecord> kv in other.DiscoveredMarkers)
        {
            if (kv.Value == null)
            {
                continue;
            }
            // Copy into a FRESH record so the party pool doesn't alias the active
            // member's object (which gets Cleared right after banking).
            if (!DiscoveredMarkers.TryGetValue(kv.Key, out MapMarkerRecord existing))
            {
                DiscoveredMarkers[kv.Key] = new MapMarkerRecord(
                    kv.Value.WorldPosition, kv.Value.Level, kv.Value.Icon, kv.Value.DisplayName,
                    kv.Value.HasActiveState, kv.Value.IconModulate, kv.Value.ActiveModulate);
                changed |= EKnowledgeCategory.Map;
            }
            else if (kv.Value.Level > existing.Level)
            {
                existing.Level = kv.Value.Level;
                existing.Icon ??= kv.Value.Icon;
                existing.DisplayName ??= kv.Value.DisplayName;
                existing.HasActiveState = kv.Value.HasActiveState;
                existing.IconModulate = kv.Value.IconModulate;
                existing.ActiveModulate = kv.Value.ActiveModulate;
                changed |= EKnowledgeCategory.Map;
            }
        }
        if (Exploration.MergeFrom(other.Exploration)) { changed |= EKnowledgeCategory.Map; }

        return changed;
    }

    public void Clear()
    {
        IdentifiedItems.Clear();
        DiscoveredRecipes.Clear();
        DiscoveredRegions.Clear();
        DiscoveredSpecies.Clear();
        LearnedLanguages.Clear();
        DiscoveredMarkers.Clear();
        Exploration.Clear();
    }
}
