using System.Collections.Generic;
using Godot;

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
    // Per-species bestiary progress (kill counts today). Kills accumulate here and
    // are SUMMED across party+individual on read and on merge.
    public readonly Dictionary<SpeciesData, MobBestiaryEntry> DiscoveredSpecies = new();
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

    // Fold `other` into this store: union the sets, SUM species kills, OR language
    // component bits. Used to bank a member's field knowledge into the permanent
    // party pool.
    public void MergeFrom(Knowledge other)
    {
        if (other == null)
        {
            return;
        }
        IdentifiedItems.UnionWith(other.IdentifiedItems);
        DiscoveredRecipes.UnionWith(other.DiscoveredRecipes);
        DiscoveredRegions.UnionWith(other.DiscoveredRegions);
        foreach (KeyValuePair<SpeciesData, MobBestiaryEntry> kv in other.DiscoveredSpecies)
        {
            if (kv.Key == null)
            {
                continue;
            }
            if (!DiscoveredSpecies.TryGetValue(kv.Key, out MobBestiaryEntry entry))
            {
                entry = new MobBestiaryEntry();
                DiscoveredSpecies[kv.Key] = entry;
            }
            entry.Kills += kv.Value?.Kills ?? 0;
        }
        foreach (KeyValuePair<LanguageData, ELanguageComponents> kv in other.LearnedLanguages)
        {
            if (kv.Key == null)
            {
                continue;
            }
            LearnedLanguages.TryGetValue(kv.Key, out ELanguageComponents existing);
            LearnedLanguages[kv.Key] = existing | kv.Value;
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
            }
            else if (kv.Value.Level > existing.Level)
            {
                existing.Level = kv.Value.Level;
                existing.Icon ??= kv.Value.Icon;
                existing.DisplayName ??= kv.Value.DisplayName;
                existing.HasActiveState = kv.Value.HasActiveState;
                existing.IconModulate = kv.Value.IconModulate;
                existing.ActiveModulate = kv.Value.ActiveModulate;
            }
        }
        Exploration.MergeFrom(other.Exploration);
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
