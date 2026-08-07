using System.Collections.Generic;
using Godot;

// The ceiling cutaway's probes, and the two planes they resolve.
//
// BASE PLANE — the ceiling of the space the player is standing in, cut
// world-wide. The MAXIMUM ceiling over the nine columns immediately around them,
// and both halves of that matter. Max, because too high is safe and too low is
// not: a plane above the roof leaves the player covered, so the camera cannot see
// them, so the disk opens and reveals it anyway — while a plane too low just
// flattens the space with nothing to catch it. Nine columns, because a max over
// anything wider reaches into whatever is next door and comes back with a ceiling
// from another building.
//
// Sky anywhere in those nine switches the cut off entirely. That is the same max
// rule taken to its limit — sky is the highest answer there is — and it is what
// makes standing under a hole in a cave roof read as being outdoors.
//
// IRIS PLANE — a fixed elevation above the player's eyes, revealed inside a
// player-centred region. Deliberately NOT geometry-derived: it is the same plane
// the manual reveal uses, so it cannot flicker at all, and the region is free to
// grow and shrink without dragging a height around with it. That region is driven
// by a wide, breathing ring of occlusion samples — a separate question from the
// ceiling, and so a separate set of samples.
//
// Three kinds of column are excluded from the height entirely, and each was a bug
// before it was a rule: DOORWAYS AND WINDOWS (authored as VoxelType.Opening,
// whose lintel and sill read exactly like a low ceiling and dragged the plane
// down every time the player passed one), WALLS, and columns whose only cover is
// a roof's OVERSAIL (cover, but neither a room nor open sky — calling that sky
// switched the whole cutaway off under the eaves).
//
// Foliage is excluded on both halves, by two different mechanisms. The CEILING scan
// reads voxels, and canopy lives in WorldState.CanopyAttenuation rather than as a
// solid voxel or a SunOpaque one, so no column can mistake a tree for a ceiling. The
// OCCLUSION query raycasts colliders masked to Environment, and trees are PorousBody
// on Porous — the same line the project already draws for perched vision and flight.
public class ClipIris
{
    // What a sample found at the player's level.
    public enum EProbeSpace
    {
        // Solid through the whole tolerance band — a wall.
        Blocked,
        // A doorway or window void. Real space, so it still votes on occlusion,
        // but never on height: its "ceiling" is a lintel or a sill.
        Opening,
        // The only cover overhead is a roof's eave or rake oversail. NOT sky —
        // there is something over this column — but not a room's ceiling either,
        // so it votes on neither. Reporting it as sky was a bug: sky is a positive
        // claim of open air, and it switches the whole cutaway off.
        Oversail,
        // Plain air with a real ceiling over it. The only kind that votes on
        // height.
        Open,
        // Plain air with nothing overhead within the scan.
        Sky,
    }

    // One rung of the player-hidden ladder.
    public struct HiddenRay
    {
        public Vector3 From;
        public Vector3 Hit;
        public bool Blocked;
    }

    public struct Probe
    {
        // Body height above this column's own floor. The occlusion ray starts here
        // but rises first — see IsOccludedFromCamera.
        public Vector3 Point;
        // Distance from the player in the CAMERA PLANE — the same measure the
        // disk uses, so the radius that covers a probe covers it on screen.
        public float ScreenDistance;
        public EProbeSpace Space;
        // World Y of the ceiling's underside. Only meaningful when Open.
        public float CeilingY;
        // Can the PLAYER see this sample? A sample they cannot see describes space
        // they have no business revealing, and never counts as occluded.
        public bool Visible;
        public bool Occluded;
        // Where the camera ray actually started (RAISED off the body — see
        // IsOccludedFromCamera) and where it stopped. Only so the overlay can draw
        // WHAT is doing the hiding: which samples are occluded has never been the
        // hard question, what is occluding them is.
        public Vector3 OcclusionFrom;
        public Vector3 OcclusionHit;
    }

    private const float NO_CEILING = float.PositiveInfinity;
    // Voxels the player's own floor resolve will climb looking for air. Their Y is
    // fractional on slopes and the capsule settles into what it stands on, so the
    // feet voxel is often the solid underfoot.
    private const int MAX_FLOOR_CLIMB = 3;
    // Fraction of a voxel the player-sight march advances per step. Coarser than a
    // DDA and can miss a one-voxel diagonal sliver — acceptable, because the result
    // feeds a count rather than a per-pixel decision. Nothing to do with the
    // occlusion query, which raycasts colliders and takes no steps at all.
    private const float SIGHT_STEP = 0.5f;
    // How far under the first cover overhead the lift parks, so the march origin
    // stays beneath it instead of teleporting through.
    private const float LIFT_CLEARANCE = 0.1f;
    // Columns the CEILING is read from: the player's own plus its eight
    // neighbours. Deliberately tiny — see BuildProbes.
    private const int CEILING_SAMPLES = 9;
    // Rungs of the player-hidden ladder. Three gives the reach four settling points
    // between its two sizes, which is enough to read as easing rather than snapping.
    private const int PLAYER_HIDDEN_TESTS = 3;
    // Voxels a sample will climb to find its column's own ground. Bounds the search
    // inside a cliff; a column buried deeper than this resolves to whatever it
    // reached, which is above the player and therefore clears the same terrain the
    // player is standing in.
    private const int MAX_SURFACE_CLIMB = 24;
    // Chunks searched around the player for roofs, per axis. A roof's node sits at
    // its footprint CENTRE, so this has to cover half the widest building rather
    // than the ring's own reach.
    private const int ROOF_SEARCH_CHUNKS_XZ = 2;
    private const int ROOF_SEARCH_CHUNKS_Y = 1;

    // Tuning, copied in by GameClient each tick from its [Export]s.
    public int RingSampleCount = 12;
    public int RingCount = 3;
    // The two iris sizes, in metres. These bound the disk AND set how far the ring
    // reaches — one pair of numbers for both, because the ring only ever needs to
    // see as far as the disk could grow, and two pairs could disagree.
    //
    // The reach shrinks to the small size while the player is visible and grows to
    // the large one while they are hidden: when nothing is wrong only the space
    // immediately around them matters, and when they are covered the question
    // becomes how far the cover extends.
    public float RadiusMin = 3.5f;
    public float RadiusMax = 8f;
    // The size a doorway or window peek opens to on its own. Its own number because a
    // peek is neither of the other two cases: nothing is hiding the player, so the
    // ring finds no occlusion to size a disk from, but a room seen through an opening
    // is worth more than the size given to "nothing is wrong here".
    public float OpeningRadius = 5.75f;
    public float ProbeRangeSeconds = 0.4f;
    public float BodyHeight = 1f;
    public int CeilingScanHeight = 24;
    public int FloorTolerance = 2;
    public float OcclusionScanDistance = 24f;
    // The two heights the occlusion ray tries to start from, RAYCAST rather than
    // assumed, and the elevation that decides between them. See IsOccludedFromCamera:
    // the low one is asked first so cover right beside the player still registers, and
    // the high one is only re-asked when the thing that blocked it was short enough to
    // be terrain. Anything overhead stops either rise short.
    //
    // ShortCover wants to sit just above a plateau step, so one terrace is short and
    // two are not.
    public float OcclusionLift = 2f;
    public float OcclusionLiftHigh = 4.5f;
    public float ShortCover = 4.25f;
    public float Clearance = 0.5f;
    // Metres from the player within which a window or door does NOT stop the ring.
    // Wants to be about "standing in it or right up against it" — see
    // IsVisibleFromPlayer.
    public float OpeningReach = 1.5f;
    // Metres each rung of the player-hidden ladder rises above the one below. The
    // ladder starts at the eye, so this is how much of the player's height above it
    // still counts toward being hidden.
    public float PlayerHiddenRise = 1f;
    // Metres of margin past the farthest hidden sample, so the reveal clears the
    // thing doing the hiding rather than stopping on it.
    public float IrisPadding = 2f;
    public float IrisGrowSeconds = 0.35f;
    public float IrisShrinkSeconds = 0.5f;
    // Shape aspect, shared with the FOLIAGE cutaway (foliagePlayerFadeAspect*) so the
    // canopy fade and the ceiling reveal are the same shape rather than two effects
    // that happen to open around the player. It scales the radius outright, as it does
    // there — so RadiusMin/Max are the shape's SHORT axis, not its extent.
    public Vector2 ShapeAspect = new(1.6f, 1.2f);

    // --- Output ---
    // The voted ceiling of the space the player is in, or infinity for open sky.
    public float BaseClipY { get; private set; } = NO_CEILING;
    // Fixed plane the disk reveals: the plateau boundary above the player's eyes.
    public float IrisClipY { get; private set; } = NO_CEILING;
    // Disk radius in screen-plane metres, centred on the player.
    public float IrisRadius { get; private set; }
    public bool IrisActive => IrisRadius > 0f;
    // Camera screen basis, resolved once here and handed to the shaders and the
    // debug draw so all three describe the same disk.
    public Vector3 ScreenRight { get; private set; } = Vector3.Right;
    public Vector3 ScreenUp { get; private set; } = Vector3.Up;
    public Vector3 IrisCenter => _irisCenter;

    // Voxel index of the lowest air at or above the player's feet — the level
    // every sample is taken from.
    public int PlayerFloorY { get; private set; }
    // HOW hidden the player is, 0 (eye in plain view) to 1 (still hidden a ladder's
    // height above it). A ladder rather than a flag because being half behind a wall
    // is a real state and snapping the reach between two sizes made it read as a
    // twitch; this eases through it instead.
    public float PlayerHiddenAmount { get; private set; }
    public bool PlayerOccluded => PlayerHiddenAmount > 0f;
    // The ladder itself, for the overlay. Kept separate from the ring's rays: these
    // are unraised, because "am I behind something" has no terrace exemption, and
    // reading the ring's raised rays as though they were these is how the large latch
    // got misdiagnosed.
    public System.ReadOnlySpan<HiddenRay> PlayerHiddenRays => _hiddenRays;
    // Standing in, or right beside, a doorway or window — see ResolveAtOpening.
    public bool AtOpening { get; private set; }
    // Live ring reach, and how many samples were usable. A high blocked/opening
    // count means the numbers beside it came from a handful of samples.
    public float ProbeRange { get; private set; }
    public int VotingCount { get; private set; }
    public int OccludedCount { get; private set; }
    // Samples the player cannot see, and which therefore reported nothing. A high
    // count next to a building is the ring spending most of itself on the far side
    // of a wall — the reach is bigger than the space.
    public int HiddenCount { get; private set; }

    private Probe[] _probes = System.Array.Empty<Probe>();
    private int _probeCount;
    private Vector2[] _directions = System.Array.Empty<Vector2>();
    private int _directionCount;
    private readonly List<Roof> _nearbyRoofs = new();
    // Physics space for the occlusion rays, refreshed per tick from the camera.
    private PhysicsDirectSpaceState3D _space;
    // Reused rather than built per query — IntersectRay already allocates a native
    // Dictionary per call, and there is no reason to pay for the parameters too.
    // Environment ALONE: trees and props are PorousBody on Porous, and the cutaway
    // must never latch on foliage. See ECollisionLayer.
    private readonly PhysicsRayQueryParameters3D _rayQuery = new()
    {
        CollisionMask = (uint)ECollisionLayer.Environment,
        CollideWithAreas = false,
        CollideWithBodies = true,
    };
    private readonly HiddenRay[] _hiddenRays = new HiddenRay[PLAYER_HIDDEN_TESTS];
    // Whether the base plane was already cutting at or below the disk's plane last
    // tick, so leaving that state can open the disk at size instead of from nothing.
    private bool _irisRedundant;
    private Vector3 _irisCenter;
    private float _playerY;

    public System.ReadOnlySpan<Probe> Probes => new(_probes, 0, _probeCount);

    public void Tick(Sim sim, Vector3 playerPosition, GameCamera camera, float deltaSeconds)
    {
        _probeCount = 0;
        BaseClipY = NO_CEILING;
        PlayerHiddenAmount = 0f;
        AtOpening = false;
        VotingCount = 0;
        OccludedCount = 0;
        HiddenCount = 0;
        WorldState world = sim?.WorldState;
        if (world == null || camera == null)
        {
            return;
        }

        using var _prof = Profiler.Sample("ClipIris.Tick");

        ScreenRight = camera.GlobalBasis.X.Normalized();
        ScreenUp = camera.GlobalBasis.Y.Normalized();
        _space = camera.GetWorld3D()?.DirectSpaceState;
        _irisCenter = playerPosition;
        _playerY = playerPosition.Y;

        GatherRoofs(sim, playerPosition);
        PlayerFloorY = ResolvePlayerFloor(world, playerPosition);
        // The plateau boundary above the player's eyes. A pure function of their
        // elevation, so it holds perfectly still until they change floor.
        float step = Mathf.Max(GameCamera.PLATEAU_STEP, 1e-3f);
        IrisClipY = Mathf.Ceil((PlayerFloorY + GameCamera.EYE_HEIGHT) / step) * step - Clearance;

        AtOpening = ResolveAtOpening(world, playerPosition);

        BuildProbes(world, camera, playerPosition);
        BaseClipY = VoteCeiling();
        TickIris(deltaSeconds, camera);
    }

    // TWO sample sets, because the two questions want completely different reach.
    //
    // The CEILING block is the 3x3 of columns immediately around the player — the
    // space they are actually standing in. It has to be tight: the height is a max
    // over these, and a max over anything wider reaches into whatever is next
    // door. Sampling metres out put the plane above the roof of the house the
    // player was inside (so only the disk cut anything) and, where a far column
    // found a taller ceiling through a gap, put it INSIDE the roof slab, showing
    // the black middle of it.
    //
    // The OCCLUSION ring is wide and breathes, because "how far does the thing
    // hiding me extend" is a question about the surroundings by definition.
    private void BuildProbes(WorldState world, GameCamera camera, Vector3 playerPosition)
    {
        if (ProbeRange <= 0f)
        {
            ProbeRange = RadiusMin;
        }
        EnsureBuffers();
        BuildDirections();

        // Index 0 is the player's own column, at their actual position — the one
        // sample with a real body standing at it.
        AddProbe(world, camera, playerPosition, playerPosition, 0f);
        // How hidden the player is, asked as a LADDER up their body rather than a
        // single test. Each rung rises PlayerHiddenRise and casts to the camera; the
        // share that come back blocked is how far the reach eases from small to large.
        // The eye alone is a flag, and a flag makes the reach snap the instant a wall
        // edge crosses one point.
        //
        // Unraised, unlike the ring's samples. Those rise so short cover passes beneath
        // them and a terrace never latches, but applying that here answers a different
        // question — standing under a roof edge, a raised ray cleared it and called the
        // player visible, so the reach stayed small and only the tight iris opened.
        int blockedRungs = 0;
        for (int i = 0; i < PLAYER_HIDDEN_TESTS; i++)
        {
            Vector3 rung = playerPosition
                + Vector3.Up * (GameCamera.EYE_HEIGHT + i * Mathf.Max(PlayerHiddenRise, 0f));
            Vector3 target = CameraTarget(camera, rung);
            Vector3 point = target;
            bool blocked = _space != null && Raycast(rung, target, out point);
            if (blocked)
            {
                blockedRungs++;
            }
            _hiddenRays[i] = new HiddenRay { From = rung, Hit = point, Blocked = blocked };
        }
        PlayerHiddenAmount = (float)blockedRungs / PLAYER_HIDDEN_TESTS;

        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0)
                {
                    continue;
                }
                var at = new Vector3(playerPosition.X + dx, playerPosition.Y, playerPosition.Z + dz);
                AddProbe(world, camera, at, playerPosition, 1f);
            }
        }

        int rings = Mathf.Max(RingCount, 1);
        for (int r = 0; r < rings; r++)
        {
            float radius = ProbeRange * (r + 1) / rings;
            for (int i = 0; i < _directionCount; i++)
            {
                Vector2 dir = _directions[i];
                var at = new Vector3(
                    playerPosition.X + dir.X * radius,
                    playerPosition.Y,
                    playerPosition.Z + dir.Y * radius);
                AddProbe(world, camera, at, playerPosition, radius);
            }
        }
    }

    // The ceiling the ring agrees on: the HIGHEST plateau band with real support.
    //
    // Highest, not most common — biasing high is what stops a low corner of a
    // space flattening the whole thing, and the max is inherently steadier than a
    // median because it only moves when a taller ceiling enters the ring or the
    // current tallest leaves. No sub-voxel jitter, no swing at a threshold.
    //
    // TOO HIGH IS BETTER THAN TOO LOW, and that is not a preference — it is what
    // makes the raw max safe. If the plane lands above the roof the player is
    // actually under, they are by definition covered, so the camera cannot see
    // them, so the iris opens and reveals that space at head height. The iris is
    // the floor under this decision; the base plane only has to get the big
    // picture right. Cutting too low has no such backstop — it just flattens the
    // space.
    private float VoteCeiling()
    {
        // WHETHER to cut is the player's own column's decision, not the ring's.
        // Sky straight up means open air however much cave surrounds them —
        // standing under a hole in a cave roof reads as outdoors, which is what it
        // is. Letting the ring outvote that made the clip engage low whenever the
        // player stood in a shaft, a doorway of a courtyard, or a collapsed room,
        // because the surrounding thirty-odd samples always outnumber the one that
        // is actually overhead.
        //
        // The ring still decides the HEIGHT. It is only this gate that is a single
        // sample, and the camera's stability filter absorbs a frame or two of it
        // flicking as the player crosses the edge of the opening.
        if (_probeCount > 0 && _probes[0].Space == EProbeSpace.Sky)
        {
            return NO_CEILING;
        }

        float highest = float.NegativeInfinity;
        for (int i = 0; i < CEILING_SAMPLES && i < _probeCount; i++)
        {
            Probe probe = _probes[i];
            // Sky anywhere in the block means open air overhead — the hole in the
            // cave roof, the courtyard, the collapsed corner — and switches the
            // cut off entirely. Consistent with taking the max everywhere else:
            // sky is simply the highest answer there is.
            if (probe.Space == EProbeSpace.Sky)
            {
                return NO_CEILING;
            }
            // Blocked is a wall, Opening a doorway or window whose lintel and sill
            // are exactly the false ceilings this must not see, Oversail an eave
            // that is neither a room nor open sky. None of them vote.
            if (probe.Space != EProbeSpace.Open)
            {
                continue;
            }
            VotingCount++;
            highest = Mathf.Max(highest, probe.CeilingY);
        }
        if (VotingCount == 0)
        {
            return NO_CEILING;
        }
        // Just under the tallest ceiling in the block, NOT snapped to a plateau
        // boundary. Snapping up puts the plane above the ceiling and seals the
        // room — interiors founded on terrain sit off-grid (13, not 12) — and
        // snapping down cuts up to a whole plateau too deep. The max is already
        // stable across a block this small: every column of one room reports the
        // same ceiling.
        return highest - Clearance;
    }

    // The disk grows to cover the farthest sample the camera cannot see, and
    // shrinks back when they clear. Radius rather than a trigger: there is nothing
    // to latch and nothing to threshold, so it cannot chatter — an occluded sample
    // appearing at the ring's edge just extends the reach it eases toward.
    private void TickIris(float deltaSeconds, GameCamera camera)
    {
        float farthest = 0f;
        for (int i = 0; i < _probeCount; i++)
        {
            // Blocked samples are excluded from this: they never ran the visibility
            // march, so their Visible is "not applicable" rather than "no", and
            // folding them in would hide the number this is for — how much of the
            // ring is being spent on the far side of a wall.
            if (!_probes[i].Visible && _probes[i].Space != EProbeSpace.Blocked)
            {
                HiddenCount++;
            }
            if (!_probes[i].Occluded)
            {
                continue;
            }
            OccludedCount++;
            // A MAX, so it is set by exactly one sample and has no outlier rejection
            // at all. That is fine while the ring only sees the space the player is
            // walking into, and stops being fine the moment it reaches the next
            // building along: one sample occluded by THAT building sets the radius,
            // and since the disk is centred on the player it then removes everything
            // in between — a hole punched in a near building because a far one was
            // detected. The reach is what keeps this honest; see clipIrisRadiusMax.
            // If the reach ever has to grow again, this wants to become a high
            // quantile rather than a max.
            farthest = Mathf.Max(farthest, _probes[i].ScreenDistance);
        }

        // Nothing hidden closes it outright; anything hidden opens it to at least
        // the small size, since a disk narrower than that reads as a hole rather
        // than a reveal.
        float target = OccludedCount > 0
            ? Mathf.Clamp(farthest + Mathf.Max(IrisPadding, 0f), RadiusMin, RadiusMax)
            : 0f;
        // Standing in or beside a doorway or window opens it too, whatever the ring
        // found. MAX rather than an override: if the player is ALSO hidden and the
        // ring wants a wider disk than this, that is the better answer and it wins.
        if (AtOpening)
        {
            target = Mathf.Max(target, Mathf.Clamp(OpeningRadius, RadiusMin, RadiusMax));
        }
        // Redundant once the base has SETTLED at or below the disk's own plane —
        // there is nothing left for it to reveal that the base is not revealing
        // already — so drop it outright instead of leaving it running with its cap
        // plane and its per-frame prop sweep. Gated on the fade being complete:
        // the base and the disk straddle each other for the whole of a transition,
        // and switching the disk off in that window is exactly the flip that made
        // it pop out the moment the player stepped into a cave.
        bool redundant = camera.ClipSettled && IrisClipY >= camera.Clip;
        if (redundant)
        {
            IrisRadius = 0f;
        }
        else if (_irisRedundant)
        {
            // Leaving redundancy: a moment ago the base was cutting at or below the
            // disk's plane, so everything the disk would reveal was ALREADY open.
            // Growing from zero would animate a reveal of space the player has been
            // looking at the whole time, which reads as the disk arriving late. Open
            // at full size and let it ease from there.
            IrisRadius = target;
        }
        else
        {
            float seconds = Mathf.Max(target > IrisRadius ? IrisGrowSeconds : IrisShrinkSeconds, 1e-3f);
            IrisRadius = Mathf.Lerp(IrisRadius, target, 1f - Mathf.Exp(-deltaSeconds / seconds));
            // Land on zero rather than idling a hair above it forever, so the disk
            // actually switches off and the shaders drop its term.
            if (target <= 0f && IrisRadius < 0.05f)
            {
                IrisRadius = 0f;
            }
        }
        _irisRedundant = redundant;

        // Reach follows the same signal, between the same two sizes.
        float rangeTarget = Mathf.Lerp(RadiusMin, RadiusMax, PlayerHiddenAmount);
        ProbeRange = Mathf.Lerp(ProbeRange, rangeTarget,
            1f - Mathf.Exp(-deltaSeconds / Mathf.Max(ProbeRangeSeconds, 1e-3f)));
    }

    private void AddProbe(WorldState world, GameCamera camera, Vector3 at, Vector3 playerPosition, float radius)
    {
        int wx = Mathf.FloorToInt(at.X);
        int wz = Mathf.FloorToInt(at.Z);
        Vector3 delta = at - playerPosition;
        var probe = new Probe
        {
            ScreenDistance = new Vector2(delta.Dot(ScreenRight), delta.Dot(ScreenUp)).Length(),
            CeilingY = NO_CEILING,
        };

        // ONE climb, shared by both questions. The column's ground is found by
        // stepping up out of the terrain until the first air voxel; the ceiling
        // question then asks whether that ground is close enough to the player's
        // to be the same floor, and the occlusion question lifts from it.
        bool isPlayerColumn = radius <= 0f;
        int ground = ResolveColumnGround(world, wx, wz);
        // The player's own sample sits where they actually are — resolving it like
        // any other means a player in a doorway reports nothing about whether they
        // can be seen, which is when it matters most.
        probe.Point = isPlayerColumn
            ? playerPosition + Vector3.Up * BodyHeight
            : new Vector3(wx + 0.5f, ground + BodyHeight, wz + 0.5f);
        // Ground more than a step above the player's is a different floor — a
        // hillside, a rooftop, the far side of a wall — so it says nothing about
        // the ceiling of the space they are standing in. It still answers the
        // occlusion question, from its own ground.
        probe.Space = ground > PlayerFloorY + Mathf.Max(FloorTolerance, 0)
            ? EProbeSpace.Blocked
            : ScanColumn(world, wx, wz, ground, out probe.CeilingY);
        // A sample buried in a wall or a hillside is not a hidden PLACE — it is not a
        // place at all, so it answers neither query and neither march runs for it.
        // Cheapest cull available (it drops the 48-step camera march outright) and it
        // removes a spurious trigger with it: a Blocked sample inside a slope came
        // back "occluded", because of course solid rock hides it from the camera, and
        // dragged the disk open over otherwise clear ground.
        //
        // The player's own column is exempt. It is never legitimately Blocked, and it
        // is the one sample whose occlusion drives the reach, so it always marches.
        if (probe.Space == EProbeSpace.Blocked && !isPlayerColumn)
        {
            Append(probe);
            return;
        }

        // A sample the PLAYER cannot see says nothing about how far the reveal needs
        // to reach. The ring is placed by bearing and radius with no notion of
        // reachability, so on the far side of a wall it lands INSIDE the building —
        // where it is of course hidden from the camera, so it counted as occluded and
        // dragged the disk out until it covered the whole house, cutting into the
        // front of it. The camera query is what the disk is FOR; this is the question
        // of whether the sample is describing the player's space at all, and it has
        // to be asked first.
        probe.Visible = isPlayerColumn
            || IsVisibleFromPlayer(world, playerPosition + Vector3.Up * BodyHeight, probe.Point);
        probe.Occluded = probe.Visible && IsOccludedFromCamera(camera, probe.Point,
            out probe.OcclusionFrom, out probe.OcclusionHit);
        Append(probe);
    }

    // Walks up from this column's floor. Plain air all the way to the first
    // occupied voxel gives a real ceiling; an authored Opening anywhere on the way
    // disqualifies the column outright, because a doorway's lintel and a window's
    // head are the false ceilings the vote must not see.
    private EProbeSpace ScanColumn(WorldState world, int wx, int wz, int floorY, out float ceilingY)
    {
        ceilingY = NO_CEILING;
        int scanTop = floorY + Mathf.Max(CeilingScanHeight, 1);
        bool sawOversail = false;
        for (int wy = floorY; wy < scanTop; wy++)
        {
            if (world.GetVoxelWorld(wx, wy, wz) == VoxelType.Opening)
            {
                return EProbeSpace.Opening;
            }
            if (wy == floorY)
            {
                continue;
            }
            ECover cover = CoverAt(world, wx, wy, wz);
            if (cover == ECover.OversailOnly)
            {
                // Keep looking for a real ceiling above it, but remember that this
                // column is not open to the sky.
                sawOversail = true;
                continue;
            }
            if (cover == ECover.None)
            {
                continue;
            }
            // A voxel index y spans world [y, y+1), so its underside — the only
            // face visible from below — is at exactly y.
            ceilingY = wy;
            return EProbeSpace.Open;
        }
        if (sawOversail)
        {
            return EProbeSpace.Oversail;
        }
        // Reached open air — but a BROKEN roof punches its holes out of the sun stamp
        // (the shaft of light through the gap is the whole point of that), and CoverAt
        // gates on the stamp before it ever asks the roof. So a column under a hole
        // walks clear to the top and reports Sky, and Sky anywhere in the nine
        // switches the base cut off entirely: one decorative hole un-enclosing a
        // whole building.
        //
        // Ask the roof itself. Its footprint test knows nothing about holes, which is
        // exactly the "is there a structure overhead" answer wanted here — the sun
        // stamp answers a lighting question, and the two are not the same. Reported as
        // Oversail: covered, so it cannot switch the cut off, but voting on no height,
        // since a hole has no ceiling underside to read. The other columns supply it.
        return RoofOverColumn(wx, wz, floorY) ? EProbeSpace.Oversail : EProbeSpace.Sky;
    }

    // Does any gathered roof's footprint sit over this column at all, holes included?
    // Purely structural, unlike CoverAt, which is gated on sunlight.
    private bool RoofOverColumn(int wx, int wz, int y)
    {
        var point = new Vector3(wx + 0.5f, y + 0.5f, wz + 0.5f);
        for (int i = 0; i < _nearbyRoofs.Count; i++)
        {
            if (_nearbyRoofs[i].CoverAt(point) != ERoofCover.None)
            {
                return true;
            }
        }
        return false;
    }

    private void Append(in Probe probe)
    {
        if (_probeCount >= _probes.Length)
        {
            return;
        }
        _probes[_probeCount++] = probe;
    }

    // Standing in, or right beside, a doorway or window — asked DIRECTLY of the
    // voxels rather than read off the probe ring.
    //
    // The ring is placed by bearing and radius, so whether a window a metre away
    // happens to land on a sample is luck. It also only ever checked the nine centre
    // columns, so a window one step further out drew its magenta marker in the overlay
    // and still never latched. "Am I next to an opening" is a question about the
    // player's own surroundings and wants a direct answer.
    //
    // Deliberately independent of whether any ray gets THROUGH the opening — being
    // beside one is the whole condition. Uses OpeningReach, the same number that
    // decides whether the ring may see through one, so "next to" means one thing.
    //
    // Banded to the player's own body height, so a second-storey window overhead is
    // not "next to" anything.
    private bool ResolveAtOpening(WorldState world, Vector3 playerPosition)
    {
        int reach = Mathf.Max(Mathf.CeilToInt(OpeningReach), 0);
        int px = Mathf.FloorToInt(playerPosition.X);
        int pz = Mathf.FloorToInt(playerPosition.Z);
        int top = PlayerFloorY + Mathf.FloorToInt(GameCamera.EYE_HEIGHT);
        for (int dz = -reach; dz <= reach; dz++)
        {
            for (int dx = -reach; dx <= reach; dx++)
            {
                for (int wy = PlayerFloorY; wy <= top; wy++)
                {
                    if (world.GetVoxelWorld(px + dx, wy, pz + dz) == VoxelType.Opening)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
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
        // Swimming. Water is not solid, so the climb above stops at the feet —
        // which float well under the surface — and every height in this class
        // would then be measured from below the waterline. Climb out to the
        // surface so a swimmer resolves as if standing on it.
        for (int i = 0; i < MAX_SURFACE_CLIMB && world.GetVoxelWorld(wx, foot, wz) == VoxelType.Water; i++)
        {
            foot++;
        }
        return foot;
    }

    // This column's ground: step up out of the terrain from the player's floor
    // until the first air voxel.
    //
    // Only upward, deliberately. A column whose ground sits BELOW the player's — a
    // pit, the far lip of a ravine — reads as air immediately and resolves to the
    // player's own level, which is above its real ground. Erring that way costs
    // nothing: it only makes the occlusion lift more generous, and a too-high lift
    // can merely miss an occluder among thirty-odd samples. A too-low one invents
    // them, which is what made a gentle slope latch the disk.
    //
    // Bounded so it terminates inside a cliff; a column buried deeper resolves to
    // wherever it reached, still above the player and still clearing the terrain
    // they are standing in.
    private int ResolveColumnGround(WorldState world, int wx, int wz)
    {
        int ground = PlayerFloorY;
        for (int i = 0; i < MAX_SURFACE_CLIMB && IsCover(world, wx, ground, wz); i++)
        {
            ground++;
        }
        return ground;
    }

    // Roofs whose footprint could reach the ring. Looked up by chunk rather than
    // by walking every active entity: the dictionary is already keyed by chunk
    // coord, so a bounded neighbourhood costs a few dozen hash lookups instead of
    // a full sweep of the loaded world every frame.
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
    // roof away while the player is still outside standing under them.
    //
    // Cover no gathered roof recognises stays a ceiling. That means either a roof
    // further off than the search reaches or a source that is not a roof, and
    // reading it as open would stop a large hall cutting at all.
    private enum ECover
    {
        None,
        // A real ceiling: a solid voxel, or roof cover over the room it covers.
        Ceiling,
        // A roof's overhang and nothing else.
        OversailOnly,
    }

    private ECover CoverAt(WorldState world, int wx, int wy, int wz)
    {
        if (VoxelTypeInfo.IsSolid(world.GetVoxelWorld(wx, wy, wz)))
        {
            return ECover.Ceiling;
        }
        if (!world.GetSunOpaqueWorld(wx, wy, wz))
        {
            return ECover.None;
        }
        var point = new Vector3(wx + 0.5f, wy + 0.5f, wz + 0.5f);
        bool oversail = false;
        for (int i = 0; i < _nearbyRoofs.Count; i++)
        {
            switch (_nearbyRoofs[i].CoverAt(point))
            {
                case ERoofCover.Ceiling:
                    return ECover.Ceiling;
                case ERoofCover.Oversail:
                    oversail = true;
                    break;
            }
        }
        // Cover no gathered roof recognises stays a ceiling — either a roof
        // further off than the search reaches or a source that is not a roof, and
        // reading it as open would stop a large hall cutting at all.
        return oversail ? ECover.OversailOnly : ECover.Ceiling;
    }

    private bool IsCutawayOccupied(WorldState world, int wx, int wy, int wz)
    {
        return CoverAt(world, wx, wy, wz) == ECover.Ceiling;
    }

    // Anything that blocks SIGHT, oversail included — unlike the ceiling test, an
    // eave is not a room but it does genuinely hide someone standing under it.
    private static bool IsCover(WorldState world, int wx, int wy, int wz)
    {
        return VoxelTypeInfo.IsSolid(world.GetVoxelWorld(wx, wy, wz))
            || world.GetSunOpaqueWorld(wx, wy, wz);
    }

    // Straight march from the player to the sample, at body height on both ends.
    // Coarse, like the camera march, and for the same reason: it feeds a count of
    // samples rather than a per-pixel decision, so a missed one-voxel diagonal sliver
    // costs nothing.
    //
    // Oversail counts here as everywhere else — an eave you cannot see past is still
    // between you and whatever is behind it.
    private bool IsVisibleFromPlayer(WorldState world, Vector3 from, Vector3 to)
    {
        Vector3 delta = to - from;
        float distance = delta.Length();
        if (distance <= SIGHT_STEP)
        {
            return true;
        }
        Vector3 direction = delta / distance;
        int startX = Mathf.FloorToInt(from.X);
        int startY = Mathf.FloorToInt(from.Y);
        int startZ = Mathf.FloorToInt(from.Z);
        for (float t = SIGHT_STEP; t < distance; t += SIGHT_STEP)
        {
            Vector3 point = from + direction * t;
            int wx = Mathf.FloorToInt(point.X);
            int wy = Mathf.FloorToInt(point.Y);
            int wz = Mathf.FloorToInt(point.Z);
            // The player's own voxel can't block their view out of it — they are
            // standing in it, and in a low space it is shared with the ceiling.
            if (wx == startX && wy == startY && wz == startZ)
            {
                continue;
            }
            // A window or door is a hole, so nothing else here calls it cover — and
            // that let the ring pour through every opening in sight and sample the
            // room beyond, which is not space the player can see in any useful sense.
            // It stops the ring, but only at a DISTANCE: standing in a doorway or
            // right up against a window, seeing through it is the whole point, and
            // that is exactly the moment the reveal is for. From across the street
            // the same opening is a slot the ring has no business reaching through.
            if (world.GetVoxelWorld(wx, wy, wz) == VoxelType.Opening)
            {
                if (t > OpeningReach)
                {
                    return false;
                }
                continue;
            }
            if (IsCover(world, wx, wy, wz))
            {
                return false;
            }
        }
        return true;
    }

    // Is this sample hidden from the camera? Honest rays against the world's
    // COLLIDERS, and nothing proxying for them.
    //
    // Masked to Environment alone, which is this project's existing line between
    // structure and foliage: roofs, walls, doors and terrain sit there, while trees,
    // bushes, rocks and chests are PorousBody on Porous. That convention is already
    // load-bearing for perched vision and flight, and it is exactly the rule the
    // cutaway needs — standing behind a tree must never light the ring up.
    //
    // ASKED TWICE, FROM TWO HEIGHTS. A single raise trades one failure for the other:
    // raise far enough that a terrace passes under the ray and cover standing right
    // beside the player passes under it too; keep it low and every bank of earth
    // latches. So the low raise asks first, and its answer is believed whenever what
    // stopped it stands taller than ShortCoverHeight above the player's floor — that
    // is a building, and being close to it is exactly when it matters.
    //
    // Only when the blocker is SHORT is the question re-asked from the high raise. A
    // terrace the ray now passes over reports clear; a building goes on blocking from
    // up there too, so nothing tall is lost by asking again.
    //
    // Measured against the player's resolved floor rather than each sample's own
    // ground, so every sample judges "short" against the same elevation the player is
    // actually standing at.
    private bool IsOccludedFromCamera(GameCamera camera, Vector3 from,
        out Vector3 marchFrom, out Vector3 hit)
    {
        marchFrom = from;
        hit = from;
        if (_space == null)
        {
            return false;
        }
        if (!CastToCamera(camera, from, OcclusionLift, out marchFrom, out hit))
        {
            return false;
        }
        if (hit.Y > PlayerFloorY + ShortCover)
        {
            return true;
        }
        return CastToCamera(camera, from, OcclusionLiftHigh, out marchFrom, out hit);
    }

    // Rise, then look toward the camera. The rise is RAYCAST, so it stops at whatever
    // is actually overhead: assuming a fixed height punched the origin through any
    // roof thinner than the raise, and the ray then swept clear sky over a player
    // standing under solid rock.
    private bool CastToCamera(GameCamera camera, Vector3 from, float lift,
        out Vector3 marchFrom, out Vector3 hit)
    {
        Vector3 lifted = from + Vector3.Up * Mathf.Max(lift, 0f);
        if (Raycast(from, lifted, out Vector3 ceiling))
        {
            // Just under whatever is overhead, never below the body itself.
            lifted = new Vector3(from.X, Mathf.Max(ceiling.Y - LIFT_CLEARANCE, from.Y), from.Z);
        }
        marchFrom = lifted;
        Vector3 target = CameraTarget(camera, lifted);
        if (Raycast(lifted, target, out hit))
        {
            return true;
        }
        // Terminus, so the overlay can draw the ray that found nothing.
        hit = target;
        return false;
    }

    // Where a sight ray toward the camera ENDS. A perspective camera is a point, so
    // the ray stops there — running a fixed length along the direction instead
    // overshoots a near camera (and can hit whatever is behind it) while falling short
    // of a far one. Orthographic has no point to aim at, so the scan distance is the
    // only bound available.
    private Vector3 CameraTarget(GameCamera camera, Vector3 from)
    {
        if (camera.Projection == Camera3D.ProjectionType.Perspective)
        {
            return camera.GlobalPosition;
        }
        return from + camera.GlobalBasis.Z.Normalized() * Mathf.Max(OcclusionScanDistance, 1f);
    }

    private bool Raycast(Vector3 from, Vector3 to, out Vector3 hit)
    {
        hit = to;
        _rayQuery.From = from;
        _rayQuery.To = to;
        Godot.Collections.Dictionary result = _space.IntersectRay(_rayQuery);
        if (result.Count == 0)
        {
            return false;
        }
        hit = result["position"].AsVector3();
        return true;
    }

    // The clip height in force at a world point — the CPU twin of the shader's
    // resolve, for prop culling and the "can the player see this" gate.
    public float ClipHeightAt(Vector3 worldPosition)
    {
        if (!IrisActive)
        {
            return BaseClipY;
        }
        return InsideIris(worldPosition) ? Mathf.Min(IrisClipY, BaseClipY) : BaseClipY;
    }

    // Twin of clip_iris_shape_distance / clip_iris_inside, so prop culling and the
    // shader agree on the shape. Kept in step by hand: the aspect normalisation and
    // the noise field must match the include exactly or a prop near the boundary
    // vanishes on one side of the line and not the other.
    private bool InsideIris(Vector3 worldPosition)
    {
        Vector3 delta = worldPosition - _irisCenter;
        var screen = new Vector2(
            delta.Dot(ScreenRight) / Mathf.Max(ShapeAspect.X, 1e-3f),
            delta.Dot(ScreenUp) / Mathf.Max(ShapeAspect.Y, 1e-3f));
        float noise = Mathf.Sin(worldPosition.X * 1.3f + worldPosition.Z * 0.7f) * 0.3f
            + Mathf.Sin(worldPosition.X * 0.5f - worldPosition.Z * 1.7f) * 0.2f
            + Mathf.Sin(worldPosition.X * 2.1f + worldPosition.Y * 1.1f + worldPosition.Z * 0.4f) * 0.15f;
        return screen.Length() + noise <= IrisRadius;
    }

    private void EnsureBuffers()
    {
        int capacity = CEILING_SAMPLES + Mathf.Max(RingSampleCount, 0) * Mathf.Max(RingCount, 1);
        if (_probes.Length != capacity)
        {
            _probes = new Probe[capacity];
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

    private static int FloorDiv(int value, int divisor)
    {
        int q = value / divisor;
        return (value % divisor != 0 && (value < 0) != (divisor < 0)) ? q - 1 : q;
    }

    public string Describe()
    {
        string baseText = float.IsPositiveInfinity(BaseClipY) ? "none" : BaseClipY.ToString("0.0");
        return $"y={_playerY:0.0} floorY={PlayerFloorY} baseClip={baseText} iris={IrisClipY:0.0} "
            + $"radius={IrisRadius:0.0} range={ProbeRange:0.0} "
            + $"hidden={PlayerHiddenAmount:0.00} occluded={OccludedCount}/{_probeCount} "
            + $"hidden={HiddenCount} voting={VotingCount} atOpening={AtOpening}";
    }
}
