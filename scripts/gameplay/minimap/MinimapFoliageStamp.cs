using Godot;

// Lightweight Node3D that asks the minimap to drop one foliage stamp at this
// node's world position. Mirrors MultimeshPropSprite.MinimapFoliageId for
// props that don't go through the sprite path — primarily 3D-mesh props that
// still need to appear under the minimap's foliage layer.
[GlobalClass]
public partial class MinimapFoliageStamp : Node3D
{
    // Index into MinimapFoliageColors. 0 = no stamp.
    [Export] public byte MinimapFoliageId { get; set; } = 0;
}
