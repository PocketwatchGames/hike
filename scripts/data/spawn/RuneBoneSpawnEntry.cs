using System.Collections.Generic;
using Godot;

// A rune bone buried in the ground: which species and which of its runes is
// picked PER PLACEMENT in the painter's property panel; the entry file supplies
// only the bone item and the spot's look. Dug up, the bone teaches that one
// rune (RuneBoneData).
//
// `boneSpecies`, not `species`: a field named `species` is what a mob entry IS
// and is hidden from placements (SpawnEntryData.IsIdentityProperty), whereas
// here it is exactly what an author came to set.
[GlobalClass]
public partial class RuneBoneSpawnEntry : BuriedSpotSpawnEntry
{
    // The item buried — the one shared rune bone.
    [Export] public RuneBoneData bone;

    // Whose sequence the rune comes from. Offered only species that have one.
    [Export] public SpeciesData boneSpecies;

    // Which rune of that sequence, counted from 1.
    [Export(PropertyHint.Range, "1,5,1")] public int runeNumber = 1;

    private static readonly StringName[] Order =
    {
        PropertyName.boneSpecies, PropertyName.runeNumber, BuriedSpotSpawnEntry.PropertyName.style,
    };

    public override StringName[] PropertyOrder => Order;

    // A rune bone buries a bone and nothing else.
    public override bool ShowsProperty(StringName name)
    {
        return name != PropertyName.bone
            && name != BuriedSpotSpawnEntry.PropertyName.item
            && name != BuriedSpotSpawnEntry.PropertyName.count
            && name != BuriedSpotSpawnEntry.PropertyName.payload
            && base.ShowsProperty(name);
    }

    public override Resource[] ResourceCandidates(StringName property)
    {
        if (property != PropertyName.boneSpecies)
        {
            return base.ResourceCandidates(property);
        }
        var found = new List<Resource>();
        foreach (string path in ResourceTypeIndex.Candidates(typeof(SpeciesData), null))
        {
            if (ResourceLoader.Load(path) is SpeciesData species && species.RunesFlat.Length > 0)
            {
                found.Add(species);
            }
        }
        return found.ToArray();
    }

    // "rune_bone: goblin_forest 2".
    public override string VariantName()
    {
        if (boneSpecies == null || string.IsNullOrEmpty(boneSpecies.ResourcePath))
        {
            return null;
        }
        return $"{boneSpecies.ResourcePath.GetFile().GetBaseName()} {runeNumber}";
    }

    public override string MissingPlacementValue()
    {
        return boneSpecies == null ? "no species picked" : null;
    }

    public override string InvalidPlacementValue()
    {
        if (bone == null)
        {
            return "the entry names no bone item.";
        }
        if (boneSpecies == null)
        {
            return null;
        }
        int length = boneSpecies.RunesFlat.Length;
        if (runeNumber < 1 || runeNumber > length)
        {
            return $"rune {runeNumber} is outside {boneSpecies.ResourcePath.GetFile()}'s sequence of {length}.";
        }
        return null;
    }

    protected override void AppendContents(List<ItemState> into)
    {
        var state = (RuneBoneState)bone.CreateState();
        state.species = boneSpecies;
        state.position = runeNumber - 1;
        into.Add(state);
    }
}
