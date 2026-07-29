using Godot;

// The authored catalog of props that can be placed by hand.
//
// Distinct from a TerrainKitData's treeScenes / tallGrassScenes, which are
// worldgen's weighted scatter palettes — those exist to be ROLLED, they're
// scoped to whichever kits the loaded WorldGenData happens to reference, and
// nothing about them survives into a .hike. This is the authoring catalog: the
// full set an author may place deliberately, independent of what procedural
// generation would pick. The two overlap today but are free to diverge, which
// is the point — a hand-authored world can use props no kit scatters.
[GlobalClass]
public partial class PropLibraryData : Resource
{
    [Export] public PropLibraryEntry[] entries = System.Array.Empty<PropLibraryEntry>();
}
