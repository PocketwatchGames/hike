using System;

[Flags]
public enum ECollisionLayer
{
    Environment = 1,
    Player = 2,
    Interactive = 4,
    // "Parked" layer for bodies/areas that exist physically but are not
    // queried by any gameplay raycast/mask. Today: Loot's RigidBody (so it
    // falls and rests against Environment without being caught by combat
    // hurt-rays or LOS checks) and TallGrass's Area3D (rustle-on-overlap
    // trigger that detects via runtime mask, not via layer queries).
    // Trees, despite being "props," live on Environment because they
    // should block LOS and stop arrows. Rename from Prop in 2026-05.
    Passive = 8,
    Mob = 16,
    HurtBox = 32,
    Water = 64,
    Burrowed = 128,
    // Hurtbox of a burrowed mob — split from Burrowed so attack-scan tools
    // can pick up the targetable shape independently of the body's
    // movement-collision volume. Default attack masks use HurtBox only, so
    // anything on this layer is naturally hidden from regular weapons until
    // a tool opts in explicitly.
    BurrowedHurtBox = 256,
    // Corpse rigid body — moved here in Mob.Die(). Mask keeps Environment
    // so the body still rests on terrain and accepts knockback impulses,
    // while staying invisible to projectile sweeps and aim raycasts (both
    // mask Environment for body queries, not Dead). Live mobs no longer
    // collide with corpses as a side effect — fine, since pathing already
    // steers around the mob spatial hash. Future "loot corpse" or "drag
    // corpse" tools opt in by masking this bit explicitly.
    Dead = 512,
    // Hurtbox of a corpse — split from Dead so future tools that scan
    // corpses as targets (revive spell, butcher tool) can pick up the
    // targetable shape independently of the body's movement-collision
    // volume. Default attack masks use HurtBox only, so anything on this
    // layer is naturally hidden from regular weapons until a tool opts in.
    DeadHurtBox = 1024,
}
