using System.Collections.Generic;
using Godot;

// Static helpers that produce world-space goal points for behaviors to
// hand to MobNavigator.Goto. Separated from MobNavigator so behaviors
// can mix goal kinds (standoff, cover, retreat) without bloating the
// navigator's API surface.
//
// ── Finding standable ground: which tool to use ──────────────────────────────
// TWO families of "where can a body stand" query live in the codebase; pick by
// whether it must work indoors/underground:
//
//  • NAV-GRID (this file: IsGroundStandable single-point, CollectStandableCells
//    bulk-by-radius, CollectReachableStandableCells bulk-by-CONNECTIVITY;
//    WalkabilityGrid.SampleColumn per-column). CPU voxel scan, multi-surface-per-
//    column, so it finds a cave/tunnel FLOOR at the query's Y — not the terrain
//    overhead. No physics. Use for ANY gameplay spawn/placement, especially
//    underground. Prefer the *Reachable* variant when the player may be in a cave
//    and unreachable-but-nearby surfaces (the ground above the tunnel) must be
//    excluded — it floods from the player's cell so only walk-connected ground
//    qualifies. Worldgen mob placement (MobSpawnEntry.IsSpawnPositionWalkable) and
//    the night mob spawner (NightMobSpawner) both use this family.
//
//  • RAYCAST-FROM-ABOVE (private TryFindGround in WeatherLightningSpawner /
//    LightningStrike, TryFindSurface in WindParticleManager — NOT centralized,
//    each rolls its own downward IntersectRay). A ray from the sky hits the
//    FIRST surface from above = the outdoor terrain top, which is WRONG under a
//    roof/cave (it catches the mountain over the tunnel). Only acceptable for
//    open-sky-only weather/particle effects. Do NOT copy it for mob spawning.
public static class NavigationGoals
{
    private const float StandoffEyeHeight = 1.5f;

    // Resolve a slot on a target's encircle ring to a concrete world
    // position the mob should stand at. Tries the slot's exact angle
    // first; if that point isn't reachable (LOS blocked, off the
    // streamed window, sitting on a wall) sweeps ±N degrees in
    // increasing offsets until it finds a reachable candidate or
    // exhausts attempts. Falls back to the nominal point on failure —
    // the navigator's A* layer will fail-soft from there.
    //
    // requireLineOfSight=true matches the typical "ranged mob holds
    // position with a clear shot" use case. Pass false for melee
    // standoff where line-of-sight isn't required at the slot itself.
    public static Vector3 PickStandoffPoint(
        Sim sim,
        in TraversalProfile profile,
        Vector3 targetPos,
        float distance,
        float slotAngle,
        bool requireLineOfSight = true,
        int sweepAttempts = 5,
        float sweepStepDegrees = 22.5f)
    {
        if (sim == null || distance <= 0f)
        {
            return targetPos;
        }
        WorldState ws = sim.WorldState;
        if (ws == null)
        {
            return targetPos;
        }

        // Sweep order: 0, +1, -1, +2, -2, ... so we try the requested
        // angle first then alternate on either side at increasing offsets.
        // First reachable candidate wins.
        float stepRad = Mathf.DegToRad(sweepStepDegrees);
        for (int attempt = 0; attempt <= sweepAttempts * 2; attempt++)
        {
            int absOffset = (attempt + 1) / 2;
            int sign = (attempt == 0) ? 0 : ((attempt % 2 == 1) ? 1 : -1);
            int offsetSlots = absOffset * sign;
            float angle = slotAngle + offsetSlots * stepRad;
            Vector3 candidate = targetPos + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * distance;

            if (!IsStandable(ws, sim, profile, candidate, out Vector3 surfacePoint))
            {
                continue;
            }
            // Reject slots at a cliff edge — the cell directly behind the
            // slot (away from the target) must also be standable at roughly
            // the same Y. A player shove sends the mob backwards along this
            // axis, so without this check the encircle ring places mobs
            // exactly where they can be punted off a ledge.
            if (!HasStableBacking(ws, sim, profile, surfacePoint, targetPos))
            {
                continue;
            }
            if (requireLineOfSight && !HasLineOfSight(sim, surfacePoint, targetPos))
            {
                continue;
            }
            return surfacePoint;
        }

        // No candidate worked — return the nominal position. The caller's
        // navigator will route as close as it can and may end up flagged
        // blocked, which BehaviorAttack can act on.
        return targetPos + new Vector3(Mathf.Sin(slotAngle), 0f, Mathf.Cos(slotAngle)) * distance;
    }

    // Public wrapper: true if `worldPos` sits over a standable surface for a mob
    // with `profile`, snapping `surfacePoint` to that surface. Used by the dodge
    // to confirm a sideways/back dash lands on valid ground (not a cliff edge or
    // wall) before committing. Fetches WorldState off `world`; false when either
    // is missing.
    public static bool IsGroundStandable(Sim sim, in TraversalProfile profile, Vector3 worldPos, out Vector3 surfacePoint)
    {
        surfacePoint = worldPos;
        WorldState ws = sim?.WorldState;
        if (ws == null)
        {
            return false;
        }
        return IsStandable(ws, sim, profile, worldPos, out surfacePoint);
    }

    // Bulk spawn-placement query: collect every DRY standable surface point in the
    // [minRadius, maxRadius] ring around `center`, at roughly the center's height
    // (surface within maxSurfaceYDelta voxels of center.Y), for a body with
    // `profile`. Fills `results` with world-space surface points; the caller
    // applies any further gate (light, spacing) and picks among them.
    //
    // Enumerates every column in a nav window (sized to maxRadius, bounded by
    // maxWindowHalfExtent) and binds each to the layer NEAREST center.Y via
    // NearestLayer — so a tunnel resolves to its floor, and confined spaces are
    // found exhaustively rather than by luck (random darts almost never hit a
    // narrow tunnel). Water cells are skipped (land spawns). `grid` is caller-
    // owned and reused across calls to avoid per-call allocation. See the class
    // header for why this, and not a downward raycast, is the spawn-placement tool.
    public static void CollectStandableCells(Sim sim, in TraversalProfile profile, WalkabilityGrid grid,
        Vector3 center, float minRadius, float maxRadius, float maxSurfaceYDelta, int maxWindowHalfExtent,
        List<Vector3> results)
    {
        results.Clear();
        WorldState ws = sim?.WorldState;
        if (ws == null || grid == null)
        {
            return;
        }
        float minR = Mathf.Max(0f, minRadius);
        float maxR = Mathf.Max(minR, maxRadius);
        int half = Mathf.Min(Mathf.Max(1, maxWindowHalfExtent), Mathf.CeilToInt(maxR));
        int size = half * 2 + 1;
        grid.Sample(ws, sim, profile,
            Mathf.FloorToInt(center.X), Mathf.FloorToInt(center.Y), Mathf.FloorToInt(center.Z), size);

        float minR2 = minR * minR;
        float maxR2 = maxR * maxR;
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
            {
                int layer = grid.NearestLayer(i, j, center.Y);
                if (layer < 0)
                {
                    continue;
                }
                WalkabilityCell c = grid.GetLayer(i, j, layer);
                if (c.IsWater || Mathf.Abs(c.surfaceY - center.Y) > maxSurfaceYDelta)
                {
                    continue;
                }
                Vector3 pos = grid.CellToWorld(i, j, layer);
                float dx = pos.X - center.X;
                float dz = pos.Z - center.Z;
                float d2 = dx * dx + dz * dz;
                if (d2 >= minR2 && d2 <= maxR2)
                {
                    results.Add(pos);
                }
            }
        }
    }

    // Scratch for CollectReachableStandableCells' flood: generation-tagged visited
    // (avoids clearing thousands of cells per call) + a reused BFS queue. Static —
    // navigation runs on the main thread only.
    private static int[] _reachGen;
    private static int _reachGeneration;
    private static readonly Queue<int> _reachQueue = new();

    // CONNECTIVITY variant of CollectStandableCells: collect only the standable
    // spots a body can actually WALK to from `center`, by flooding the nav grid
    // from the center's cell under the same step/fall rules the pathfinder uses.
    // So a surface separated from the player by solid rock — the ground directly
    // above a cave/tunnel — is excluded even though it's close in XZ, and only the
    // connected floor the player is on (and reachable ledges) qualifies. Results
    // are the reachable cells within [minRadius, maxRadius] of center (water
    // skipped). Use this for spawn placement that must behave in caves/dungeons;
    // the plain radius CollectStandableCells only suits open ground where
    // everything at the query height is one connected surface. Cardinal 4-
    // connectivity (enough to decide reachability of a contiguous floor).
    public static void CollectReachableStandableCells(Sim sim, in TraversalProfile profile, WalkabilityGrid grid,
        Vector3 center, float minRadius, float maxRadius, int maxWindowHalfExtent, bool allowFalling, List<Vector3> results)
    {
        results.Clear();
        WorldState ws = sim?.WorldState;
        if (ws == null || grid == null)
        {
            return;
        }
        float minR = Mathf.Max(0f, minRadius);
        float maxR = Mathf.Max(minR, maxRadius);
        int half = Mathf.Min(Mathf.Max(1, maxWindowHalfExtent), Mathf.CeilToInt(maxR));
        int size = half * 2 + 1;
        int cx = Mathf.FloorToInt(center.X);
        int cz = Mathf.FloorToInt(center.Z);
        grid.Sample(ws, sim, profile, cx, Mathf.FloorToInt(center.Y), cz, size);

        int layers = WalkabilityGrid.MaxColumnLayers;
        int startI = cx - grid.OriginX;
        int startJ = cz - grid.OriginZ;
        if (startI < 0 || startI >= size || startJ < 0 || startJ >= size)
        {
            return;
        }
        int startLayer = grid.NearestLayer(startI, startJ, center.Y);
        if (startLayer < 0)
        {
            return;
        }

        int total = size * size * layers;
        if (_reachGen == null || _reachGen.Length < total)
        {
            _reachGen = new int[total];
        }
        _reachGeneration++;
        _reachQueue.Clear();

        int startIdx = (startJ * size + startI) * layers + startLayer;
        _reachGen[startIdx] = _reachGeneration;
        _reachQueue.Enqueue(startIdx);

        float minR2 = minR * minR;
        float maxR2 = maxR * maxR;
        while (_reachQueue.Count > 0)
        {
            int cur = _reachQueue.Dequeue();
            int cLayer = cur % layers;
            int c2d = cur / layers;
            int ci = c2d % size;
            int cj = c2d / size;
            WalkabilityCell cc = grid.GetLayer(ci, cj, cLayer);

            if (!cc.IsWater)
            {
                Vector3 pos = grid.CellToWorld(ci, cj, cLayer);
                float dx = pos.X - center.X;
                float dz = pos.Z - center.Z;
                float d2 = dx * dx + dz * dz;
                if (d2 >= minR2 && d2 <= maxR2)
                {
                    results.Add(pos);
                }
            }

            // Cardinal neighbours; connect to every stacked surface the vertical
            // rules allow (step up onto a ledge, down onto the cave floor).
            for (int dir = 0; dir < 4; dir++)
            {
                int ni = ci + (dir == 0 ? 1 : (dir == 1 ? -1 : 0));
                int nj = cj + (dir == 2 ? 1 : (dir == 3 ? -1 : 0));
                if (ni < 0 || ni >= size || nj < 0 || nj >= size)
                {
                    continue;
                }
                int nLayers = grid.LayerCount(ni, nj);
                for (int nLayer = 0; nLayer < nLayers; nLayer++)
                {
                    int nIdx = (nj * size + ni) * layers + nLayer;
                    if (_reachGen[nIdx] == _reachGeneration)
                    {
                        continue;
                    }
                    if (!StepAllowed(profile, cc, grid.GetLayer(ni, nj, nLayer), allowFalling))
                    {
                        continue;
                    }
                    _reachGen[nIdx] = _reachGeneration;
                    _reachQueue.Enqueue(nIdx);
                }
            }
        }
    }

    // Cardinal step check between two stacked surfaces — mirrors LocalPathfinder's
    // vertical rules: climbers/fliers move freely; otherwise up-step <= maxStep,
    // down-step <= maxStep (or <= maxFall when falling is allowed), water gated by
    // maxFall.
    private static bool StepAllowed(in TraversalProfile profile, WalkabilityCell from, WalkabilityCell to, bool allowFalling)
    {
        if (profile.CanClimb || profile.CanFly)
        {
            return true;
        }
        int dy = to.surfaceY - from.surfaceY;
        if (to.IsWater)
        {
            return !(dy < 0 && -dy > profile.maxFallHeight);
        }
        if (dy > profile.maxStepHeight)
        {
            return false;
        }
        if (dy < 0)
        {
            int drop = -dy;
            if (drop > profile.maxStepHeight)
            {
                if (!allowFalling || drop > profile.maxFallHeight)
                {
                    return false;
                }
            }
        }
        return true;
    }

    // True if `worldPos` sits over a column with a standable surface
    // within a couple voxels of the requested Y. Snaps the returned
    // `surfacePoint` to that surface so callers can hand the navigator a
    // point that's actually on the ground rather than mid-air.
    private static bool IsStandable(WorldState ws, Sim sim, in TraversalProfile profile, Vector3 worldPos, out Vector3 surfacePoint)
    {
        surfacePoint = worldPos;
        int verticalClearance = profile.verticalClearance;
        int wx = Mathf.FloorToInt(worldPos.X);
        int wz = Mathf.FloorToInt(worldPos.Z);
        int anchorY = Mathf.FloorToInt(worldPos.Y);
        // Search ±SurfaceSearchRadius around the anchor Y for a standable
        // air-or-water voxel over solid. Mirrors WalkabilityGrid.SampleColumn's
        // contract; a land mob (avoidsDeepWater) additionally rejects a
        // swim-depth water column so its standoff slot never lands in deep
        // water (it may still wade in the shallows).
        for (int dy = WalkabilityGrid.SurfaceSearchRadius; dy >= -WalkabilityGrid.SurfaceSearchRadius; dy--)
        {
            int wy = anchorY + dy;
            if (!ws.IsInBounds(wx, wy, wz))
            {
                return false;
            }
            int here = ws.GetBlockWorld(wx, wy, wz);
            if (Blocks.IsSolid(here))
            {
                continue;
            }
            if (!ws.IsInBounds(wx, wy - 1, wz))
            {
                return false;
            }
            if (!Blocks.IsSolid(ws.GetBlockWorld(wx, wy - 1, wz)))
            {
                continue;
            }
            // Headroom check so the standoff point is in a slot the mob
            // actually fits into — verticalClearance voxels above the floor,
            // matching WalkabilityGrid.SampleColumn (vc=1 needs none).
            bool headroomBlocked = false;
            for (int h = 1; h < verticalClearance; h++)
            {
                if (!ws.IsInBounds(wx, wy + h, wz) || Blocks.IsSolid(ws.GetBlockWorld(wx, wy + h, wz)))
                {
                    headroomBlocked = true;
                    break;
                }
            }
            if (headroomBlocked)
            {
                continue;
            }
            // A land mob won't stand at the bottom of a deep water body. The
            // accepted voxel is Water only when the whole column is water over
            // a solid floor; reject it once that column reaches swim depth.
            if (Blocks.IsWater(here) && profile.AvoidsDeepWater)
            {
                int depth = 1;
                while (ws.IsInBounds(wx, wy + depth, wz) && Blocks.IsWater(ws.GetBlockWorld(wx, wy + depth, wz)))
                {
                    depth++;
                }
                if (depth >= Mathf.Max(1, Mathf.FloorToInt(profile.swimDepthThreshold)))
                {
                    return false;
                }
            }
            // Mirror WalkabilityGrid's entity-blocker rejection so standoff
            // slots can't land on a cell occupied by a tree or chest.
            if (sim != null)
            {
                bool entityBlocked = false;
                for (int h = 0; h < verticalClearance; h++)
                {
                    if (sim.IsPathBlocked(wx, wy + h, wz))
                    {
                        entityBlocked = true;
                        break;
                    }
                }
                if (entityBlocked)
                {
                    continue;
                }
            }
            surfacePoint = new Vector3(wx + 0.5f, wy, wz + 0.5f);
            return true;
        }
        return false;
    }

    // True if the cell one voxel behind `slotSurface` (away from `targetPos`)
    // is also a standable surface within ±1 voxel of slotSurface.Y. Used to
    // reject ring slots that sit at the literal edge of a cliff — the
    // mob arrives, idles, and the player walks into it and shoves it over.
    private static bool HasStableBacking(WorldState ws, Sim sim, in TraversalProfile profile, Vector3 slotSurface, Vector3 targetPos)
    {
        Vector3 awayXZ = new Vector3(slotSurface.X - targetPos.X, 0f, slotSurface.Z - targetPos.Z);
        float len = awayXZ.Length();
        if (len < 0.0001f)
        {
            return true;
        }
        Vector3 backCell = slotSurface + (awayXZ / len);
        if (!IsStandable(ws, sim, profile, backCell, out Vector3 backSurface))
        {
            return false;
        }
        // Same-plateau check: if the back cell's surface is more than one
        // voxel below the slot, the slot is on an overhang / step down,
        // not stable ground.
        return Mathf.Abs(backSurface.Y - slotSurface.Y) <= 1.001f;
    }

    // Environment-only line-of-sight raycast at eye height. Mirrors the
    // LOS check in BehaviorInvestigate / UpdatePerception so all three
    // systems agree on what counts as visible.
    private static bool HasLineOfSight(Sim sim, Vector3 from, Vector3 to)
    {
        Vector3 a = from + new Vector3(0f, StandoffEyeHeight, 0f);
        Vector3 b = to + new Vector3(0f, StandoffEyeHeight, 0f);
        using var query = PhysicsRayQueryParameters3D.Create(a, b, (uint)ECollisionLayer.Solid);
        query.CollideWithAreas = false;
        query.CollideWithBodies = true;
        var result = sim.GetWorld3D().DirectSpaceState.IntersectRay(query);
        return result.Count == 0;
    }
}
