using Godot;

// The roof materials the editor's Roofs palette offers, in button order.
// Separate from PropLibraryData because a roof isn't a scene to stamp — it has
// no authored mesh at all, only a surface the generator skins its geometry with.
[GlobalClass]
public partial class RoofLibraryData : Resource
{
    [Export] public RoofStyleData[] styles = System.Array.Empty<RoofStyleData>();
}
