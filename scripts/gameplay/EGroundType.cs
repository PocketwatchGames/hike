// Logical ground category for footstep effects (sound + particle). Each
// BlockSurfaceData picks one of these via [Export], and GroundTypeResolver maps a
// world-space position to the BlockSurfaceData under the player's feet (overlay
// first, then the voxel's flat tile), so a single dictionary on Player/Mob
// keyed by this enum drives all footstep emission.
//
// Decoupled from int and from the kit set on purpose: many blocks map
// to the same ground category (e.g. DesertTop + DesertSand both -> Sand)
// and several blocks share Mud or Stone too. New entries should append to
// the end so existing serialized resources keep their numeric values.
public enum EGroundType
{
    Grass = 0,
    Stone = 1,
    Sand = 2,
    Water = 3,
    Mud = 4,
    Wood = 5,
    Dirt = 6,
    Snow = 7,
}
