using System;
using System.Collections.Generic;
using Godot;

// Per-mob navigation controller. Owns the mob's current movement intent
// (goto a point / wander around a center / stop), the path it's currently
// following, and the per-frame steering output. Behaviors should set intent
// through Goto/Wander/Stop and then call WriteSteering(...) once per
// _PhysicsProcess to populate AIOutput.pathTarget.
//
// Architecture: each repath samples a local WalkabilityGrid centered on the
// mob, runs A* to the current goal (or to the projected boundary cell if
// the goal lies outside the grid), and stores the resulting waypoint list.
// WriteSteering pops waypoints as the mob arrives at each. Wander picks a
// goal via brownian sampling on the same grid.
public class MobNavigator
{
    public enum State
    {
        Idle,
        Goto,
        Wander,
    }

    // How often the navigator re-validates / re-plans the path. Mirrors the
    // perception-tick jitter pattern in MobSimState — each navigator picks
    // an offset at construction so a swarm doesn't all repath on the same
    // frame. Keep this short enough that mobs respond to player movement,
    // long enough that the per-mob cost is bounded at swarm densities.
    private const float RepathIntervalSeconds = 0.4f;
    private const float RepathJitterSeconds = 0.2f;

    // Half-extent of the walkability sample window in voxels. 16 covers
    // local A*'s ~16m radius without sampling the entire streamed window.
    // Goals outside this window get truncated to the closest border cell —
    // the navigator catches up with the rest of the path on the next repath
    // after the mob has moved.
    private const int LocalGridHalfExtent = 16;
    private const int LocalGridSize = LocalGridHalfExtent * 2 + 1;

    // Arrival radius below which Goto considers the goal reached. Behaviors
    // can override this per-call by passing their own value to Goto.
    private const float DefaultArrivalDistance = 0.5f;

    // How close (XZ) the mob must get to a waypoint before we advance to
    // the next one. Smaller than DefaultArrivalDistance so the mob hits
    // each path corner cleanly rather than cutting across.
    private const float WaypointAdvanceDistance = 0.6f;

    // String-pulling lookahead cap. The pathfinder emits one waypoint per grid
    // cell (~1m apart), so steering at the immediate next waypoint kept the
    // mob's arrival-speed ramp engaged the whole path and it crawled at a
    // fraction of maxSpeed. Instead we skip ahead to the furthest waypoint still
    // reachable by a clear straight line on the walkability grid (GridLineClear)
    // — so the mob holds full speed and moves smoothly on open ground but hugs
    // corners exactly where a wall is in the way. Near the goal the lookahead
    // collapses onto the final waypoint and the arrival ramp slows it normally.
    // The LOS check makes a longer cap safe than blind distance would allow.
    private const float SteerLookaheadDistance = 6f;

    // Wander tuning. Brownian sampling: each repath when wandering, pick a
    // random walkable neighbour cell weighted by 1/cost and a forward bias
    // along the mob's current heading. This makes wander look like a
    // stumbling drift instead of a teleport-to-random-point hop.
    private const float WanderForwardBias = 1.6f;

    // Separation tuning. Mobs within (clearanceRadius * SeparationRadiusScale)
    // contribute a repulsion vector that's added to the steering target as
    // a positional offset. Soft-falloff: full strength at touching distance,
    // zero at the kernel edge. Strength is in voxel units of offset; values
    // around 1.0 nudge the mob ~1 voxel sideways at full overlap, which
    // reads as "they bump apart" rather than "they ricochet."
    private const float SeparationRadiusScale = 3f;
    private const float SeparationStrength = 1.2f;

    private readonly Mob _mob;
    private readonly TraversalProfile _profile;
    private readonly WalkabilityGrid _grid = new();
    private readonly LocalPathfinder _pathfinder = new();
    private readonly List<Vector3> _waypoints = new();
    // Reused across calls so the per-frame separation query doesn't churn
    // GC. Cleared at the top of each ApplySeparation.
    private readonly List<Mob> _separationScratch = new();

    private State _state;
    private Vector3 _goal;
    private float _arrivalDistance = DefaultArrivalDistance;
    // Whether the current goal allows the mob to take drops larger than
    // maxStepHeight. Set per-Goto-call by the behavior. Wander forces this
    // to false unconditionally so a mob can't accidentally wander itself
    // off a cliff its goto-pathing would happily descend.
    private bool _allowFalling;
    // Whether the current goal routes around damaging-prop danger zones
    // (fire trap, campfire, spike trap). True for wander and ordinary goto
    // so mobs don't stroll through hazards; the attack behavior sets it
    // false so a mob chasing the player can be lured onto one.
    private bool _avoidHazards = true;
    private Vector3 _wanderCenter;
    private float _wanderRadius;
    private Vector3 _lastWanderHeading = Vector3.Forward;
    private float _repathTimer;
    private float _repathInterval;
    private bool _arrived;
    private bool _blocked;
    private int _waypointIndex;

    public State CurrentState => _state;
    public bool HasArrived => _arrived;
    public bool IsBlocked => _blocked;
    public Vector3 Goal => _goal;
    // The mob's traversal profile, so behaviors can resolve goal points
    // (e.g. NavigationGoals.PickStandoffPoint) under the same water / step
    // rules the navigator itself paths by.
    public TraversalProfile Profile => _profile;

    // Read-only access for debug visualisation. The list is mutated each
    // repath so the debug drawer must walk it within a single frame and
    // not retain it across frames.
    public IReadOnlyList<Vector3> Waypoints => _waypoints;
    public int WaypointIndex => _waypointIndex;

    public MobNavigator(Mob mob)
    {
        _mob = mob;
        _profile = new TraversalProfile(mob.mobData);
        // Seed the repath timer with a per-mob offset so 100 mobs spawned on
        // the same frame don't all replan together. Initial timer is in
        // [0, RepathIntervalSeconds] so a fresh navigator's first plan
        // happens within one repath window — not deferred a full cycle.
        _repathInterval = RepathIntervalSeconds;
        _repathTimer = (float)GD.RandRange(0.0, RepathIntervalSeconds);
    }

    // Behavior-facing API.

    // allowFalling=true lets the pathfinder route the mob over drops up to
    // the mob's MobData.maxFallHeight (e.g. chase the player off a low
    // ledge). Default false matches the wander/follow-a-friend cases where
    // the path should always be reversible. Mobs that refuse to fall at
    // all should keep maxFallHeight=0 in their MobData; behaviors don't
    // need to know per-mob.
    public void Goto(Vector3 worldPos, float arrivalDistance = DefaultArrivalDistance, bool allowFalling = false, bool avoidHazards = true)
    {
        if (_state == State.Goto && _goal.DistanceSquaredTo(worldPos) < 0.01f && _allowFalling == allowFalling && _avoidHazards == avoidHazards)
        {
            // Same goal & policy restated; don't reset arrival flags.
            return;
        }
        _state = State.Goto;
        _goal = worldPos;
        _arrivalDistance = arrivalDistance;
        _allowFalling = allowFalling;
        _avoidHazards = avoidHazards;
        _arrived = false;
        _blocked = false;
        _waypoints.Clear();
        _waypointIndex = 0;
        _repathTimer = 0f;
    }

    public void Wander(Vector3 center, float radius)
    {
        _state = State.Wander;
        _wanderCenter = center;
        _wanderRadius = radius;
        // Wander explicitly disallows falling so the path stays reversible
        // — a wandering mob should never strand itself at the bottom of a
        // cliff its profile can't climb back up.
        _allowFalling = false;
        // A wandering mob always routes around hazards.
        _avoidHazards = true;
        _arrived = false;
        _blocked = false;
        _waypoints.Clear();
        _waypointIndex = 0;
        // Defer first goal pick to the next repath — by then RefreshGrid
        // will have populated the grid we sample neighbours on. Setting a
        // zero repath timer forces that to happen on the next WriteSteering.
        _repathTimer = 0f;
    }

    public void Stop()
    {
        _state = State.Idle;
        _arrived = true;
        _blocked = false;
        _waypoints.Clear();
        _waypointIndex = 0;
    }

    // Called by Mob each physics tick. Advances the repath timer, refreshes
    // the walkability grid + path when needed, and writes the next waypoint
    // to output.pathTarget. This is the only place the navigator should
    // touch the AIOutput struct.
    public void WriteSteering(float deltaSeconds, ref AIOutput output)
    {
        if (_state == State.Idle || _arrived)
        {
            return;
        }

        _repathTimer -= deltaSeconds;
        if (_repathTimer <= 0f)
        {
            _repathTimer = _repathInterval + (float)GD.RandRange(-RepathJitterSeconds, RepathJitterSeconds);
            // RefreshGrid samples LocalGridSize² = 1089 voxel columns every
            // 0.4s per mob — the hot spot at swarm density. Split out from
            // WanderPick and ReplanPath so the profiler can attribute cost.
            using (Profiler.Sample("MobNavigator.RefreshGrid"))
            {
                RefreshGrid();
            }
            if (_state == State.Wander && (_waypoints.Count == 0 || GoalReached() || _blocked))
            {
                using (Profiler.Sample("MobNavigator.WanderPick"))
                {
                    if (TryPickWanderGoal(out Vector3 p))
                    {
                        _goal = p;
                        _arrivalDistance = DefaultArrivalDistance;
                        _arrived = false;
                        _blocked = false;
                    }
                }
            }
            using (Profiler.Sample("MobNavigator.ReplanPath"))
            {
                ReplanPath();
            }
        }

        if (GoalReached())
        {
            _arrived = true;
            _waypoints.Clear();
            _waypointIndex = 0;
            return;
        }

        // Advance through waypoints as the mob arrives at each. Compare in
        // XZ only — vertical position is decided by the surface, and the
        // impulse layer already strips Y from its movement vector.
        Vector3 mobPos = _mob.GlobalPosition;
        while (_waypointIndex < _waypoints.Count - 1)
        {
            Vector3 wp = _waypoints[_waypointIndex];
            Vector2 d = new Vector2(wp.X - mobPos.X, wp.Z - mobPos.Z);
            if (d.Length() <= WaypointAdvanceDistance)
            {
                _waypointIndex++;
                continue;
            }
            break;
        }

        Vector3 steerTarget;
        if (_waypoints.Count > 0 && _waypointIndex < _waypoints.Count)
        {
            // String-pull: skip ahead to the furthest waypoint reachable by a
            // clear straight line on the walkability grid, capped at the
            // lookahead distance. Waypoints run forward along the path, so the
            // first one that's out of range OR not in line of sight ends the
            // skip — the mob then hugs that corner instead of cutting it.
            int steerIndex = _waypointIndex;
            for (int k = _waypointIndex + 1; k < _waypoints.Count; k++)
            {
                Vector3 wp = _waypoints[k];
                Vector2 d = new Vector2(wp.X - mobPos.X, wp.Z - mobPos.Z);
                if (d.Length() > SteerLookaheadDistance)
                {
                    break;
                }
                if (!GridLineClear(mobPos, wp))
                {
                    break;
                }
                steerIndex = k;
            }
            steerTarget = _waypoints[steerIndex];
        }
        else
        {
            // No path computed yet (first repath hasn't run, or A* failed
            // and we have nothing to follow). Steer directly toward the
            // goal so the mob still tries to make progress; the next
            // repath will resolve a real path or flag _blocked.
            steerTarget = _goal;
        }

        // Separation: nudge the steer target sideways away from nearby
        // mobs. Operates on the steer point rather than directly on the
        // velocity so the impulse layer in Mob._PhysicsProcess doesn't
        // need to know about it — feeds the same single Vector3 it
        // already consumes.
        using (Profiler.Sample("MobNavigator.ApplySeparation"))
        {
            steerTarget = ApplySeparation(mobPos, steerTarget);
        }

        output.pathTarget = steerTarget;
        if (output.speed <= 0f)
        {
            output.speed = 1f;
        }
        if (output.pathSuccessDistance <= 0f)
        {
            // For intermediate waypoints, success distance is the advance
            // threshold; for the final waypoint, the caller's arrival
            // distance. Picking the final value here is fine because the
            // mob only ever stops on the final waypoint.
            output.pathSuccessDistance = _arrivalDistance;
        }
    }

    private bool GoalReached()
    {
        Vector3 toGoal = _goal - _mob.GlobalPosition;
        toGoal.Y = 0f;
        return toGoal.Length() <= _arrivalDistance;
    }

    // Boids-style separation. Sums a repulsion vector from every mob within
    // the kernel radius (XZ-only), weighted by inverse distance, and adds
    // it to the current steer target as a positional offset. Vertical
    // distance is filtered out so a mob 8m below me on a different floor
    // doesn't push my path sideways.
    //
    // The query goes through World's spatial hash (cell ≈ 4m), so for a
    // separation kernel of ~1m the call touches at most 9 cells and
    // typically just 1. At 100-mob density that's a handful of comparisons
    // per mob per frame.
    private Vector3 ApplySeparation(Vector3 mobPos, Vector3 steerTarget)
    {
        World w = _mob.World;
        if (w == null)
        {
            return steerTarget;
        }
        float kernelRadius = _profile.clearanceRadius * SeparationRadiusScale;
        if (kernelRadius <= 0f)
        {
            return steerTarget;
        }

        _separationScratch.Clear();
        w.MobSpatialHash.QueryRadius(mobPos, kernelRadius, _separationScratch, _mob);
        if (_separationScratch.Count == 0)
        {
            return steerTarget;
        }

        // Vertical filter: exclude mobs more than 1.5 voxels above or
        // below us. They're plausibly on a different floor and shouldn't
        // contribute to horizontal separation.
        const float VerticalGate = 1.5f;
        float r2 = kernelRadius * kernelRadius;

        float ax = 0f;
        float az = 0f;
        for (int i = 0; i < _separationScratch.Count; i++)
        {
            Mob other = _separationScratch[i];
            Vector3 op = other.GlobalPosition;
            float dx = mobPos.X - op.X;
            float dy = mobPos.Y - op.Y;
            float dz = mobPos.Z - op.Z;
            if (dy > VerticalGate || dy < -VerticalGate)
            {
                continue;
            }
            float d2 = dx * dx + dz * dz;
            if (d2 <= 0.0001f || d2 >= r2)
            {
                continue;
            }
            // Linear falloff (1 at touching, 0 at kernel edge). Cheaper
            // than 1/d and gives a softer push that doesn't blow up at
            // small distances.
            float d = Mathf.Sqrt(d2);
            float falloff = 1f - (d / kernelRadius);
            float invD = 1f / d;
            ax += dx * invD * falloff;
            az += dz * invD * falloff;
        }

        if (ax == 0f && az == 0f)
        {
            return steerTarget;
        }
        return new Vector3(steerTarget.X + ax * SeparationStrength, steerTarget.Y, steerTarget.Z + az * SeparationStrength);
    }

    // Line-of-sight test on the resident walkability grid: can the mob walk a
    // straight line from `fromWorld` to `toWorld` without leaving walkable
    // ground or crossing a step taller than it can climb? Used by the
    // string-pulling lookahead to skip ahead only across cells it could
    // actually traverse, so smoothing never cuts a corner into a wall.
    //
    // Traversal is a 2D DDA (Amanatides-Woo) that steps ONE axis per cell, so
    // at a corner it passes through the orthogonal cell and rejects there if
    // it's blocked — the same diagonal-pinch guard the pathfinder applies.
    // Cheap: visits ~|di|+|dj| cells (a handful at this lookahead), all reads
    // against the already-sampled grid. Conservative on anything it can't
    // verify (off-grid, no walkable layer) — returns false and the caller
    // falls back to per-waypoint steering.
    private bool GridLineClear(Vector3 fromWorld, Vector3 toWorld)
    {
        int size = _grid.Size;
        int oi = _grid.OriginX;
        int oj = _grid.OriginZ;

        int i = Mathf.FloorToInt(fromWorld.X) - oi;
        int j = Mathf.FloorToInt(fromWorld.Z) - oj;
        int i1 = Mathf.FloorToInt(toWorld.X) - oi;
        int j1 = Mathf.FloorToInt(toWorld.Z) - oj;
        if (!InGrid(i, j, size) || !InGrid(i1, j1, size))
        {
            return false;
        }

        int startLayer = _grid.NearestLayer(i, j, fromWorld.Y);
        if (startLayer < 0)
        {
            return false;
        }
        float curSurfaceY = _grid.GetLayer(i, j, startLayer).surfaceY;

        // Line in grid-local coords for the DDA.
        float x0 = fromWorld.X - oi;
        float z0 = fromWorld.Z - oj;
        float dx = (toWorld.X - oi) - x0;
        float dz = (toWorld.Z - oj) - z0;
        int stepI = dx > 0f ? 1 : (dx < 0f ? -1 : 0);
        int stepJ = dz > 0f ? 1 : (dz < 0f ? -1 : 0);
        float adx = Mathf.Abs(dx);
        float adz = Mathf.Abs(dz);
        float tMaxX = stepI != 0 ? (stepI > 0 ? (i + 1 - x0) : (x0 - i)) / adx : float.MaxValue;
        float tMaxZ = stepJ != 0 ? (stepJ > 0 ? (j + 1 - z0) : (z0 - j)) / adz : float.MaxValue;
        float tDeltaX = stepI != 0 ? 1f / adx : float.MaxValue;
        float tDeltaZ = stepJ != 0 ? 1f / adz : float.MaxValue;

        // Bound the walk so a degenerate case can't spin; |di|+|dj| steps max.
        int guard = Mathf.Abs(i1 - i) + Mathf.Abs(j1 - j) + 2;
        while ((i != i1 || j != j1) && guard-- > 0)
        {
            if (tMaxX < tMaxZ)
            {
                i += stepI;
                tMaxX += tDeltaX;
            }
            else
            {
                j += stepJ;
                tMaxZ += tDeltaZ;
            }
            if (!InGrid(i, j, size))
            {
                return false;
            }
            int layer = _grid.NearestLayer(i, j, curSurfaceY);
            if (layer < 0)
            {
                return false;
            }
            WalkabilityCell c = _grid.GetLayer(i, j, layer);
            if (_avoidHazards && c.IsHazard)
            {
                return false;
            }
            if (!_profile.CanClimb && Mathf.Abs(c.surfaceY - curSurfaceY) > _profile.maxStepHeight)
            {
                return false;
            }
            curSurfaceY = c.surfaceY;
        }
        return true;
    }

    private static bool InGrid(int i, int j, int size)
    {
        return i >= 0 && i < size && j >= 0 && j < size;
    }

    private void RefreshGrid()
    {
        Vector3 origin = _mob.GlobalPosition;
        World w = _mob.World;
        WorldState ws = w?.WorldState;
        if (ws == null)
        {
            return;
        }
        int wx = Mathf.FloorToInt(origin.X);
        int wy = Mathf.FloorToInt(origin.Y);
        int wz = Mathf.FloorToInt(origin.Z);
        _grid.Sample(ws, w, _profile, wx, wy, wz, LocalGridSize);
    }

    // Run A* over the current grid from the mob's cell to the goal cell.
    // If the goal is outside the grid, project it to the nearest border
    // walkable cell along the goal direction so the mob makes progress
    // toward unstreamed targets without the pathfinder failing outright.
    private void ReplanPath()
    {
        if (_state == State.Idle)
        {
            return;
        }
        Vector3 start = _mob.GlobalPosition;
        Vector3 goal = _goal;

        int gi = Mathf.FloorToInt(goal.X) - _grid.OriginX;
        int gj = Mathf.FloorToInt(goal.Z) - _grid.OriginZ;
        int size = _grid.Size;
        if (gi < 0 || gi >= size || gj < 0 || gj >= size)
        {
            // Goal off-grid — pick the closest walkable cell along the line
            // from the mob to the goal as a temporary surrogate target.
            Vector3 dir = goal - start;
            dir.Y = 0f;
            float len = dir.Length();
            if (len < 0.001f)
            {
                _waypoints.Clear();
                _waypointIndex = 0;
                return;
            }
            dir /= len;
            // Walk inward from the goal direction at the grid border.
            float maxDist = LocalGridHalfExtent - 1f;
            Vector3 surrogate = start + dir * maxDist;
            gi = Mathf.FloorToInt(surrogate.X) - _grid.OriginX;
            gj = Mathf.FloorToInt(surrogate.Z) - _grid.OriginZ;
            gi = Mathf.Clamp(gi, 0, size - 1);
            gj = Mathf.Clamp(gj, 0, size - 1);
            if (_grid.LayerCount(gi, gj) == 0)
            {
                if (!FindNearestWalkable(gi, gj, out gi, out gj))
                {
                    _blocked = true;
                    _waypoints.Clear();
                    _waypointIndex = 0;
                    return;
                }
            }
            goal = _grid.CellToWorld(gi, gj, _grid.NearestLayer(gi, gj, _goal.Y));
        }
        else if (_grid.LayerCount(gi, gj) == 0)
        {
            // Goal cell itself isn't walkable — search outward for the
            // nearest walkable cell so the mob can reach the closest
            // approachable point. This handles encircle slots that landed
            // on a rock and similar near-misses.
            if (!FindNearestWalkable(gi, gj, out gi, out gj))
            {
                _blocked = true;
                _waypoints.Clear();
                _waypointIndex = 0;
                return;
            }
            goal = _grid.CellToWorld(gi, gj, _grid.NearestLayer(gi, gj, _goal.Y));
        }

        var path = _pathfinder.Find(_grid, _profile, start, goal, _allowFalling, _avoidHazards);
        if (path == null || path.Count == 0)
        {
            // A* either rejected the start cell (mob standing on something
            // we can't sample) or couldn't find any forward progress. Fall
            // back to direct steering — the next repath gets another shot.
            _blocked = true;
            _waypoints.Clear();
            _waypointIndex = 0;
            return;
        }
        _blocked = false;
        _waypoints.Clear();
        _waypoints.AddRange(path);
        _waypointIndex = 0;
    }

    // BFS outward from (i,j) on the grid for the nearest walkable cell.
    // Bounded by grid size — worst case touches every cell, which at 33×33
    // is ~1100 visits and still cheap.
    private bool FindNearestWalkable(int startI, int startJ, out int outI, out int outJ)
    {
        outI = startI;
        outJ = startJ;
        int size = _grid.Size;
        int maxRadius = size; // covers any cell in the grid from any start
        for (int r = 1; r <= maxRadius; r++)
        {
            for (int dj = -r; dj <= r; dj++)
            {
                for (int di = -r; di <= r; di++)
                {
                    // Only test the ring at distance r so we expand outward.
                    if (Mathf.Abs(di) != r && Mathf.Abs(dj) != r)
                    {
                        continue;
                    }
                    int ni = startI + di;
                    int nj = startJ + dj;
                    if (ni < 0 || ni >= size || nj < 0 || nj >= size)
                    {
                        continue;
                    }
                    if (_grid.LayerCount(ni, nj) > 0)
                    {
                        outI = ni;
                        outJ = nj;
                        return true;
                    }
                }
            }
        }
        return false;
    }

    // Wander point selection: starting from the mob's current cell, take a
    // sequence of random walkable steps on the grid until we've moved far
    // enough or run out of attempts. The endpoint is returned as the new
    // wander goal; A* in ReplanPath handles the actual route.
    //
    // Each step is biased toward the previous step's heading so the walk
    // doesn't oscillate in place — a long string of random direction picks
    // tends to cancel out, while a forward-biased random walk drifts
    // outward and reads as a deliberate stroll. Cell cost weights each
    // step (water cells discounted for water-averse mobs), so the
    // resulting endpoint already prefers dry, unobstructed ground.
    //
    // The leash to _wanderCenter is enforced as a hard rejection: if the
    // forward step would carry us outside the wander radius, we instead
    // turn back toward the center. This keeps the mob on its post even
    // across many wander legs without needing a separate "go home" rule.
    private bool TryPickWanderGoal(out Vector3 point)
    {
        point = default;
        Vector3 origin = _mob.GlobalPosition;
        int size = _grid.Size;
        int ci = Mathf.FloorToInt(origin.X) - _grid.OriginX;
        int cj = Mathf.FloorToInt(origin.Z) - _grid.OriginZ;
        if (ci < 0 || ci >= size || cj < 0 || cj >= size)
        {
            return false;
        }
        // Start the walk on the stacked surface the mob is actually standing
        // on, and carry that layer through each step so the stroll stays on
        // the mob's level (the cave floor, not the roof above it).
        int curLayer = _grid.NearestLayer(ci, cj, origin.Y);
        if (curLayer < 0)
        {
            return false;
        }

        // Total cells to traverse on the random walk. Sized in cells (each
        // ≈ 1 voxel) — for a 15-voxel wander radius, ~10 steps lands the
        // mob roughly 5–8 cells from origin (random walks have sub-linear
        // displacement vs step count, and the forward bias pushes that up).
        // Cap so we don't burn time when the walk dead-ends repeatedly.
        const int MinSteps = 6;
        const int MaxSteps = 16;
        int targetSteps = (int)GD.RandRange(MinSteps, MaxSteps);

        Vector3 heading = _lastWanderHeading;
        Vector3 leashCenter = _wanderCenter;
        float leashRadiusSq = _wanderRadius * _wanderRadius;

        int curI = ci;
        int curJ = cj;

        const int MaxCandidates = 8 * WalkabilityGrid.MaxColumnLayers;
        Span<int> di = stackalloc int[MaxCandidates];
        Span<int> dj = stackalloc int[MaxCandidates];
        Span<int> layerPick = stackalloc int[MaxCandidates];
        Span<float> weight = stackalloc float[MaxCandidates];

        for (int step = 0; step < targetSteps; step++)
        {
            int count = 0;
            float weightSum = 0f;
            WalkabilityCell prev = _grid.GetLayer(curI, curJ, curLayer);

            for (int ddj = -1; ddj <= 1; ddj++)
            {
                for (int ddi = -1; ddi <= 1; ddi++)
                {
                    if (ddi == 0 && ddj == 0)
                    {
                        continue;
                    }
                    int ni = curI + ddi;
                    int nj = curJ + ddj;
                    if (ni < 0 || ni >= size || nj < 0 || nj >= size)
                    {
                        continue;
                    }
                    // Consider each stacked surface in the neighbour column so
                    // the walk can step between layers (down into a cave) just
                    // like the pathfinder does.
                    int nLayers = _grid.LayerCount(ni, nj);
                    for (int nLayer = 0; nLayer < nLayers; nLayer++)
                    {
                        WalkabilityCell c = _grid.GetLayer(ni, nj, nLayer);
                        if (c.IsWater && _profile.waterCost > 1f)
                        {
                            continue;
                        }
                        // Never let the stroll endpoint land in (or the walk
                        // step onto) a hazard cell — wander routes around them.
                        if (c.IsHazard)
                        {
                            continue;
                        }
                        // No-fall rule: wander never selects a step that would
                        // require dropping or climbing more than maxStepHeight.
                        // The pathfinder enforces the same rule at routing
                        // time, but filtering here keeps the random walk's
                        // endpoint reachable both ways — the whole point of
                        // disallowing falls during wander.
                        int dy = c.surfaceY - prev.surfaceY;
                        if (!_profile.CanClimb && Mathf.Abs(dy) > _profile.maxStepHeight)
                        {
                            continue;
                        }

                        Vector3 cellWorld = _grid.CellToWorld(ni, nj, nLayer);
                        Vector2 leashDelta = new Vector2(cellWorld.X - leashCenter.X, cellWorld.Z - leashCenter.Z);
                        if (leashDelta.LengthSquared() > leashRadiusSq)
                        {
                            continue;
                        }

                        // Direction of THIS step, used for forward-bias weight.
                        // Diagonals normalized so the bias is by direction, not
                        // by step length.
                        float ndx = ddi;
                        float ndz = ddj;
                        float invLen = 1f / Mathf.Sqrt(ndx * ndx + ndz * ndz);
                        ndx *= invLen;
                        ndz *= invLen;
                        float forwardDot = ndx * heading.X + ndz * heading.Z;
                        float w = Mathf.Max(0.05f, 1f + WanderForwardBias * forwardDot) / Mathf.Max(c.cost, 0.01f);

                        di[count] = ddi;
                        dj[count] = ddj;
                        layerPick[count] = nLayer;
                        weight[count] = w;
                        weightSum += w;
                        count++;
                    }
                }
            }

            if (count == 0 || weightSum <= 0f)
            {
                // Walk dead-ended (boxed in by water, leash, or grid edge).
                // Stop here and use whatever we've reached so far if we've
                // moved at all; otherwise fail the pick.
                break;
            }

            float r = (float)GD.RandRange(0.0, weightSum);
            int picked = count - 1;
            for (int idx = 0; idx < count; idx++)
            {
                r -= weight[idx];
                if (r <= 0f)
                {
                    picked = idx;
                    break;
                }
            }

            curI += di[picked];
            curJ += dj[picked];
            curLayer = layerPick[picked];

            // Update heading to this step's direction so subsequent steps
            // bias forward off the new direction. Without this the walk
            // would always bias off the original heading and curl in.
            float hx = di[picked];
            float hz = dj[picked];
            float hLen = Mathf.Sqrt(hx * hx + hz * hz);
            if (hLen > 0f)
            {
                heading = new Vector3(hx / hLen, 0f, hz / hLen);
            }
        }

        if (curI == ci && curJ == cj)
        {
            return false;
        }

        Vector3 chosen = _grid.CellToWorld(curI, curJ, curLayer);
        Vector3 newHeading = chosen - origin;
        newHeading.Y = 0f;
        if (newHeading.LengthSquared() > 0.0001f)
        {
            _lastWanderHeading = newHeading.Normalized();
        }
        point = chosen;
        return true;
    }
}
