using System.Collections.Generic;
using Godot;

// Probe ring for the iris cutaway (camera_clip_mode 4).
//
// Iris mode is the SCALAR mode with one disc on top: a base clip plane over the
// world, and a disc in plan inside which a lower target plane applies. This class
// owns the sampling both heights are derived from — it is deliberately its own
// code and shares nothing with the column or cell modes, which are on their way
// out.
//
// A ring of samples around the player answers two questions per sample, and the
// same ring serves both:
//
//   WHAT IS THE CEILING HERE — a HEIGHT, not "is something above me". Reading a
//   bool makes an approached doorway and a wall walked past register identically;
//   reading the height tells them apart, because a doorway's column has air at
//   the player's level with a low lintel over it while a wall's column has no air
//   at that level at all (EProbeSpace.Blocked, and excluded from everything).
//
//   IS THIS SPOT HIDDEN FROM THE CAMERA — march from the sample toward the
//   camera and look for solid on the way. Nearer samples weigh more, so the
//   aggregate means "the space I could act in is hidden" rather than "some pixel
//   of me is behind something", which is what keeps trees and posts from
//   triggering a full-screen cut.
//
// Foliage is invisible here for free: canopy lives in WorldState.CanopyAttenuation
// and is neither a solid voxel nor SunOpaque, so no probe can ever mistake a tree
// for a ceiling.
//
// Stage 2: the ring and its two queries, plus the debug drawing. Nothing here
// drives a clip path yet.
public class ClipIris
{
    // What a sample found at the player's level.
    public enum EProbeSpace
    {
        // Solid through the whole tolerance band — a wall. Contributes to
        // nothing: it is neither a low space to reveal nor open sky.
        Blocked,
        // Air at the player's level with a real ceiling over it.
        Open,
        // Air at the player's level and nothing overhead within the scan.
        Sky,
    }

    public struct Probe
    {
        // Where the occlusion march starts: body height above this column's own
        // floor, so the query asks about the space rather than about the ground.
        public Vector3 Point;
        public float Radius;
        // Proximity weight for the occlusion vote.
        public float Weight;
        public EProbeSpace Space;
        // World Y of the ceiling's underside. Only meaningful when Open;
        // PositiveInfinity for Sky, unset for Blocked.
        public float CeilingY;
        public bool Occluded;
    }

    // Ceiling of a sample that reached the scan limit without finding anything.
    // Infinity rather than a sentinel so it sorts correctly in the quantile — a
    // ring that is mostly sky lands the base on "no clip" with no special case.
    private const float NO_CEILING = float.PositiveInfinity;
    // Voxels the player's own floor resolve will climb looking for air. Their Y is
    // fractional on slopes and the capsule settles into what it stands on, so the
    // feet voxel is often the solid underfoot.
    private const int MAX_FLOOR_CLIMB = 3;
    // Fraction of a voxel the occlusion march advances per step. Coarser than a
    // DDA and can miss a one-voxel diagonal sliver — acceptable, because the
    // result feeds a weighted percentage rather than a per-pixel decision.
    private const float OCCLUSION_STEP = 0.5f;
    // Chunks searched around the player for roofs, per axis. A roof's node sits at
    // its footprint CENTRE, so this has to cover half the widest building rather
    // than the probe ring's own reach. Y is tighter because a roof only matters
    // here while its eave is near the player's own level.
    private const int ROOF_SEARCH_CHUNKS_XZ = 2;
    private const int ROOF_SEARCH_CHUNKS_Y = 1;

    // Tuning, copied in by GameClient each tick from its [Export]s.
    public float[] RingRadii = { 1.5f, 3f, 5f };
    public int RingSampleCount = 12;
    public float BodyHeight = 1f;
    public int CeilingScanHeight = 24;
    public int FloorTolerance = 2;
    public float OcclusionScanDistance = 24f;
    public float OcclusionWeightFalloff = 0.25f;
    // Quantile of the sorted per-sample ceilings taken as the base height. 0.5
    // (median) discards a couple of outliers in either direction — one probe
    // through a doorway must not drag the whole plane down, and one poking into
    // an alcove must not lift it. Lower biases toward the lowest thing seen,
    // higher toward the tallest.
    public float BaseCeilingQuantile = 0.5f;
    // How far below the base a sample's ceiling has to sit before it counts as a
    // low space worth revealing. Below this the two planes are close enough that
    // a disc would be pure noise.
    public float LowSpaceDrop = 2f;
    // How far below a ceiling's underside the cut plane parks. A voxel spans
    // [y, y+1), so a ceiling's underside — the only face visible from beneath it —
    // sits at exactly its own index, and the shaders cut on `>`. With no clearance
    // that face survives and the cutaway reads as having done nothing.
    public float Clearance = 0.5f;
    // Quantile over the samples the disc covers, for the target height. Lower than
    // the base's: inside the disc we want the low space itself, not the average of
    // it and its surroundings, but a hard min would let one sample through a
    // further doorway drag the whole reveal down.
    public float TargetCeilingQuantile = 0.25f;
    // Disk radius in SCREEN-PLANE metres, at the two extremes of how strongly the
    // ring is calling for it: far when the trigger is barely registering, near
    // when the player is on top of it.
    public float RadiusFar = 3f;
    public float RadiusNear = 10f;
    public float GrowSeconds = 0.35f;
    // Weighted occluded share that counts as "approaching a region the camera
    // cannot see into" — what opens the disk. Lower than the base latch, because
    // the disk is the warning and the latch is the commitment.
    public float ApproachOcclusion = 0.35f;
    // Weighted occluded share that latches the BASE elevation down, and the lower
    // one it releases at. Separate values because a single threshold chatters when
    // walking along a building edge.
    public float LatchOnFraction = 0.6f;
    public float LatchOffFraction = 0.35f;

    // --- Output: two planes and the disk between them ---
    // The scalar clip over the world — mode 1's plane, latched lower while the
    // player is obscured. Infinity means no cut at all.
    public float BaseClipY { get; private set; } = NO_CEILING;
    // The lower plane revealed inside the disk.
    public float TargetClipY { get; private set; } = NO_CEILING;
    // Disk radius in screen-plane metres, centred on the player.
    public float DiscRadius { get; private set; }
    public bool DiscActive => DiscRadius > 0f && TargetClipY < BaseClipY;
    // True while the base is being held down because the player is hidden. The
    // committed state, as against the disk's approaching one.
    public bool BaseLatched { get; private set; }
    // Camera screen basis, resolved once here and handed to the shaders and the
    // debug draw so all three describe the same disk.
    public Vector3 ScreenRight { get; private set; } = Vector3.Right;
    public Vector3 ScreenUp { get; private set; } = Vector3.Up;

    // Ring samples, valid for the tick that built them. Sized on first tick and
    // reused — the ring shape only changes when the tuning does.
    private Probe[] _probes = System.Array.Empty<Probe>();
    private int _probeCount;
    // Sorted scratch for the quantile, so it costs no allocation per tick.
    private float[] _ceilingScratch = System.Array.Empty<float>();
    // Unit XZ directions, rebuilt only when RingSampleCount changes so nothing
    // calls trig per frame.
    private Vector2[] _directions = System.Array.Empty<Vector2>();
    private int _directionCount;
    // Roofs near the player, refreshed each tick. A roof's cover in SunOpaque
    // includes its eave and rake oversail, and only the roof itself can tell the
    // room it is the ceiling of from the overhang hanging past it.
    private readonly List<Roof> _nearbyRoofs = new();

    public System.ReadOnlySpan<Probe> Probes => new(_probes, 0, _probeCount);
    // Voxel index of the lowest air at or above the player's feet — the level
    // every sample is taken from.
    public int PlayerFloorY { get; private set; }
    // The robust ceiling over the whole ring, or infinity for open sky. This is
    // the base plane's source; stage 3 turns it into a clip height.
    public float BaseCeilingY { get; private set; } = NO_CEILING;
    // Nearest sample sitting LowSpaceDrop or more below the base — the disc's
    // seed. -1 when nothing qualifies, which is the common outdoor case.
    public int NearestLowProbe { get; private set; } = -1;
    // Weighted share of the ring that is hidden from the camera, and whether the
    // player's own spot is. Promotion needs both (see the design note: a
    // percentage alone triggers behind trees and posts).
    public float OccludedWeight { get; private set; }
    public bool PlayerOccluded { get; private set; }
    // What the player's own column reads as, and how much of the ring is walled
    // off. Blocked samples take no part in either aggregate, so these are what
    // make an aggregate legible — a high blocked count means the numbers next to
    // it were computed from a handful of samples and should not be trusted.
    public EProbeSpace PlayerSpace { get; private set; }
    public int BlockedCount { get; private set; }
    private float _playerY;
    // The disk is centred on the player; kept as the exact position the shader
    // globals were pushed with so the CPU and GPU tests can't drift by a frame.
    private Vector3 _discCenter;
    public Vector3 DiscCenter => _discCenter;

    public void Tick(Sim sim, Vector3 playerPosition, GameCamera camera, float deltaSeconds)
    {
        _probeCount = 0;
        BaseCeilingY = NO_CEILING;
        NearestLowProbe = -1;
        OccludedWeight = 0f;
        PlayerOccluded = false;
        WorldState world = sim?.WorldState;
        if (world == null || camera == null)
        {
            return;
        }

        using var _prof = Profiler.Sample("ClipIris.Tick");

        GatherRoofs(sim, playerPosition);
        PlayerFloorY = ResolvePlayerFloor(world, playerPosition);
        EnsureBuffers();
        BuildDirections();

        int wx = Mathf.FloorToInt(playerPosition.X);
        int wz = Mathf.FloorToInt(playerPosition.Z);
        // The player's own column first, at index 0 and full weight: promotion
        // case (A) is entirely about what is directly overhead, and the occlusion
        // rule needs this one specifically rather than as part of the average.
        AddProbe(world, camera, wx, wz, 0f, playerPosition);
        PlayerOccluded = _probeCount > 0 && _probes[0].Occluded;
        PlayerSpace = _probeCount > 0 ? _probes[0].Space : EProbeSpace.Blocked;
        _playerY = playerPosition.Y;

        for (int r = 0; r < RingRadii.Length; r++)
        {
            float radius = RingRadii[r];
            if (radius <= 0f)
            {
                continue;
            }
            for (int i = 0; i < _directionCount; i++)
            {
                Vector2 dir = _directions[i];
                int sx = Mathf.FloorToInt(playerPosition.X + dir.X * radius);
                int sz = Mathf.FloorToInt(playerPosition.Z + dir.Y * radius);
                AddProbe(world, camera, sx, sz, radius, playerPosition);
            }
        }

        Aggregate(playerPosition);
        ScreenRight = camera.GlobalBasis.X.Normalized();
        ScreenUp = camera.GlobalBasis.Y.Normalized();
        _discCenter = playerPosition;
        TickDisc(playerPosition, deltaSeconds);
    }

    // Resolves the base plane, the latch, and the disk between the two.
    //
    // Two stages, and they are the same story at different distances. APPROACHING
    // something the camera cannot see into opens the disk — a screen-space circle
    // around the player showing the lower plane. Actually BEING hidden latches the
    // base itself down, at which point the disk has nothing left to reveal and
    // closes on its own.
    private void TickDisc(Vector3 playerPosition, float deltaSeconds)
    {
        float probed = float.IsPositiveInfinity(BaseCeilingY) ? NO_CEILING : BaseCeilingY - Clearance;

        // Hysteresis on the share; the player-occluded term is a hard gate in both
        // directions, since once they are visible again there is nothing to
        // rescue. Both are required — the share alone latches behind trees, posts
        // and railings, which hide the avatar without hiding the space.
        float threshold = BaseLatched ? LatchOffFraction : LatchOnFraction;
        BaseLatched = PlayerOccluded && OccludedWeight >= threshold;
        BaseClipY = BaseLatched ? Mathf.Min(probed, HeadPlane()) : probed;

        float targetRadius = ResolveDisk();

        // Growth eases; the close does NOT. A contracting reveal of space the
        // player is walking away from is useless and irritating, so leaving drops
        // the disk outright and lets the base plane's own dither carry it.
        if (targetRadius <= 0f)
        {
            DiscRadius = 0f;
            TargetClipY = NO_CEILING;
            return;
        }
        DiscRadius = Mathf.Lerp(DiscRadius, targetRadius,
            1f - Mathf.Exp(-deltaSeconds / Mathf.Max(GrowSeconds, 1e-3f)));
    }

    // The disk's radius and the plane it reveals. Zero radius means no disk.
    //
    // Centred on the player and growing from them — NOT from whatever the probes
    // found. The disk is the player's own bubble of readability; seeding it out at
    // the thing being approached makes it a spotlight on the geometry instead.
    private float ResolveDisk()
    {
        float target = NO_CEILING;
        float strength = 0f;

        // Approaching a low ceiling: the nearest low sample supplies the plane,
        // and how close it is supplies the growth.
        int seed = NearestLowProbe;
        if (seed >= 0)
        {
            target = Mathf.Min(target, LowSampleHeight() - Clearance);
            float reach = MaxRingRadius();
            float distance = _probes[seed].Radius;
            strength = reach > 1e-3f ? 1f - Mathf.Clamp(distance / reach, 0f, 1f) : 1f;
        }

        // Approaching somewhere the camera cannot see into. No low ceiling to read
        // a height from — the player is outdoors walking behind something — so it
        // falls back to their own head height on the plateau grid, the same plane
        // the manual reveal uses.
        if (OccludedWeight >= ApproachOcclusion)
        {
            target = Mathf.Min(target, HeadPlane());
            float span = Mathf.Max(1f - ApproachOcclusion, 1e-3f);
            strength = Mathf.Max(strength, (OccludedWeight - ApproachOcclusion) / span);
        }

        TargetClipY = target;
        // Nothing to reveal if the disk's plane is not actually below the base —
        // which is exactly what happens once the latch commits, so the disk closes
        // itself rather than needing to be told.
        if (!(target < BaseClipY))
        {
            return 0f;
        }
        return Mathf.Lerp(RadiusFar, RadiusNear, Mathf.Clamp(strength, 0f, 1f));
    }

    // Lowest ceiling among the low samples, outlier-rejected. A hard minimum lets
    // one sample that slipped through a further doorway drag the whole reveal down
    // to a space the player cannot even see yet.
    private float LowSampleHeight()
    {
        int count = 0;
        for (int i = 0; i < _probeCount; i++)
        {
            if (_probes[i].Space == EProbeSpace.Open)
            {
                _ceilingScratch[count++] = _probes[i].CeilingY;
            }
        }
        if (count == 0)
        {
            return NO_CEILING;
        }
        System.Array.Sort(_ceilingScratch, 0, count);
        int index = Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Clamp(TargetCeilingQuantile, 0f, 1f) * (count - 1)), 0, count - 1);
        return _ceilingScratch[index];
    }

    // The player's head height on the plateau grid — the same plane the manual R3
    // reveal drops to, so an automatic reveal and a requested one land identically.
    private float HeadPlane()
    {
        float eyeY = PlayerFloorY + GameCamera.EYE_HEIGHT;
        float step = Mathf.Max(GameCamera.PLATEAU_STEP, 1e-3f);
        return Mathf.Ceil(eyeY / step) * step - Clearance;
    }

    private float MaxRingRadius()
    {
        float max = 0f;
        for (int i = 0; RingRadii != null && i < RingRadii.Length; i++)
        {
            max = Mathf.Max(max, RingRadii[i]);
        }
        return max;
    }

    // Samples one column and appends it. Blocked columns are still recorded — the
    // debug view needs to show them, and seeing where the ring is being eaten by
    // walls is most of what stage 2 is for — but they take no part in either
    // aggregate.
    private void AddProbe(WorldState world, GameCamera camera, int wx, int wz, float radius, Vector3 playerPosition)
    {
        var probe = new Probe
        {
            Radius = radius,
            // Exponential rather than inverse-distance: it stays finite at the
            // player's own column and decays smoothly, so the far ring can be
            // extended for reach without diluting the vote.
            Weight = Mathf.Exp(-radius * Mathf.Max(OcclusionWeightFalloff, 0f)),
            CeilingY = NO_CEILING,
        };

        int floorY = ResolveProbeFloor(world, wx, wz);
        if (floorY < 0)
        {
            probe.Space = EProbeSpace.Blocked;
            // Still needs a point to draw at; park it at the player's level.
            probe.Point = new Vector3(wx + 0.5f, PlayerFloorY + BodyHeight, wz + 0.5f);
            Append(probe);
            return;
        }

        probe.Point = new Vector3(wx + 0.5f, floorY + BodyHeight, wz + 0.5f);
        int scanTop = floorY + Mathf.Max(CeilingScanHeight, 1);
        probe.Space = EProbeSpace.Sky;
        for (int wy = floorY + 1; wy < scanTop; wy++)
        {
            if (!IsCutawayOccupied(world, wx, wy, wz))
            {
                continue;
            }
            // A voxel index y spans world [y, y+1), so its underside — the only
            // face visible from below — is at exactly y.
            probe.CeilingY = wy;
            probe.Space = EProbeSpace.Open;
            break;
        }
        probe.Occluded = IsOccludedFromCamera(world, camera, probe.Point);
        Append(probe);
    }

    private void Append(in Probe probe)
    {
        if (_probeCount >= _probes.Length)
        {
            return;
        }
        _probes[_probeCount++] = probe;
    }

    // Base height from the sorted per-sample ceilings, and the nearest low sample
    // the disc would seed from.
    //
    // The quantile is what "eliminates outliers": one probe that slipped through a
    // doorway or into an alcove moves the sorted position by one slot instead of
    // taking the whole plane with it. Sky samples carry infinity, so a ring that is
    // mostly sky lands on "no clip" through the same comparison rather than a
    // special case — which is exactly right at a cave mouth, where the split
    // between inside and outside IS the measurement.
    private void Aggregate(Vector3 playerPosition)
    {
        int open = 0;
        float weighted = 0f;
        float weightTotal = 0f;
        BlockedCount = 0;
        for (int i = 0; i < _probeCount; i++)
        {
            Probe probe = _probes[i];
            if (probe.Space == EProbeSpace.Blocked)
            {
                BlockedCount++;
                continue;
            }
            _ceilingScratch[open++] = probe.CeilingY;
            weightTotal += probe.Weight;
            if (probe.Occluded)
            {
                weighted += probe.Weight;
            }
        }
        OccludedWeight = weightTotal > 0f ? weighted / weightTotal : 0f;
        if (open == 0)
        {
            // Walled in on every side — nothing to measure, and no plane is
            // better than a wrong one.
            return;
        }

        System.Array.Sort(_ceilingScratch, 0, open);
        int index = Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Clamp(BaseCeilingQuantile, 0f, 1f) * (open - 1)), 0, open - 1);
        BaseCeilingY = _ceilingScratch[index];

        if (float.IsPositiveInfinity(BaseCeilingY))
        {
            // Sky overhead. A low space alongside is still worth seeding a disc
            // from — that is the cave mouth and the doorway approached from the
            // street — so the search below runs against the drop from the SAMPLE
            // to nothing, which every real ceiling satisfies.
            FindNearestLow(playerPosition, float.PositiveInfinity);
            return;
        }
        FindNearestLow(playerPosition, BaseCeilingY - Mathf.Max(LowSpaceDrop, 0f));
    }

    // Nearest Open sample whose ceiling sits at or below `threshold`. Nearest, not
    // lowest: the disc follows what the player is walking toward, and picking the
    // lowest would make it jump to whichever distant sample happened to clip a
    // doorway.
    private void FindNearestLow(Vector3 playerPosition, float threshold)
    {
        float bestDistanceSq = float.MaxValue;
        for (int i = 0; i < _probeCount; i++)
        {
            Probe probe = _probes[i];
            if (probe.Space != EProbeSpace.Open || probe.CeilingY > threshold)
            {
                continue;
            }
            float dx = probe.Point.X - playerPosition.X;
            float dz = probe.Point.Z - playerPosition.Z;
            float distanceSq = dx * dx + dz * dz;
            if (distanceSq >= bestDistanceSq)
            {
                continue;
            }
            bestDistanceSq = distanceSq;
            NearestLowProbe = i;
        }
    }

    // Lowest air voxel at or above the player's feet. Not floor(playerY): the
    // capsule settles into what it stands on and grade blocks put the feet inside
    // a solid voxel, so taking that as the base makes the floor itself the ceiling
    // for every sample at once.
    private int ResolvePlayerFloor(WorldState world, Vector3 playerPosition)
    {
        int wx = Mathf.FloorToInt(playerPosition.X);
        int wz = Mathf.FloorToInt(playerPosition.Z);
        int foot = Mathf.FloorToInt(playerPosition.Y);
        for (int i = 0; i < MAX_FLOOR_CLIMB && IsCutawayOccupied(world, wx, foot, wz); i++)
        {
            foot++;
        }
        return foot;
    }

    // This column's own floor, or -1 if it is solid through the tolerance band.
    // The band is what keeps gently stepped ground from reading as a wall on every
    // side; anything taller than it is a step the player would have to climb, and
    // reads as blocked on purpose.
    private int ResolveProbeFloor(WorldState world, int wx, int wz)
    {
        int top = PlayerFloorY + Mathf.Max(FloorTolerance, 0);
        for (int wy = PlayerFloorY; wy <= top; wy++)
        {
            if (!IsCutawayOccupied(world, wx, wy, wz))
            {
                return wy;
            }
        }
        return -1;
    }

    // Roofs whose footprint could reach the probe ring. Looked up by chunk rather
    // than by walking every active entity: the dictionary is already keyed by
    // chunk coord, so a bounded neighbourhood costs a few dozen hash lookups
    // instead of a full sweep of the loaded world every frame.
    private void GatherRoofs(Sim sim, Vector3 playerPosition)
    {
        _nearbyRoofs.Clear();
        int cx = FloorDiv(Mathf.FloorToInt(playerPosition.X), ChunkState.SIZE);
        int cy = FloorDiv(Mathf.FloorToInt(playerPosition.Y), ChunkState.SIZE);
        int cz = FloorDiv(Mathf.FloorToInt(playerPosition.Z), ChunkState.SIZE);
        for (int dy = -ROOF_SEARCH_CHUNKS_Y; dy <= ROOF_SEARCH_CHUNKS_Y; dy++)
        {
            for (int dz = -ROOF_SEARCH_CHUNKS_XZ; dz <= ROOF_SEARCH_CHUNKS_XZ; dz++)
            {
                for (int dx = -ROOF_SEARCH_CHUNKS_XZ; dx <= ROOF_SEARCH_CHUNKS_XZ; dx++)
                {
                    if (!sim.ActiveEntities.TryGetValue(new Vector3I(cx + dx, cy + dy, cz + dz),
                        out List<Node3D> entities))
                    {
                        continue;
                    }
                    for (int i = 0; i < entities.Count; i++)
                    {
                        if (entities[i] is Roof roof)
                        {
                            _nearbyRoofs.Add(roof);
                        }
                    }
                }
            }
        }
    }

    // Occupancy as the CUTAWAY sees it. A solid voxel always counts. Roof cover
    // counts only where the roof says the column is inside the room it covers —
    // its eave and rake oversail are stamped into SunOpaque as well, because they
    // genuinely shade the ground, and treating those as a ceiling cuts the whole
    // roof away while the player is still outside the house standing under them.
    //
    // Cover no gathered roof recognises stays a ceiling. That means either a roof
    // further off than the search reaches or a source that is not a roof, and
    // reading it as open would stop a large hall cutting at all — a far worse
    // failure than an eave triggering.
    private bool IsCutawayOccupied(WorldState world, int wx, int wy, int wz)
    {
        if (VoxelTypeInfo.IsSolid(world.GetVoxelWorld(wx, wy, wz)))
        {
            return true;
        }
        if (!world.GetSunOpaqueWorld(wx, wy, wz))
        {
            return false;
        }
        var point = new Vector3(wx + 0.5f, wy + 0.5f, wz + 0.5f);
        bool oversail = false;
        for (int i = 0; i < _nearbyRoofs.Count; i++)
        {
            switch (_nearbyRoofs[i].CoverAt(point))
            {
                case ERoofCover.Ceiling:
                    return true;
                case ERoofCover.Oversail:
                    oversail = true;
                    break;
            }
        }
        return !oversail;
    }

    private static int FloorDiv(int value, int divisor)
    {
        int q = value / divisor;
        return (value % divisor != 0 && (value < 0) != (divisor < 0)) ? q - 1 : q;
    }

    // March from the sample toward the camera looking for solid. The direction is
    // constant for the orthographic presets and per-point for the perspective
    // ones, matching how the clip shaders resolve their own view direction.
    private bool IsOccludedFromCamera(WorldState world, GameCamera camera, Vector3 from)
    {
        Vector3 toCamera = camera.Projection == Camera3D.ProjectionType.Perspective
            ? (camera.GlobalPosition - from).Normalized()
            : camera.GlobalBasis.Z.Normalized();
        int startX = Mathf.FloorToInt(from.X);
        int startY = Mathf.FloorToInt(from.Y);
        int startZ = Mathf.FloorToInt(from.Z);
        float distance = Mathf.Max(OcclusionScanDistance, OCCLUSION_STEP);
        for (float t = OCCLUSION_STEP; t <= distance; t += OCCLUSION_STEP)
        {
            Vector3 point = from + toCamera * t;
            int wx = Mathf.FloorToInt(point.X);
            int wy = Mathf.FloorToInt(point.Y);
            int wz = Mathf.FloorToInt(point.Z);
            // The sample's own voxel can't occlude it — the march starts inside
            // it, and a body-height point in a low space often shares its voxel
            // with the ceiling it is standing under.
            if (wx == startX && wy == startY && wz == startZ)
            {
                continue;
            }
            if (IsOccupied(world, wx, wy, wz))
            {
                return true;
            }
        }
        return false;
    }

    // Occupancy for the OCCLUSION march, which wants the opposite of the ceiling
    // query above: an eave is not a ceiling, but it does genuinely hide someone
    // standing under it from the camera, so every scrap of roof cover counts here
    // and no roof is consulted. The second source is the easy one to miss — a roof
    // is an ENTITY, so a purely voxel test lets a cottage read as open sky.
    private static bool IsOccupied(WorldState world, int wx, int wy, int wz)
    {
        return VoxelTypeInfo.IsSolid(world.GetVoxelWorld(wx, wy, wz))
            || world.GetSunOpaqueWorld(wx, wy, wz);
    }

    private void EnsureBuffers()
    {
        int capacity = 1 + Mathf.Max(RingSampleCount, 0) * (RingRadii?.Length ?? 0);
        if (_probes.Length != capacity)
        {
            _probes = new Probe[capacity];
            _ceilingScratch = new float[capacity];
        }
    }

    private void BuildDirections()
    {
        int count = Mathf.Max(RingSampleCount, 0);
        if (_directionCount == count)
        {
            return;
        }
        _directions = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            // Half-step offset so no bearing lands exactly on an axis, where a
            // sample would straddle two columns and flip between them as the
            // player drifts.
            float angle = (i + 0.5f) / count * Mathf.Tau;
            _directions[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        }
        _directionCount = count;
    }

    // The clip height that applies at this world point — the CPU twin of the
    // shader's disc test, for prop culling and the "can the player see this" gate.
    // Min rather than the disc's own value so the two planes can never invert if
    // the target ever resolves above the base.
    public float ClipHeightAt(Vector3 worldPosition)
    {
        if (!DiscActive)
        {
            return BaseClipY;
        }
        // Screen-plane distance, matching the shader exactly — the disk is a
        // circle ON SCREEN, so a world-space test here would disagree with what is
        // drawn everywhere the camera is pitched.
        Vector3 delta = worldPosition - _discCenter;
        float x = delta.Dot(ScreenRight);
        float y = delta.Dot(ScreenUp);
        return x * x + y * y <= DiscRadius * DiscRadius ? TargetClipY : BaseClipY;
    }

    public string Describe()
    {
        string baseText = float.IsPositiveInfinity(BaseCeilingY) ? "sky" : BaseCeilingY.ToString("0.0");
        string lowText = NearestLowProbe < 0
            ? "none"
            : $"{_probes[NearestLowProbe].CeilingY:0.0}@{_probes[NearestLowProbe].Radius:0.0}m";
        string discText = DiscActive ? $"r={DiscRadius:0.0} target={TargetClipY:0.0}" : "off";
        string clipText = float.IsPositiveInfinity(BaseClipY) ? "none" : BaseClipY.ToString("0.0");
        return $"y={_playerY:0.0} floorY={PlayerFloorY} self={PlayerSpace} ceiling={baseText} low={lowText} "
            + $"occluded={OccludedWeight:0.00} playerOccluded={PlayerOccluded} "
            + $"blocked={BlockedCount}/{_probeCount} baseClip={clipText} latched={BaseLatched} disk={discText}";
    }
}
