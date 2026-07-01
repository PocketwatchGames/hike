using System;

// Locomotion capabilities for a mob, consolidated from the former per-field
// bools on MobData. Authored as a single flags field (MobData.movement);
// MobData exposes a derived bool accessor per flag so gameplay/nav reads stay
// readable (md.CanFly, md.CanTraverseLand, ...).
// Wire values are stable — append new bits, never reassign existing ones, so
// existing mob .tres keep loading.
[Flags]
public enum EMovementFlags
{
    None = 0,
    // Can enter Water voxels at all. Clear it for a creature that treats water
    // as a hard wall (e.g. a small flightless critter); set, water is enterable
    // but priced per cell (waterCost / swimCost).
    CanSwim = 1 << 0,
    // Wades shallow water but treats a swim-depth column (>= swimDepthThreshold)
    // as a wall: the pathfinder won't route it into deep water (including attack
    // / encircle slots) and BehaviorAttack won't let it attack while swimming.
    // It only swims when knocked in, making for the shallows. Aquatic mobs
    // ignore this — read everywhere via MobData.AvoidsDeepWater, never the raw
    // flag.
    AvoidsDeepWater = 1 << 1,
    // Flies: steering applies a hover force toward terrain + hoverHeight rather
    // than ground locomotion. Routing still uses the layered 2.5D grid, but the
    // A* vertical step/fall caps are lifted (a flier moves freely between stacked
    // surfaces, like a climber) so its route can dive into and out of caves.
    CanFly = 1 << 2,
    // Climbs arbitrary vertical surfaces (spider): skips the maxStepHeight
    // check, so any adjacent solid is walkable.
    CanClimb = 1 << 3,
    // Can stand and move on dry land (the default). Clear it for a creature
    // bound to water — a fish/eel that can't walk ashore: only water cells stay
    // walkable, ground locomotion is suppressed unless swimming, and
    // AvoidsDeepWater is forced off (deep water is its home).
    CanTraverseLand = 1 << 4,
    // Lives submerged rather than bobbing at the surface: ApplyWaterPhysics
    // holds it at submergedDepth (and pushes it back down if it breaches)
    // instead of floating it up, and it never hauls out onto a bank. Pairs with
    // a water-bound mob (CanTraverseLand cleared) for an underwater ambusher.
    SubmergedWhileSwimming = 1 << 5,
    // For a flier (CanFly): keep a solid collision mask while airborne instead
    // of passing through world geometry. The flier slides along walls and its
    // nav route (relaxed to move freely between vertical layers) carries it into
    // and out of caves. Clear it for cheap pass-through fliers (sparrows) that
    // never chase into tight spaces. No effect without CanFly.
    FliesSolid = 1 << 6,
}
