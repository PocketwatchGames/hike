// Logical ground category for footstep effects (sound + particle). Each
// EnvironmentKitData picks one of these via [Export], and a small static
// resolver also maps the non-Terrain authored VoxelTypes (Stone, Water, ...)
// into the same space, so a single dictionary on Player/Mob keyed by this
// enum can drive all footstep emission.
//
// Decoupled from VoxelType and from the kit set on purpose: many kits map to
// the same ground category (e.g. desert + a future dune kit both -> Sand)
// and several VoxelTypes do too (Marsh + future Bog -> Mud). New entries
// should append to the end so existing serialized resources keep their
// numeric values.
public enum EGroundType
{
    Grass = 0,
    Stone = 1,
    Sand = 2,
    Water = 3,
    Mud = 4,
    Wood = 5,
}
