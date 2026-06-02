using Godot;

// A water-locked rideable boat. Floats on the nearest water surface, paddles
// toward the rider's steering input with momentum, and refuses propulsion when
// beached. Physics is self-contained in _PhysicsProcess; the rider is a
// passive passenger parented under the seat anchor (see Player.Mount), so the
// boat's transform carries them along.
[GlobalClass]
public partial class Boat : RideableVehicle
{
    [Export] private BoatData _data;

    private float _bobPhase;
    private bool _propelling;

    // Vertical voxels searched above/below the hull when locating the water
    // surface in the boat's column.
    private const int WaterSearchVertical = 3;

    // Ring radius (voxels) scanned for a standable shore cell on dismount.
    private const int DismountSearchRadius = 5;

    protected override RideableData RideData => _data;

    public override bool IsPropelling => _propelling;

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        if (dt <= 0f || _data == null || _world == null)
        {
            return;
        }

        // Locate the water surface in this column. Null => beached on land.
        float? surfaceY = FindWaterSurfaceY(GlobalPosition);

        // Steering intent (camera-relative), only while ridden and afloat.
        Vector3 steer = Vector3.Zero;
        if (_rider != null)
        {
            steer = _rider.MountMoveInput;
            steer.Y = 0f;
        }
        float steerMag = steer.Length();
        _propelling = surfaceY.HasValue && steerMag > _data.propellingInputThreshold;

        // Horizontal momentum.
        Vector3 horizVel = new(Velocity.X, 0f, Velocity.Z);
        if (_propelling)
        {
            Vector3 target = steer.Normalized() * _data.maxSpeed * Mathf.Min(steerMag, 1f);
            horizVel = horizVel.MoveToward(target, _data.acceleration * dt);

            // Pivot the hull toward the travel heading at a bounded rate so the
            // boat turns with weight instead of snapping.
            float targetYaw = Mathf.Atan2(target.X, target.Z);
            float maxStep = Mathf.DegToRad(_data.turnRateDegrees) * dt;
            float yaw = Mathf.RotateToward(Rotation.Y, targetYaw, maxStep);
            Rotation = new Vector3(0f, yaw, 0f);
        }
        else
        {
            horizVel = horizVel.MoveToward(Vector3.Zero, _data.drag * dt);
        }

        // Vertical: settle onto the water surface (+ bob), or sink to the
        // ground when beached. The buoyancy lerp is expressed as a velocity so
        // MoveAndSlide still resolves collisions with banks and the lake floor.
        float vy;
        if (surfaceY.HasValue)
        {
            _bobPhase += dt / Mathf.Max(0.01f, _data.bobPeriodSeconds) * Mathf.Tau;
            float bob = Mathf.Sin(_bobPhase) * _data.bobAmplitude;
            float targetY = surfaceY.Value + bob;
            float newY = Mathf.Lerp(GlobalPosition.Y, targetY, Mathf.Min(1f, _data.buoyancyLerp * dt));
            vy = (newY - GlobalPosition.Y) / dt;
        }
        else
        {
            vy = Velocity.Y - _data.beachedGravity * dt;
        }

        Velocity = new Vector3(horizVel.X, vy, horizVel.Z);
        MoveAndSlide();
    }

    // World Y of the water surface (top face of the highest water voxel) in the
    // boat's column, or null if no water sits within WaterSearchVertical voxels
    // of the hull. Mirrors Player.UpdateWaterState's column scan.
    private float? FindWaterSurfaceY(Vector3 world)
    {
        WorldState ws = _world.WorldState;
        int fx = Mathf.FloorToInt(world.X);
        int fz = Mathf.FloorToInt(world.Z);
        int startY = Mathf.FloorToInt(world.Y);

        for (int y = startY + WaterSearchVertical; y >= startY - WaterSearchVertical; y--)
        {
            if (ws.GetVoxelWorld(fx, y, fz) == VoxelType.Water)
            {
                int s = y;
                while (ws.GetVoxelWorld(fx, s + 1, fz) == VoxelType.Water)
                {
                    s++;
                }
                return s + 1f;
            }
        }
        return null;
    }

    // Nearest standable shore cell around the hull, so dismounting drops the
    // player on dry ground rather than into the water. Falls back to the boat's
    // own position when no shore is found within DismountSearchRadius.
    public override Vector3 GetDismountPosition()
    {
        WorldState ws = _world?.WorldState;
        if (ws == null)
        {
            return GlobalPosition;
        }
        int cx = Mathf.FloorToInt(GlobalPosition.X);
        int cy = Mathf.FloorToInt(GlobalPosition.Y);
        int cz = Mathf.FloorToInt(GlobalPosition.Z);

        for (int r = 1; r <= DismountSearchRadius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    // Boundary of the ring only — interior cells were covered
                    // by a smaller radius.
                    if (Mathf.Abs(dx) != r && Mathf.Abs(dz) != r)
                    {
                        continue;
                    }
                    if (TryStandableColumn(ws, cx + dx, cy, cz + dz, out Vector3 p))
                    {
                        return p;
                    }
                }
            }
        }
        return GlobalPosition;
    }

    // A standable cell is a solid voxel with air directly above it, within a
    // couple voxels of the hull's height. Water-topped columns are rejected so
    // the player isn't dropped back into the lake.
    private static bool TryStandableColumn(WorldState ws, int x, int yNear, int z, out Vector3 pos)
    {
        pos = Vector3.Zero;
        for (int y = yNear + 2; y >= yNear - 2; y--)
        {
            VoxelType v = ws.GetVoxelWorld(x, y, z);
            VoxelType above = ws.GetVoxelWorld(x, y + 1, z);
            if (VoxelTypeInfo.IsSolid(v) && above == VoxelType.Air)
            {
                pos = new Vector3(x + 0.5f, y + 1f, z + 0.5f);
                return true;
            }
        }
        return false;
    }

    public static Boat Create(World world, BoatSimState data)
    {
        var instance = data.Scene.Instantiate<Boat>();
        instance.Position = data.WorldPosition;
        instance.RotationDegrees = new Vector3(0f, Mathf.RadToDeg(data.RotationY), 0f);
        instance._world = world;
        world.AddChild(instance);
        return instance;
    }
}
