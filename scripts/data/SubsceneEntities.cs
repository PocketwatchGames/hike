using Godot;

// The entities a `.hikescene` is built from, in a `.tres` beside it
// (`house01.hikescene` → `house01.entities.tres`). A resource rather than a
// block in the binary because the placements reference entries by path and
// carry their forks inline, nested lists and records included — exactly what a
// `.tres` already saves faithfully and the binary's by-value ref table cannot.
// The scene file and this one are one document and move together.
[GlobalClass]
public partial class SubsceneEntities : Resource
{
    public const string FILE_SUFFIX = ".entities.tres";

    [Export] public SceneEntityPlacement[] placements = System.Array.Empty<SceneEntityPlacement>();

    // The next id to hand out. Kept rather than derived from the largest id in
    // use, because deleting the newest entity and adding another would then hand
    // its id straight back — and a stamp's override for the deleted door would
    // land on whatever replaced it.
    [Export] public int nextId = 1;

    public static string PathFor(string scenePath) => scenePath.GetBaseName() + FILE_SUFFIX;
}
