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
// spawnConditions is honored by mob and chest sim states — their nodes only
// MATERIALIZE while the required circumstances hold. Once spawned they persist
// until their chunk evicts; spawnConditions is a one-way spawn gate, not a
// presence gate. Subclasses that don't use spawn gating (loot, fire trap,
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
    [Export(PropertyHint.Range, "0,5000,1,or_greater")] public float squareMetersPerSpawn;

    // Required circumstances for this entry's node to materialize. Mob and
    // chest sim states honour these by deferring node spawn until their chunk
    // activates while the conditions hold (e.g. Night, or Day | Clear); the
    // resulting entity then persists across changing conditions. Other sim
    // states ignore it. None = spawn unconditionally.
    [Export, CompactFlags] public ESpawnConditions spawnConditions;

    // Reject this entry's spawn position if any existing entity sits within
    // this radius. Prevents campfires inside trees, mobs inside chests, etc.
    // Set to 0 to disable the check (composite entries like SpawnGroupData
    // don't sit on a tile themselves — their anchor is just a scatter center,
    // so overlap at the group level is meaningless).
    [Export] public float minSpacing = 0.5f;

    // When this entry is a sub-entry of a SpawnGroupData, place it directly on
    // the group's anchor (the cluster center) instead of scattering it within
    // ScatterRadius — the cluster's centerpiece (a home campfire, a well). The
    // entry's placement gates still run via TrySpawn. Ignored outside a group.
    [Export] public bool placeAtAnchor;

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

    // True iff this entry spawns a mob. Mob entries are kept out of hazard
    // danger zones at spawn time (see TrySpawn). Defaults false; MobSpawnEntry
    // overrides.
    public virtual bool IsMobEntry => false;

    // True iff this entry resolves its own final position from the anchor it's
    // handed, ignoring the calling pass's column-validity sampler. A
    // SpawnGroupData calls Spawn directly on the anchor for these (no scatter,
    // no grassy-column gate) — e.g. a boat that must ring-scan for water, which
    // the grassy surface sampler would otherwise reject. Default false.
    public virtual bool SelfPlaces => false;

    // Radius (meters) of the damaging danger zone this entry's entity projects
    // — set by hazard entries (fire trap, campfire, spike trap). 0 = harmless.
    // Drives both the spawn keep-out (mobs won't spawn within it, and the
    // hazard won't spawn onto an existing mob) and the runtime hazard grid
    // (wander/normal pathing routes around it). Authored as a per-type
    // [Export] on the hazard subclasses so it's designer-tunable.
    public virtual float HazardSpawnRadius => 0f;

    // Final standability gate, evaluated against the same navigation
    // walkability sampler the mob navigator uses at runtime — so an entity
    // only spawns where its profile could actually stand and path. Default
    // true (the voxel air-over-solid + flat/lateral gates suffice for static
    // props); MobSpawnEntry overrides to require a navgrid-walkable column.
    // Runs at worldgen with no World node, so path-blocker cells aren't
    // consulted here (entity overlap is already covered by MinSpacing).
    public virtual bool IsSpawnPositionWalkable(WorldState ws, Vector3 position) => true;

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
        if (squareMetersPerSpawn <= 0f) { return false; }
        return rng.NextDouble() * squareMetersPerSpawn < 1f;
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
        if (minSpacing > 0f && ws.HasEntityWithinRadius(position, minSpacing))
        {
            return false;
        }
        // Hazard keep-out, enforced symmetrically so it's order-independent: a
        // mob never spawns inside a hazard's danger zone, and a hazard never
        // drops onto an already-placed mob. Either way the two end up at least
        // the hazard's radius apart. (Runtime attack pathing still lures mobs
        // in — this gate is spawn-time only.)
        if (IsMobEntry && ws.HasHazardSpawnConflict(position))
        {
            return false;
        }
        if (HazardSpawnRadius > 0f && ws.HasMobWithinRadius(position, HazardSpawnRadius))
        {
            return false;
        }
        if (!IsSpawnPositionWalkable(ws, position))
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
