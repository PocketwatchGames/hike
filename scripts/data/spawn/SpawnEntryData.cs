using System;
using Godot;

// Base class for one entity entry in either a SpawnGroupData (cluster around
// an anchor) or a SpawnListData (per-zone authored list). Subclass per entity
// type (MobSpawnEntry, ChestSpawnEntry, ...) and override Spawn to construct
// the matching EntitySimState and add it to the world.
//
// Two coexisting use modes consume the same base:
//   - Cluster (SpawnGroupData): for each sub-entry the caller invokes
//     RollCount(rng) to decide how many instances to scatter, and calls
//     Spawn on each. Sub-entries are not gated by SquareMetersPerSpawn —
//     the parent group already decided to fire.
//   - Per-column scan (SpawnListData on ZoneGenData): the caller rolls
//     RollAreaChance once per (wx, wz) candidate and calls Spawn once on hit.
//     Subclasses that need their own count parameter (chest loot count,
//     berry count) declare a purpose-named field and roll inside Spawn.
//
// SpawnAtNight is honored by mob and chest sim states — their nodes only
// MATERIALIZE after dark. Once spawned they persist through daytime until
// their chunk evicts; SpawnAtNight is a one-way spawn gate, not a presence
// gate. Subclasses that don't use a night-spawn concept (loot, fire trap,
// berry tree, plain torch) simply ignore it.
[GlobalClass]
public partial class SpawnEntryData : Resource
{
    // Average number of qualifying square meters between spawns from the
    // per-column scan (each grass / cave-pocket column is 1m²). Inverse of
    // a per-1m² probability: 1000 means "≈one spawn per 1000m² of eligible
    // terrain", 200 means dense placement. Authored as a friendly integer
    // range so the Godot editor's default spinbox step doesn't quietly
    // round sub-0.001 probabilities to zero.
    //
    // Default 0 disables the per-column scan for this entry — used by
    // SpawnGroupData sub-entries, which are gated by their parent's
    // RollCount rather than an independent area roll.
    [Export(PropertyHint.Range, "0,5000,1,or_greater")] public float SquareMetersPerSpawn;

    // Mob and chest sim states honour this by deferring node spawn until
    // their chunk activates after dark; the resulting entity then persists
    // across the day/night cycle. Other sim states ignore it.
    [Export] public bool SpawnAtNight;

    // Reject this entry's spawn position if any existing entity sits within
    // this radius. Prevents campfires inside trees, mobs inside chests, etc.
    // Set to 0 to disable the check (composite entries like SpawnGroupData
    // don't sit on a tile themselves — their anchor is just a scatter center,
    // so overlap at the group level is meaningless).
    [Export] public float MinSpacing = 0.5f;

    // True iff this entry requires a flat patch — the column and all 8
    // surrounding columns must share the same surface height. Subclasses
    // override to opt in; defaults to false so existing entry types
    // (loot, torches, fire traps, berry trees, ...) keep their current
    // placement domain. Mobs and campfires opt in to stop placements at
    // step edges and ramp adjacencies where physics can knock them off.
    public virtual bool RequireFlatTerrain => false;

    // True iff this entry needs air at the 4 lateral neighbors over a
    // 2-voxel body height. Catches mobs spawned against tunnel walls (the
    // cave-pocket pre-validation only checks the column itself, so a wall-
    // adjacent column passes — and a mob hitbox slightly wider than 0.5m
    // can clip in). Redundant with RequireFlatTerrain on the surface pass
    // (flat patch guarantees lateral air) — useful primarily inside caves.
    public virtual bool RequireLateralClearance => false;

    // Number of instances to scatter when this entry is a sub-entry of a
    // SpawnGroupData cluster. Default returns 1; subclasses that want
    // multi-instance scatter override (see MobSpawnEntry.ClusterCountMin/Max).
    public virtual int RollCount(Random rng)
    {
        return 1;
    }

    // Per-column area roll for SpawnListData scans. Returns true if the
    // caller should fire Spawn on this column. SquareMetersPerSpawn == 0
    // disables the entry from area scans (default for SpawnGroupData
    // sub-entries). Centralized so call sites stay readable — both the
    // surface and cave passes in WorldGen go through here.
    public bool RollAreaChance(Random rng)
    {
        if (SquareMetersPerSpawn <= 0f) { return false; }
        return rng.NextDouble() * SquareMetersPerSpawn < 1f;
    }

    // Run the entry-specific placement gates (flat-terrain check, overlap
    // check) and dispatch to Spawn on success. Returns false if the spot
    // was rejected — caller skips the instance. SpawnGroupData's scatter
    // path bypasses this wrapper because TryPickInRadius does the same
    // checks inside its rejection-sampling loop.
    public bool TrySpawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (RequireFlatTerrain && context?.IsFlatColumn != null)
        {
            int wx = Mathf.FloorToInt(position.X);
            int wz = Mathf.FloorToInt(position.Z);
            if (!context.IsFlatColumn(wx, wz))
            {
                return false;
            }
        }
        if (RequireLateralClearance && !HasLateralClearance(ws, position))
        {
            return false;
        }
        if (MinSpacing > 0f && ws.HasEntityWithinRadius(position, MinSpacing))
        {
            return false;
        }
        Spawn(ws, position, rng, context);
        return true;
    }

    // 4-connected air check over a 2-voxel body height around the spawn
    // anchor's voxel. Conservative — rejects 1-voxel-wide tunnels (mobs
    // would barely fit and be hard to navigate around anyway).
    private static bool HasLateralClearance(WorldState ws, Vector3 position)
    {
        int vx = Mathf.FloorToInt(position.X);
        int vy = Mathf.FloorToInt(position.Y);
        int vz = Mathf.FloorToInt(position.Z);
        const int BodyHeight = 2;
        for (int h = 0; h < BodyHeight; h++)
        {
            if (ws.GetVoxelWorld(vx + 1, vy + h, vz) != VoxelType.Air) { return false; }
            if (ws.GetVoxelWorld(vx - 1, vy + h, vz) != VoxelType.Air) { return false; }
            if (ws.GetVoxelWorld(vx, vy + h, vz + 1) != VoxelType.Air) { return false; }
            if (ws.GetVoxelWorld(vx, vy + h, vz - 1) != VoxelType.Air) { return false; }
        }
        return true;
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
