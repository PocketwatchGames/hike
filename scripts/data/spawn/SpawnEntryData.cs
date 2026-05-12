using System;
using Godot;

// Base class for one entity entry in either a SpawnGroupData (cluster around
// an anchor) or a SpawnListData (per-zone authored list). Subclass per entity
// type (MobSpawnEntry, ChestSpawnEntry, ...) and override Spawn to construct
// the matching EntitySimState and add it to the world.
//
// Two coexisting use modes consume the same base:
//   - Cluster (SpawnGroupData): for each sub-entry the caller invokes
//     RollCount(rng) to decide how many instances to scatter, then rolls
//     Chance per instance and calls Spawn on each survivor.
//   - Per-column scan (SpawnListData on ZoneGenData): the caller rolls Chance
//     once per (wx, wz) candidate and calls Spawn once on hit. Subclasses
//     that need their own count parameter (chest loot count, berry count)
//     declare a purpose-named field and roll inside Spawn.
//
// SpawnAtNight is honored by mob and chest sim states (their nodes only spawn
// after dark when set). Subclasses that don't use a night-spawn concept
// (loot, fire trap, berry tree, plain torch) simply ignore it.
[GlobalClass]
public partial class SpawnEntryData : Resource
{
    // Per-(roll) probability used by per-column scanners (SpawnListData) and
    // by SpawnGroupData per spawned instance. Default 1.0 = always spawn.
    [Export] public float Chance = 1f;

    // Mob and chest sim states honour this by deferring node spawn until
    // their chunk activates after dark. Other sim states ignore it.
    [Export] public bool SpawnAtNight;

    // Number of instances to scatter when this entry is a sub-entry of a
    // SpawnGroupData cluster. Default returns 1; subclasses that want
    // multi-instance scatter override (see MobSpawnEntry.ClusterCountMin/Max).
    public virtual int RollCount(Random rng)
    {
        return 1;
    }

    // `position` is the GROUND TOP (top face of the solid voxel below the
    // entity), unified across both the surface and cave passes so subclasses
    // are pass-agnostic. Subclasses consume it as-is — every entity sits
    // with its scene root on this anchor, so the scene itself is the right
    // place to author any internal Y offset (a campfire bowl raised slightly
    // off the floor, a sprite stem lifted to avoid z-fighting, etc.). No
    // per-entry spawn-time lift; doing one here adds an in-air drop on
    // first physics tick, which can tunnel mobs through the floor when the
    // chunk's trimesh collider isn't registered yet.
    //
    // SpawnContext lets composite entries (SpawnGroupData) scatter sub-
    // entries within the placement domain of the calling pass. Leaf entries
    // (MobSpawnEntry, LootSpawnEntry, ...) ignore it. May be null when the
    // caller has no scatter sampler to provide (e.g. cave-pocket pass —
    // cells are pre-validated, no rejection needed).
    public virtual void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        GD.PushError($"SpawnEntryData subclass '{GetType().Name}' did not override Spawn");
    }
}
