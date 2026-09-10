using Godot;

// One stroke that writes the BROAD-BRUSH per-column layers together — "boreal
// forest" sets the ground and the wildlife at once.
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
// cover only one layer — a "meadow" that changes the wildlife without
// disturbing the ground under it.
//
// PROPS ARE NOT ONE OF THEM, and that is the whole reason the list is short. A
// painted prop region is a BARRIER and a no-spawn region over every column of
// it (see PropPaintTool), so a preset that wrote the prop layer would wall off
// and sterilize the entire area of every stroke — "paint a forest" would mean
// "make this impassable". Where a wood actually stops the player is a local
// decision taken a stroke at a time, not a property of the biome. The preset
// covers the two layers that genuinely are broad: what the ground is made of,
// and what lives on it.
[GlobalClass]
public partial class PaintPresetData : Resource
{
    [Export] public string displayName = "";

    [Export] public TerrainKitData ground;
    [Export] public SpawnScatterData mobs;

    // The palette swatch, and the brush ring: the GROUND this preset lays down,
    // because that is what the map will show where the stroke lands. A preset
    // carried its own mapColor once, which was a third colour authored beside
    // the ground set's and the scatter set's and able to disagree with both —
    // a swatch that is not a preview of the stroke is decoration.
    public Color SwatchColor => ground?.mapColor ?? new Color(0.6f, 0.6f, 0.6f);

    public string Label => string.IsNullOrEmpty(displayName)
        ? (string.IsNullOrEmpty(ResourcePath) ? "Preset" : ResourcePath.GetFile().GetBaseName())
        : displayName;
}
