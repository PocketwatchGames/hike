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
}
