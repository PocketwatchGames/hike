using System.Collections.Generic;
using Godot;

// Lagrangian breadcrumb-based scent trail. The owner stamps timestamped
// breadcrumbs at its current position; each crumb is then advected by the
// per-position wind (zero under overhead cover, see GameClient.SampleWindSpeed)
// and linearly decays toward zero strength. Lifetime is implicit:
// `lifetime = strength / decayRate`, so a stronger emission lasts longer.
//
// Voxel-solid cells block both stamping and advection — a crumb that would
// enter a wall holds in place, and a stamp inside a solid voxel is dropped.
// Mobs that smell run their own raycast LOS gate when reading Crumbs, since
// even with wall-blocked drift a crumb on the player's side of a thin wall
// would otherwise leak straight through it.
//
// Pattern follows the plain-class emitters on Player (FootstepEmitter,
// FootprintEmitter): no scene node, no [Export] fields — owner constructs
// and ticks it.
public class ScentEmitter
{
    public struct Breadcrumb
    {
        public Vector3 pos;
        public float strength;
    }

    private readonly Node3D _owner;
    private readonly World _world;
    private readonly List<Breadcrumb> _crumbs = new();

    private readonly float _decayRate;
    private readonly float _stampIntervalSeconds;
    private readonly float _stampMoveDistanceSq;
    private readonly int _maxCrumbs;

    // Mutable per-frame. Owners drive this from game state — e.g. a wounded
    // player can crank it up to leak blood scent. <= 0 suppresses stamping.
    public float Strength;

    private Vector3 _lastStampPos;
    private ulong _lastStampMs;

    public IReadOnlyList<Breadcrumb> Crumbs => _crumbs;

    public ScentEmitter(
        Node3D owner,
        World world,
        float initialStrength,
        float decayRate,
        float stampIntervalSeconds,
        float stampMoveDistance,
        int maxCrumbs)
    {
        _owner = owner;
        _world = world;
        Strength = initialStrength;
        _decayRate = Mathf.Max(decayRate, 0.0001f);
        _stampIntervalSeconds = Mathf.Max(stampIntervalSeconds, 0f);
        _stampMoveDistanceSq = stampMoveDistance * stampMoveDistance;
        _maxCrumbs = Mathf.Max(maxCrumbs, 1);

        _lastStampPos = owner != null ? owner.GlobalPosition : Vector3.Zero;
        _lastStampMs = world?.GameTimeMs ?? 0;
    }

    public void Tick(float dt)
    {
        if (_world == null || _owner == null || dt <= 0f)
        {
            return;
        }
        WorldState ws = _world.WorldState;
        if (ws == null)
        {
            return;
        }

        // Wind direction is blended per-zone on SkyController each frame and
        // sits in XZ. Sampled once per tick — wind direction is global, only
        // wind SPEED varies per-position (zero under cover).
        Vector3 windDir = SkyController.Current?.ZoneState.WindDirection ?? Vector3.Zero;
        bool windActive = windDir.LengthSquared() > 0.0001f;
        if (windActive)
        {
            windDir = windDir.Normalized();
        }

        GameClient gc = GameClient.Current;

        for (int i = _crumbs.Count - 1; i >= 0; i--)
        {
            Breadcrumb c = _crumbs[i];

            c.strength -= _decayRate * dt;
            if (c.strength <= 0f)
            {
                _crumbs.RemoveAt(i);
                continue;
            }

            if (windActive && gc != null)
            {
                float windSpeed = gc.SampleWindSpeed(c.pos);
                if (windSpeed > 0f)
                {
                    Vector3 desired = c.pos + windDir * (windSpeed * dt);
                    int vx = Mathf.FloorToInt(desired.X);
                    int vy = Mathf.FloorToInt(desired.Y);
                    int vz = Mathf.FloorToInt(desired.Z);
                    if (!VoxelTypeInfo.IsSolid(ws.GetVoxelWorld(vx, vy, vz)))
                    {
                        c.pos = desired;
                    }
                }
            }

            _crumbs[i] = c;
        }

        if (Strength <= 0f)
        {
            return;
        }

        Vector3 ownerPos = _owner.GlobalPosition;
        ulong now = _world.GameTimeMs;
        bool timeOk = _stampIntervalSeconds <= 0f
            || now >= _lastStampMs + (ulong)(_stampIntervalSeconds * 1000f);
        bool distOk = _stampMoveDistanceSq > 0f
            && (ownerPos - _lastStampPos).LengthSquared() >= _stampMoveDistanceSq;
        if (!timeOk && !distOk)
        {
            return;
        }

        int sx = Mathf.FloorToInt(ownerPos.X);
        int sy = Mathf.FloorToInt(ownerPos.Y);
        int sz = Mathf.FloorToInt(ownerPos.Z);
        if (VoxelTypeInfo.IsSolid(ws.GetVoxelWorld(sx, sy, sz)))
        {
            return;
        }

        if (_crumbs.Count >= _maxCrumbs)
        {
            _crumbs.RemoveAt(0);
        }
        _crumbs.Add(new Breadcrumb { pos = ownerPos, strength = Strength });
        _lastStampPos = ownerPos;
        _lastStampMs = now;
    }
}
