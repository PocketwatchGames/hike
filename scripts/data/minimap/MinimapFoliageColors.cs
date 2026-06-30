using Godot;

// Color palette for foliage stamps on the minimap, keyed by foliage id. Both
// detail-scatter (DetailGroupData.MinimapFoliageId) and props
// (MultimeshPropSprite.MinimapFoliageId) write a foliage id into the material
// texture's G channel; this palette resolves it to a color + priority.
//
// Priority resolves overlap: when two foliage stamps fall in the same minimap
// pixel, only the one with priority >= the existing pixel's priority wins.
// Set trees high (3), bushes medium (2), grass low (1) so a tree pixel isn't
// overwritten by surrounding grass.
//
// Foliage id 0 is reserved for "no stamp" (terrain shows through), so palette
// indices 0..255 cover authored entries — entry 0 is unused.
[GlobalClass]
public partial class MinimapFoliageColors : Resource
{
    public const int Size = 256;

    [Export] public Godot.Collections.Array<MinimapFoliageEntry> entries = new();

    public MinimapFoliageEntry Get(int foliageId)
    {
        if (foliageId <= 0 || foliageId >= entries.Count)
        {
            return null;
        }
        return entries[foliageId];
    }
}
