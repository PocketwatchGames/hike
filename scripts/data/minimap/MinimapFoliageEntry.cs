using Godot;

// One foliage palette entry. Authored as a sub-resource on
// MinimapFoliageColors.Entries; referenced by foliage id (the array index).
[GlobalClass]
public partial class MinimapFoliageEntry : Resource
{
    [Export] public string name = "";
    [Export] public Color color = new Color(0f, 1f, 0f);
    // Higher beats lower when stamps overlap in the same pixel.
    [Export(PropertyHint.Range, "0,255,1")] public int priority = 1;
}
