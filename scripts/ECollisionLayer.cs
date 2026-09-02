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
    // hurt-rays or LOS checks) and Foliage's Area3D (rustle-on-overlap
    // trigger that detects via runtime mask, not via layer queries).
    // Props (trees, etc.) live on Porous, not here — see that layer.
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
    // Porous props (trees and most prop colliders). Distinct from Environment
    // so a prop blocks movement and grounded line-of-sight (queries mask Solid)
    // while letting smell, sound, perched vision, and flight pass straight
    // through (those mask Environment alone). Colliders authored as a PorousBody
    // node sit here; terrain/walls stay on Environment so they stay solid to
    // everything.
    Porous = 2048,
    // Loose world debris — settled Loot. Included by the VOLUME attack queries
    // (the melee sweep and the AoE burst, both of which collect every overlap)
    // so blasts and swings scatter dropped items. Deliberately NOT included by
    // the first-hit queries — a hitscan ray, a projectile sweep or a
    // chain-lightning hop must not pick a dropped berry over the goblin behind
    // it — nor by AimingReticle, so loot never steals the reticle from a mob.
    // Separate from HurtBox for exactly that reason; don't fold it in.
    Debris = 4096,
    // The invisible walls and floor that box the streamed world in
    // (WorldBoundary). Its OWN layer, not Environment, because it is felt and
    // never seen: it exists to stop bodies leaving the world, and nothing that
    // asks a question ABOUT the world should find it. On Environment it read as
    // real cover to every sight query, so standing near the map edge in open
    // desert reported the player hidden behind a wall that is not there — and
    // every future query would have had to remember to exclude it.
    WorldBounds = 8192,
    // Invisible barriers standing at the top edge of every drop taller than a
    // legal step, generated per chunk alongside terrain collision. Its OWN
    // layer, and deliberately outside Solid and Blocking, for the same reason
    // WorldBounds is: it is felt and never seen, so nothing that asks a question
    // ABOUT the world — sight, aim, pathing, projectiles — may find it. Only a
    // body that has explicitly opted in collides with it, so the barriers can be
    // switched on for the player without becoming cover, walls, or obstacles for
    // everything else.
    // One bit per traversal CLASS, keyed by the deepest drop that class of body
    // accepts (see LedgeBarrierClasses). A body masks in the bit matching its
    // own maxFallHeight and is then physically unable to walk off anything
    // deeper — the player at 1, a goblin at 2, an ordinary mob at 4. One mesh
    // could not serve all three: it can only be cut at one threshold, and a
    // threshold too strict wedges a body at a drop its own router chose.
    LedgeBarrierFall1 = 16384,
    LedgeBarrierFall2 = 32768,
    LedgeBarrierFall4 = 65536,
    // Every barrier, for the queries that must ignore all of them — "does the
    // body fit here" is a question ABOUT the world, and a barrier is not part
    // of the world.
    LedgeBarrier = LedgeBarrierFall1 | LedgeBarrierFall2 | LedgeBarrierFall4,
    // Convenience combo: "solid to the world" — terrain/walls plus porous
    // props. World raycasts (vision, arrows, aim, pathing, rain, lightning) mask
    // this so props still block them; the few queries that should see / smell /
    // fly through props mask Environment alone. Deliberately WITHOUT WorldBounds,
    // so no query can see the boundary.
    Solid = Environment | Porous,
    // What a moving BODY must not pass through — the world's solids plus the
    // boundary. Every CollisionMask that exists to contain something masks this;
    // masking Solid instead lets the thing leave the world.
    Blocking = Solid | WorldBounds,
}
