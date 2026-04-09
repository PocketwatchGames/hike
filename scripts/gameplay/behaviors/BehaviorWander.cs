using Godot;

public partial class BehaviorWander : BehaviorBase
{
    private const float WanderRange = 15f;
    private const float PathSuccessDistance = 1f;
    private const ulong PathTimeoutMs = 20000;
    private const int PatrolPointAttempts = 8;
    // How far above/below the mob's current Y we'll accept a ground sample
    // along the path. Mobs can clear small steps but shouldn't be routed off
    // cliffs or into tall walls.
    private const int PathGroundSearchRadius = 2;
    // When every random candidate is rejected (mob is at a streaming edge or
    // boxed in), retry again after this short pause instead of burning through
    // random rolls every tick.
    private const ulong NoValidPointRetryMs = 1000;

    private readonly WanderBehaviorData _data;
    private ulong _pauseUntilMs;
    private ulong _pathTimeoutMs;
    private Vector3? _patrolPoint;

    public BehaviorWander(WanderBehaviorData data)
    {
        _data = data;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }

        if (!_patrolPoint.HasValue && time >= _pauseUntilMs)
        {
            if (TryPickPatrolPoint(me, out Vector3 point))
            {
                _patrolPoint = point;
                _pathTimeoutMs = time + PathTimeoutMs;
            }
            else
            {
                // No walkable point in any direction this frame (streaming
                // edge, surrounded by walls/gaps). Back off briefly instead of
                // spinning on random rolls.
                _pauseUntilMs = time + NoValidPointRetryMs;
            }
        }

        if (_patrolPoint.HasValue)
        {
            Vector3 toPoint = _patrolPoint.Value - me.GlobalPosition;
            toPoint.Y = 0f;
            if (toPoint.Length() > PathSuccessDistance && time < _pathTimeoutMs)
            {
                output.pathTarget = _patrolPoint.Value;
                output.speed = 0.25f;
                output.pathSuccessDistance = 0.5f;
            }
            else
            {
                double pauseSeconds = GD.RandRange((double)_data.pauseTimeRange.X, (double)_data.pauseTimeRange.Y);
                _pauseUntilMs = time + (ulong)(pauseSeconds * 1000.0);
                _patrolPoint = null;
            }
        }

        return new BehaviorOutput(EBehaviorResult.Running);
    }

    // Picks a random point in a disc around the mob, but only accepts it if
    // the straight-line XZ path between the mob and the point stays on loaded,
    // walkable terrain. This keeps wander from marching mobs off cliffs, into
    // caves, or out of the streamed chunk window (all of which end with the
    // mob stuck at the bottom of the world).
    private static bool TryPickPatrolPoint(Mob me, out Vector3 point)
    {
        WorldState ws = me.World.WorldState;
        Vector3 origin = me.GlobalPosition;

        for (int attempt = 0; attempt < PatrolPointAttempts; attempt++)
        {
            float angle = (float)GD.RandRange(0.0, Mathf.Tau);
            float radius = WanderRange * Mathf.Sqrt((float)GD.Randf());
            Vector3 candidate = origin + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

            if (PathStaysOnGround(ws, origin, candidate))
            {
                point = candidate;
                return true;
            }
        }

        point = default;
        return false;
    }

    // Walks the XZ segment from -> to in roughly 1m steps. At each step the
    // column must be inside a loaded chunk and must contain a standable voxel
    // (air with solid below) within PathGroundSearchRadius of the start Y.
    // Anything outside the loaded window, over a gap, or blocked by a wall
    // taller than the search radius fails.
    private static bool PathStaysOnGround(WorldState ws, Vector3 from, Vector3 to)
    {
        Vector3 delta = to - from;
        delta.Y = 0f;
        float dist = delta.Length();
        if (dist < 0.001f)
        {
            return true;
        }

        int startFloorY = Mathf.FloorToInt(from.Y);
        int steps = Mathf.Max(1, Mathf.CeilToInt(dist));
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            Vector3 sample = from + delta * t;
            int wx = Mathf.FloorToInt(sample.X);
            int wz = Mathf.FloorToInt(sample.Z);

            if (!HasStandableColumn(ws, wx, wz, startFloorY))
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasStandableColumn(WorldState ws, int wx, int wz, int startFloorY)
    {
        // Search from above the start Y downward so we prefer the nearest
        // standable surface at or just below where the mob currently is.
        for (int dy = PathGroundSearchRadius; dy >= -PathGroundSearchRadius; dy--)
        {
            int wy = startFloorY + dy;
            if (!ws.IsInBounds(wx, wy, wz))
            {
                // Column crosses out of the loaded chunk window — reject the
                // whole candidate so the mob never targets unstreamed space.
                return false;
            }
            if (ws.GetVoxelWorld(wx, wy, wz) != VoxelType.Air)
            {
                continue;
            }
            // Need solid ground directly under this air cell. If the cell
            // below is outside the loaded window we also reject: we can't
            // confirm footing, which is the whole point of the check.
            if (!ws.IsInBounds(wx, wy - 1, wz))
            {
                return false;
            }
            if (ws.GetVoxelWorld(wx, wy - 1, wz) != VoxelType.Air)
            {
                return true;
            }
        }
        return false;
    }
}
