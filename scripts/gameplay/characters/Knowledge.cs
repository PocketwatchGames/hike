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
    Name = 1 << 5,      // characters whose name the party has learned
    Rune = 1 << 6,      // positions of a species' rune sequence
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
    // Keyed by what the recipe makes: an ItemData crafted at a bench, or a
    // MealData cooked in a pot.
    public readonly HashSet<Resource> DiscoveredRecipes = new();
    // Per-species bestiary discovery — the set of species this store has charted.
    // Unioned across party+individual on read and on merge.
    public readonly HashSet<SpeciesData> DiscoveredSpecies = new();
    // Per language: the SOURCE of each lesson (Player.LessonSourceKey — the
    // inscription or scroll it was read from) -> the percent it taught. One
    // source teaches once. A missing key = nothing learned.
    public readonly Dictionary<LanguageData, Dictionary<string, float>> LearnedLessons = new();
    // Characters introduced by name, keyed by their conversation - it is the one
    // resource per authored character (see ConversationData.nameLocKey).
    public readonly HashSet<ConversationData> KnownNames = new();
    // Per species: a bit per known position of its rune sequence (SpeciesData.runes).
    public readonly Dictionary<SpeciesData, int> KnownRunes = new();

    // Fold `other` into this store: union the sets, and per lesson keep the larger percent.
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

        int speciesBefore = DiscoveredSpecies.Count;
        DiscoveredSpecies.UnionWith(other.DiscoveredSpecies);
        if (DiscoveredSpecies.Count > speciesBefore) { changed |= EKnowledgeCategory.Bestiary; }

        foreach (KeyValuePair<SpeciesData, int> kv in other.KnownRunes)
        {
            KnownRunes.TryGetValue(kv.Key, out int mine);
            if ((mine | kv.Value) != mine)
            {
                KnownRunes[kv.Key] = mine | kv.Value;
                changed |= EKnowledgeCategory.Rune;
            }
        }

        int namesBefore = KnownNames.Count;
        KnownNames.UnionWith(other.KnownNames);
        if (KnownNames.Count > namesBefore) { changed |= EKnowledgeCategory.Name; }
        foreach (KeyValuePair<LanguageData, Dictionary<string, float>> kv in other.LearnedLessons)
        {
            if (kv.Key == null)
            {
                continue;
            }
            foreach (KeyValuePair<string, float> lesson in kv.Value)
            {
                if (RecordLesson(kv.Key, lesson.Key, lesson.Value))
                {
                    changed |= EKnowledgeCategory.Language;
                }
            }
        }

        return changed;
    }

    // Inside a shared EntitySerializer table (SaveGame).
    public void Serialize(BinaryWriter w)
    {
        WriteSet(w, IdentifiedItems);
        WriteSet(w, DiscoveredRecipes);
        WriteSet(w, DiscoveredSpecies);
        WriteSet(w, KnownNames);
        w.Write(KnownRunes.Count);
        foreach (KeyValuePair<SpeciesData, int> kv in KnownRunes)
        {
            EntitySerializer.WriteRef(w, kv.Key);
            w.Write(kv.Value);
        }
        w.Write(LearnedLessons.Count);
        foreach (KeyValuePair<LanguageData, Dictionary<string, float>> kv in LearnedLessons)
        {
            EntitySerializer.WriteRef(w, kv.Key);
            w.Write(kv.Value.Count);
            foreach (KeyValuePair<string, float> lesson in kv.Value)
            {
                w.Write(lesson.Key);
                w.Write(lesson.Value);
            }
        }
    }

    // Adds to this store; a reference that no longer resolves is dropped.
    public void Deserialize(BinaryReader r)
    {
        ReadSet(r, IdentifiedItems);
        ReadSet(r, DiscoveredRecipes);
        ReadSet(r, DiscoveredSpecies);
        ReadSet(r, KnownNames);
        int runeSpecies = r.ReadInt32();
        for (int i = 0; i < runeSpecies; i++)
        {
            SpeciesData species = EntitySerializer.ReadRef<SpeciesData>(r);
            int mask = r.ReadInt32();
            if (species != null)
            {
                KnownRunes.TryGetValue(species, out int mine);
                KnownRunes[species] = mine | mask;
            }
        }
        int languages = r.ReadInt32();
        for (int i = 0; i < languages; i++)
        {
            LanguageData language = EntitySerializer.ReadRef<LanguageData>(r);
            int lessons = r.ReadInt32();
            for (int j = 0; j < lessons; j++)
            {
                string lesson = r.ReadString();
                float percent = r.ReadSingle();
                if (language != null)
                {
                    RecordLesson(language, lesson, percent);
                }
            }
        }
    }

    // Keeps the larger percent if the source is already held. True when that
    // changed anything.
    public bool RecordLesson(LanguageData language, string source, float percent)
    {
        if (!LearnedLessons.TryGetValue(language, out Dictionary<string, float> lessons))
        {
            lessons = new Dictionary<string, float>();
            LearnedLessons[language] = lessons;
        }
        if (lessons.TryGetValue(source, out float existing) && existing >= percent)
        {
            return false;
        }
        lessons[source] = percent;
        return true;
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
        DiscoveredSpecies.Clear();
        KnownNames.Clear();
        KnownRunes.Clear();
        LearnedLessons.Clear();
    }
}
