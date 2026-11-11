using System.Collections.Generic;

// The variant pools a subscene defines: every distinct marker pool in the
// file, with how many markers join it. Baked into the .hikescene header at save so
// a variant can be authored against a scene without decoding its voxel body —
// SubsceneFile.ReadDirectory pulls just this block off disk.
//
// Derived data, never authored: the scene's markers ARE the definition,
// and this is their summary. A pool listed here with count 5 is "5 positions
// available"; what a variant does with them is the variant's business.
public sealed class SubsceneDirectory
{
    public struct Entry
    {
        // Pool name, i.e. a marker's MarkerSpawnEntry.poolTag. Never empty — an
        // untagged marker belongs to no pool.
        public string Tag;
        // Entities in the scene carrying this tag: the ceiling on how many
        // positions a variant can pick from it.
        public int Count;
    }

    // Sorted by Tag, so the authoring UI has a stable order and two saves of an
    // unchanged scene produce identical bytes.
    public Entry[] Entries = System.Array.Empty<Entry>();

    public int CountOf(string tag)
    {
        foreach (Entry entry in Entries)
        {
            if (entry.Tag == tag)
            {
                return entry.Count;
            }
        }
        return 0;
    }

    // Only MARKERS make pools. A path hint's tag names a road endpoint, not a
    // spawn pool — listing it here would offer a variant a position it must
    // never fill. `subscene_info` reports hints off the entity list.
    public static SubsceneDirectory FromEntities(IReadOnlyList<SceneEntity> entities)
    {
        var counts = new Dictionary<string, int>();
        if (entities != null)
        {
            foreach (SceneEntity entity in entities)
            {
                string tag = (entity?.Placement.Entry as MarkerSpawnEntry)?.poolTag;
                if (string.IsNullOrEmpty(tag))
                {
                    continue;
                }
                counts.TryGetValue(tag, out int existing);
                counts[tag] = existing + 1;
            }
        }

        var tags = new List<string>(counts.Keys);
        tags.Sort(System.StringComparer.Ordinal);
        var directory = new SubsceneDirectory { Entries = new Entry[tags.Count] };
        for (int i = 0; i < tags.Count; i++)
        {
            directory.Entries[i] = new Entry { Tag = tags[i], Count = counts[tags[i]] };
        }
        return directory;
    }
}
