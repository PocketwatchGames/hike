using Godot;

// One stroke that writes the per-column layers together — "boreal forest" sets
// the ground, the props and the wildlife at once.
//
// Zone is deliberately NOT one of them. It is chunk resolution while these are
// per column, so a preset stroke narrower than 16m would still flip a whole
// chunk's weather and sky; and a zone covers ground of many kinds, so tying the
// two means you cannot repaint one without disturbing the other.
//
// This is what keeps the decomposition from costing four times the work. Split
// into independent layers, every ordinary stroke would need repeating per layer
// and staying consistent by hand across the whole map; the preset restores the
// one-stroke common case while leaving each layer independently repaintable
// afterwards. Composite for speed, layers for control.
//
// A null slot means "leave that layer alone", so a preset can deliberately
// cover only some layers — e.g. a "clearing" that changes props without
// disturbing the ground under them.
[GlobalClass]
public partial class PaintPresetData : Resource
{
    [Export] public string displayName = "";
    [Export] public Color mapColor = new Color(0.6f, 0.6f, 0.6f);

    [Export] public GroundSetData ground;
    [Export] public PropListData collidableProps;
    [Export] public PropListData destructibleProps;
    [Export] public SpawnSetData mobs;

    // Density written into the mob layer, a fraction of that set's own authored
    // rate. The prop layers have no equivalent: their placement is direct, so
    // painting one is not a rate to scale.
    [Export(PropertyHint.Range, "0,1,0.01")] public float mobDensity = 1f;

    public string Label => string.IsNullOrEmpty(displayName)
        ? (string.IsNullOrEmpty(ResourcePath) ? "Preset" : ResourcePath.GetFile().GetBaseName())
        : displayName;
}
