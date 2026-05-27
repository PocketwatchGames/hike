// Marks an entity whose colliders authored on the bare Environment layer (1)
// should be remapped to Porous when it spawns — so it blocks movement and
// grounded line-of-sight but lets smell, sound, perched vision, and flight
// pass through (trees, bushes, most props, and porous interactives like
// chests/signposts). Solid things (doors, boulders, buildings) return false.
//
// Applied to PropInstance and IInteractive so porousness is one shared concept
// across props and interactives; World applies the remap once at spawn via
// PorousColliders.Apply. Only layer-1 colliders are touched, so colliders
// deliberately authored on another layer keep it.
public interface IPorous
{
    bool Porous => true;
}
