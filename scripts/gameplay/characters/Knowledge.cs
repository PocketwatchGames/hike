using System.Collections.Generic;
using System.IO;
using Godot;

// Which categories of knowledge a merge/bank freshly added to the destination
// store. Returned by Knowledge.MergeFrom so a campfire bank can announce exactly
// the kinds of knowledge that were newly committed to the party pool.
[System.Flags]
public enum EKnowledgeCategory
{
    None = 0,
    Recipe = 1 << 0,
    Bestiary = 1 << 1,  // per-species discovery
    Language = 1 << 2,
    Item = 1 << 3,      // identified items
    Spell = 1 << 4,     // learned alchemy spells
}

// A store of "learned" knowledge — identified items, discovered recipes, bestiary
// progress, and learned language pieces. The map is not in here: it has no
// provisional tier (see MapChart). Two instances exist per run: one PERMANENT party-shared pool (Party.Knowledge) and one
// PROVISIONAL per-member pool (PlayerState.Knowledge) holding what the currently
// active character has learned in the field since the last campfire "bank".
//
// Reads combine the two (see SimState / Player): away from a campfire, "do
// we know X?" is party ∪ active-member. Banking (Party.BankActive, fired when the
// player camps) folds the active member's store into the party pool via MergeFrom
// and then Clears it. Plain class (like Party) — runtime state SaveGame persists
// alongside the roster.
public class Knowledge
{
    public readonly HashSet<ItemData> IdentifiedItems = new();
    public readonly HashSet<RecipeData> DiscoveredRecipes = new();
    // Learned alchemy spells — the single "known" axis for the spell list (a spell
    // is cast, never identified as a physical item, so it has no separate output-
    // identification step the way a cooked recipe does). Gates SpellSelectionPanel.
    public readonly HashSet<SpellData> KnownSpells = new();
    // Per-species bestiary discovery — the set of species this store has charted.
    // Unioned across party+individual on read and on merge.
    public readonly HashSet<SpeciesData> DiscoveredSpecies = new();
    // Per-language learned component bitset; a missing key = fully unknown.
    public readonly Dictionary<LanguageData, ELanguageComponents> LearnedLanguages = new();

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

        int spellsBefore = KnownSpells.Count;
        KnownSpells.UnionWith(other.KnownSpells);
        if (KnownSpells.Count > spellsBefore) { changed |= EKnowledgeCategory.Spell; }

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

        return changed;
    }

    // Inside a shared EntitySerializer table (SaveGame).
    public void Serialize(BinaryWriter w)
    {
        WriteSet(w, IdentifiedItems);
        WriteSet(w, DiscoveredRecipes);
        WriteSet(w, KnownSpells);
        WriteSet(w, DiscoveredSpecies);
        w.Write(LearnedLanguages.Count);
        foreach (KeyValuePair<LanguageData, ELanguageComponents> kv in LearnedLanguages)
        {
            EntitySerializer.WriteRef(w, kv.Key);
            w.Write((int)kv.Value);
        }
    }

    // Adds to this store; a reference that no longer resolves is dropped.
    public void Deserialize(BinaryReader r)
    {
        ReadSet(r, IdentifiedItems);
        ReadSet(r, DiscoveredRecipes);
        ReadSet(r, KnownSpells);
        ReadSet(r, DiscoveredSpecies);
        int languages = r.ReadInt32();
        for (int i = 0; i < languages; i++)
        {
            LanguageData language = EntitySerializer.ReadRef<LanguageData>(r);
            var components = (ELanguageComponents)r.ReadInt32();
            if (language != null)
            {
                LearnedLanguages[language] = components;
            }
        }
    }

    private static void WriteSet<T>(BinaryWriter w, HashSet<T> set) where T : Resource
    {
        w.Write(set.Count);
        foreach (T item in set)
        {
            EntitySerializer.WriteRef(w, item);
        }
    }

    private static void ReadSet<T>(BinaryReader r, HashSet<T> set) where T : Resource
    {
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            T item = EntitySerializer.ReadRef<T>(r);
            if (item != null)
            {
                set.Add(item);
            }
        }
    }

    public void Clear()
    {
        IdentifiedItems.Clear();
        DiscoveredRecipes.Clear();
        KnownSpells.Clear();
        DiscoveredSpecies.Clear();
        LearnedLanguages.Clear();
    }
}
