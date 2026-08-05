using System;
using Godot;

// Per-column ceiling cutaway. Replaces the single global clip scalar the upward
// raycast in GameCamera produced.
//
// ONE RULE: sample the vertical band from the player's floor elevation up to
// Headroom. A column whose band is entirely air is clipped above Headroom; every
// other column is kept whole. Plainly: anything above head height, over ground
// you could stand on, gets cut.
//
// The band is measured from the PLAYER's floor, not each column's own, so every
// column that clips cuts at the same Y — the height is one scalar (ClipY) and
// what varies per column is only whether it takes part. That is why this is a 2D
// mask and not a 3D classification: a balcony deck overhead has a clear band and
// goes, the wall holding it up is solid through the band and stays, and a window
// column is disqualified by its own sill before anything above it is considered,
// so facades survive with no lintel or spandrel detection anywhere.
//
// Thickness, run length and slab-vs-wall shape are deliberately not consulted.
// They fail on thick cave ceilings and stacked windows; the band test does not.
//
// World-anchored like VisibilityFan, and for the same reason: the per-cell
// smoothing has to mean the same patch of world from frame to frame, or a
// lagging cell reads as a different region every time the player moves.
public class ClipColumnMask
{
    // Cells per side of the window. One cell is one voxel column — the
    // resolution the occluders actually have. A buffer size the indexing and
    // upload depend on, not a tuning value; Radius is what bounds the reach.
    public const int GRID_SIZE = 64;
    // Value below which a cell counts as exempt, for deciding the whole mask is
    // shut and the shader term can be switched off.
    private const float CLOSED_EPSILON = 0.01f;
    // Share of the nominal per-frame travel the smoothing covers regardless of
    // how close it already is, so a closing mask lands on zero instead of idling
    // a hair above it forever. Same tail floor VisibilityFan uses.
    private const float TAIL_RATE_FRACTION = 0.15f;
    // Where a smoothed cell counts as "clipping" for the CPU-side queries (prop
    // culling, HUD gating). The GPU reads the fractional value directly.
    private const float BINARY_THRESHOLD = 0.5f;
    // How far below floor + Headroom the clip plane actually sits. A voxel index
    // y spans world [y, y+1), so a ceiling resting on the headroom boundary has
    // its underside at exactly that Y — and the shaders cut on `>`, so without
    // clearance that face (the only one you see from underneath) survives and the
    // cutaway reads as having done nothing. Parks the plane mid-air-gap instead.
    private const float CLIP_CLEARANCE = 0.5f;
    // Voxels the floor resolve will climb looking for air. The player's Y is
    // fractional on slopes and grade blocks and the capsule settles slightly into
    // what it stands on, so the feet voxel is often the solid underfoot.
    private const int MAX_FLOOR_CLIMB = 3;

    // Tuning, copied in by GameClient each tick from its [Export]s.
    public float Radius = 20f;
    public float Headroom = 3f;
    public float OpenSeconds = 0.35f;
    public float CloseSeconds = 0.25f;
    public int CoverScanHeight = 20;
    public int ArmingInterval = 6;
    // How far the region flood penetrates INTO blocked columns, in cells. The
    // walls bounding the player's air are what cut at the taller plane; this is
    // how thick a wall can be and still be carried, so it wants to cover the
    // thickest authored wall. 0 leaves walls exempt, i.e. the old behaviour.
    public int WallDepth = 2;
    // Height of the band the region flood travels through, in voxels — SHORTER
    // than Headroom on purpose. Connectivity and participation are different
    // questions: "can I get there" tolerates a duck, "should this column cut"
    // does not. Sharing one band severs a passage at every pinch and strands
    // everything past it, which is the opposite of what the region is for.
    public int ConnectHeight = 1;
    // Fraction of a mesh's footprint that must sit over the player's region
    // before it starts taking the cut; participation reaches full at twice this.
    public float FootprintCoverageThreshold = 0.15f;
    // How much higher a blocked column cuts than a clear one, mirroring the
    // camera_clip_wall_offset global so CPU-side consumers (prop culling, HUD
    // gating) resolve the same two-level surface the shaders do.
    public float WallOffset;

    // Smoothed mask (what the GPU sees) and this tick's raw target. Separate
    // because the clip iris animates HEIGHT changes only — a column crossing the
    // radius boundary as the player walks has nothing else to animate it, and
    // would pop in a single frame.
    private float[] _accumulated = new float[GRID_SIZE * GRID_SIZE];
    private float[] _scratch = new float[GRID_SIZE * GRID_SIZE];
    private readonly float[] _target = new float[GRID_SIZE * GRID_SIZE];
    // Second channel: 1 where the band is clear, 0 where blocked — which of the
    // two clip heights the column cuts at. Smoothed like participation so a
    // column changing class when the player's floor changes glides rather than
    // jumping its plane by the whole wall offset in a frame.
    private float[] _accumulatedClear = new float[GRID_SIZE * GRID_SIZE];
    private float[] _scratchClear = new float[GRID_SIZE * GRID_SIZE];
    private readonly float[] _targetClear = new float[GRID_SIZE * GRID_SIZE];

    // Per-tick scratch for the region flood. `_depth` is 0 for air in the
    // player's region, 1..WallDepth for the walls bounding it, NOT_IN_PLAY
    // otherwise.
    private const byte NOT_IN_PLAY = 255;
    private readonly bool[] _clear = new bool[GRID_SIZE * GRID_SIZE];
    // Clear over the shorter connectivity band — what the flood travels through.
    private readonly bool[] _connected = new bool[GRID_SIZE * GRID_SIZE];
    private readonly bool[] _inRadius = new bool[GRID_SIZE * GRID_SIZE];
    private readonly byte[] _depth = new byte[GRID_SIZE * GRID_SIZE];
    private readonly int[] _queue = new int[GRID_SIZE * GRID_SIZE];
    // Thresholded state, for the CPU queries and for detecting the frames on
    // which a static entity's visibility can actually have changed.
    private readonly bool[] _binary = new bool[GRID_SIZE * GRID_SIZE];
    // Two bytes per cell — RG8, participation then clearness.
    private readonly byte[] _bytes = new byte[GRID_SIZE * GRID_SIZE * 2];
    private ImageTexture _texture;

    // Window anchor in whole cells, this tick and last. Last tick's is kept
    // because a window that scrolled OFF an entity has to let it back — testing
    // only the current bounds would strand it hidden. Both are owned by Recenter.
    private int _minCellX;
    private int _minCellZ;
    private int _prevMinCellX;
    private int _prevMinCellZ;
    private bool _anchored;

    // Floor voxel index the band is measured from. Latched while grounded so a
    // jump doesn't lift the whole plane with the player.
    private int _floorY;
    private bool _hasFloor;
    private int _armingCountdown;

    // World Y everything in a participating column is cut above.
    public float ClipY { get; private set; } = float.PositiveInfinity;
    // Whether anything overhead is actually being cut. False on open ground,
    // where the rule self-nullifies — the band is clear but there is nothing
    // above to remove — so the caller can park the clip at infinity and leave
    // the whole cutaway (and its indoor-mode signal) switched off.
    public bool AnyClipped { get; private set; }
    // False once the mask has smoothed shut, so the caller can drop the shader
    // term and its texture fetch rather than sampling an empty field.
    public bool IsOpen { get; private set; }
    // True on a tick where any cell crossed BINARY_THRESHOLD or the window
    // scrolled. Prop culling uses it to skip static entities the rest of the time.
    public bool MaskChanged { get; private set; }
    // Size of the player's flooded region, and how many bounding wall cells it
    // carried. Diagnostics only — a region that collapses to a handful of cells
    // is the signature of a seed that landed somewhere it shouldn't.
    public int RegionCells { get; private set; }
    public int WallCells { get; private set; }

    // World XZ of the mask's (0,0) corner, and its total world size.
    public Vector2 OriginXz => new Vector2(_minCellX, _minCellZ);
    public float Extent => GRID_SIZE;
    public Texture2D Texture => _texture;

    // `active` false skips the voxel work entirely and drives the mask toward
    // exempt, so it closes on its authored curve instead of snapping — and costs
    // nothing at all once closed.
    public void Tick(bool active, WorldState world, Vector3 playerPosition, bool grounded, float deltaSeconds)
    {
        MaskChanged = false;
        if (!active && !IsOpen)
        {
            return;
        }

        using var _prof = Profiler.Sample("ClipColumnMask.Tick");

        // The plane never RISES except off solid ground, but it always follows
        // the player DOWN. Latching purely on `grounded` was meant to stop a jump
        // lifting it, and did — but it also froze it at the rim while falling into
        // a pit or swimming (neither is grounded), so the band got measured at the
        // rim elevation, every column around read as solid rock, and the cutaway
        // never armed at all down there.
        int resolved = ResolveFloor(world, playerPosition);
        if (grounded || !_hasFloor || resolved < _floorY)
        {
            _floorY = resolved;
            _hasFloor = true;
        }
        // Sits CLIP_CLEARANCE below the headroom boundary so a ceiling resting on
        // that boundary is cut rather than kept by its own underside.
        ClipY = _floorY + Headroom - CLIP_CLEARANCE;

        bool scrolled = Recenter(playerPosition);

        Array.Clear(_target, 0, _target.Length);
        Array.Clear(_targetClear, 0, _targetClear.Length);
        if (active && world != null)
        {
            RasterizeColumns(world);
        }

        bool anyOpen = false;
        bool changed = scrolled;
        for (int i = 0; i < _accumulated.Length; i++)
        {
            float target = _target[i];
            // Clearness rides the same curve; it is only ever read where
            // participation is non-zero, so its idle value doesn't matter.
            float clearConstant = Mathf.Max(_targetClear[i] > _accumulatedClear[i] ? OpenSeconds : CloseSeconds, 1e-3f);
            float clearEased = Mathf.Lerp(_accumulatedClear[i], _targetClear[i],
                1f - Mathf.Exp(-deltaSeconds / clearConstant));
            // Same tail floor participation gets, and for a sharper reason than
            // tidiness: an exponential settles at 0.998, not 1, and consumers
            // multiply this in. A roof then reads 0.998 participation, misses the
            // full-discard path, and dithers — leaving the one Bayer cell whose
            // threshold is exactly 0 alive, as a screen-space speckle that slides
            // with the camera over geometry that is supposed to be gone.
            _accumulatedClear[i] = Mathf.MoveToward(clearEased, _targetClear[i],
                (deltaSeconds / clearConstant) * TAIL_RATE_FRACTION);
            // Opening slower than closing, mirroring clipFadeDownSeconds /
            // clipFadeUpSeconds: the reveal is worth watching, the close is not.
            float timeConstant = Mathf.Max(target > _accumulated[i] ? OpenSeconds : CloseSeconds, 1e-3f);
            float blend = 1f - Mathf.Exp(-deltaSeconds / timeConstant);
            float eased = Mathf.Lerp(_accumulated[i], target, blend);
            float minStep = (deltaSeconds / timeConstant) * TAIL_RATE_FRACTION;
            _accumulated[i] = Mathf.MoveToward(eased, target, minStep);
            if (_accumulated[i] > CLOSED_EPSILON)
            {
                anyOpen = true;
            }
            bool binary = _accumulated[i] > BINARY_THRESHOLD;
            if (binary != _binary[i])
            {
                // On a scrolled tick the stale entries these compare against
                // describe a different patch of world, but `changed` is already
                // true and every entry is rewritten here, so the mismatch costs
                // nothing beyond the compare.
                _binary[i] = binary;
                changed = true;
            }
        }

        IsOpen = anyOpen;
        MaskChanged = changed;
        if (!anyOpen)
        {
            AnyClipped = false;
            return;
        }
        Upload();
    }

    // Lowest AIR voxel at or above the player's feet — the level the band is
    // measured from. Not simply floor(playerY): the capsule settles into what it
    // stands on and grade blocks put the feet inside a solid voxel, and taking
    // that as the band's base makes the floor itself the blocker. That
    // disqualifies every column at that ground level at once, so the cutaway
    // never arms at all rather than failing somewhere visible.
    private int ResolveFloor(WorldState world, Vector3 playerPosition)
    {
        int foot = Mathf.FloorToInt(playerPosition.Y);
        if (world == null)
        {
            return foot;
        }
        int wx = Mathf.FloorToInt(playerPosition.X);
        int wz = Mathf.FloorToInt(playerPosition.Z);
        for (int i = 0; i < MAX_FLOOR_CLIMB && VoxelTypeInfo.IsSolid(world.GetVoxelWorld(wx, foot, wz)); i++)
        {
            foot++;
        }
        return foot;
    }

    // Moves the window onto the player in whole cells, carrying accumulated
    // cells with it so each keeps describing the same column. Cells scrolling in
    // from the edge start exempt and smooth up, which reads correctly — a roof
    // that just came into range was not cut a moment ago.
    private bool Recenter(Vector3 playerPosition)
    {
        int half = GRID_SIZE / 2;
        int newMinX = Mathf.FloorToInt(playerPosition.X) - half;
        int newMinZ = Mathf.FloorToInt(playerPosition.Z) - half;
        if (!_anchored)
        {
            Array.Clear(_accumulated, 0, _accumulated.Length);
            Array.Clear(_accumulatedClear, 0, _accumulatedClear.Length);
            _minCellX = _prevMinCellX = newMinX;
            _minCellZ = _prevMinCellZ = newMinZ;
            _anchored = true;
            return true;
        }

        _prevMinCellX = _minCellX;
        _prevMinCellZ = _minCellZ;
        int dx = newMinX - _minCellX;
        int dz = newMinZ - _minCellZ;
        if (dx == 0 && dz == 0)
        {
            return false;
        }
        if (Mathf.Abs(dx) >= GRID_SIZE || Mathf.Abs(dz) >= GRID_SIZE)
        {
            Array.Clear(_accumulated, 0, _accumulated.Length);
            Array.Clear(_accumulatedClear, 0, _accumulatedClear.Length);
            _minCellX = newMinX;
            _minCellZ = newMinZ;
            return true;
        }

        Scroll(ref _accumulated, ref _scratch, dx, dz);
        Scroll(ref _accumulatedClear, ref _scratchClear, dx, dz);
        _minCellX = newMinX;
        _minCellZ = newMinZ;
        return true;
    }

    // Destination cell (gx, gz) holds the world column that lived at
    // (gx + dx, gz + dz) in the old window. Ping-pong rather than shifting in
    // place so overlapping ranges can't clobber themselves.
    private static void Scroll(ref float[] values, ref float[] scratch, int dx, int dz)
    {
        Array.Clear(scratch, 0, scratch.Length);
        int copyWidth = GRID_SIZE - Mathf.Abs(dx);
        int dstX = Mathf.Max(0, -dx);
        int srcX = dstX + dx;
        for (int gz = 0; gz < GRID_SIZE; gz++)
        {
            int srcZ = gz + dz;
            if (srcZ < 0 || srcZ >= GRID_SIZE)
            {
                continue;
            }
            Array.Copy(values, srcZ * GRID_SIZE + srcX, scratch, gz * GRID_SIZE + dstX, copyWidth);
        }
        (values, scratch) = (scratch, values);
    }

    // The column test itself, over every cell inside the radius bound. Stage 1
    // cuts hard at the boundary — no spatial feathering — so the mask stair-steps
    // at column resolution and its edge travels with the player.
    private void RasterizeColumns(WorldState world)
    {
        // Clamped so the reach can never exceed the buffer it is rasterized into.
        float radius = Mathf.Min(Mathf.Max(Radius, 1f), GRID_SIZE / 2 - 1);
        float radiusSq = radius * radius;
        int centerX = _minCellX + GRID_SIZE / 2;
        int centerZ = _minCellZ + GRID_SIZE / 2;
        int span = Mathf.CeilToInt(radius);

        int bandLow = _floorY;
        int bandHigh = _floorY + Mathf.FloorToInt(Headroom);
        // First voxel entirely above the clip plane — where cover has to start to
        // count, since anything straddling the plane is being cut already.
        int clipVoxelY = Mathf.CeilToInt(ClipY);

        // The arming scan answers a far slower question than the mask does — is
        // there anything overhead worth cutting — so it runs on its own cadence
        // rather than every frame with the band test.
        bool runArming = --_armingCountdown <= 0;
        bool anyCover = false;
        if (runArming)
        {
            _armingCountdown = Mathf.Max(ArmingInterval, 1);
        }

        int connectHigh = bandLow + Mathf.Max(1, ConnectHeight);
        Array.Clear(_clear, 0, _clear.Length);
        Array.Clear(_connected, 0, _connected.Length);
        Array.Clear(_inRadius, 0, _inRadius.Length);
        for (int dz = -span; dz <= span; dz++)
        {
            int wz = centerZ + dz;
            for (int dx = -span; dx <= span; dx++)
            {
                if (dx * dx + dz * dz > radiusSq)
                {
                    continue;
                }
                int wx = centerX + dx;
                int index = (wz - _minCellZ) * GRID_SIZE + (wx - _minCellX);
                _inRadius[index] = true;
                _clear[index] = IsBandClear(world, wx, wz, bandLow, bandHigh);
                // A column clear to head height is necessarily clear to the
                // shorter connectivity height, so only pinches cost the extra
                // scan.
                _connected[index] = _clear[index] || IsBandClear(world, wx, wz, bandLow, connectHigh);
            }
        }

        // Participation is a property of the REGION, not the column. Without this
        // the band test is purely elevation-based, so a tunnel bored through a
        // hill at the player's elevation is indistinguishable from the ground they
        // are standing on and the hilltop cuts away to reveal it.
        FloodRegion();

        for (int dz = -span; dz <= span; dz++)
        {
            int wz = centerZ + dz;
            for (int dx = -span; dx <= span; dx++)
            {
                if (dx * dx + dz * dz > radiusSq)
                {
                    continue;
                }
                int wx = centerX + dx;
                int index = (wz - _minCellZ) * GRID_SIZE + (wx - _minCellX);
                // Clearness is geometry, so it is published for every column in
                // range whether or not it is in play — a column fading out still
                // needs the right plane on the way down.
                _targetClear[index] = _clear[index] ? 1f : 0f;
                if (_depth[index] == NOT_IN_PLAY)
                {
                    continue;
                }
                _target[index] = 1f;
                if (runArming && !anyCover && _clear[index] && HasCoverAbove(world, wx, wz, clipVoxelY))
                {
                    anyCover = true;
                }
            }
        }

        if (runArming)
        {
            AnyClipped = anyCover;
        }
    }

    // Flood the air region the player is standing in, then carry the walls that
    // bound it a few cells deep. Air the player has no route to never
    // participates, which is what stops a hill's tunnel cutting the hill and what
    // makes a house interior its own region — its roof holds from the street and
    // goes when you step through the door.
    //
    // Travels over _connected (the short band), NOT _clear (the headroom band).
    // A pinch is still part of the passage: it joins the region, and its own
    // blocked headroom makes it cut at the taller wall plane, so it survives as a
    // lump rather than severing everything past it.
    //
    // The radius survives as a clamp on this flood, but only as a performance
    // bound: it no longer expresses anything about what should or should not cut.
    private void FloodRegion()
    {
        Array.Fill(_depth, NOT_IN_PLAY);
        RegionCells = 0;
        WallCells = 0;
        int seed = ResolveSeed();
        if (seed < 0)
        {
            return;
        }

        int maxWall = Mathf.Max(0, WallDepth);
        int head = 0;
        int tail = 0;
        _depth[seed] = 0;
        _queue[tail++] = seed;
        RegionCells = 1;
        while (head < tail)
        {
            int index = _queue[head++];
            int depth = _depth[index];
            int gx = index % GRID_SIZE;
            int gz = index / GRID_SIZE;
            for (int side = 0; side < 4; side++)
            {
                int nx = gx + (side == 0 ? 1 : side == 1 ? -1 : 0);
                int nz = gz + (side == 2 ? 1 : side == 3 ? -1 : 0);
                if (nx < 0 || nz < 0 || nx >= GRID_SIZE || nz >= GRID_SIZE)
                {
                    continue;
                }
                int neighbour = nz * GRID_SIZE + nx;
                if (!_inRadius[neighbour] || _depth[neighbour] != NOT_IN_PLAY)
                {
                    continue;
                }
                if (_connected[neighbour])
                {
                    // A wall cell never spreads back out into air, or a region
                    // would leak through any wall thinner than WallDepth and take
                    // the next room with it.
                    if (depth != 0)
                    {
                        continue;
                    }
                    _depth[neighbour] = 0;
                    RegionCells++;
                }
                else
                {
                    int wallDepth = depth + 1;
                    if (wallDepth > maxWall)
                    {
                        continue;
                    }
                    _depth[neighbour] = (byte)wallDepth;
                    WallCells++;
                }
                _queue[tail++] = neighbour;
            }
        }
    }

    // The player's own cell, or the nearest clear one to it. Standing in a
    // doorway puts them on an authored Opening column, which reads blocked — and
    // seeding a blocked cell would find no region at all and switch the whole
    // cutaway off exactly when walking through a door.
    private int ResolveSeed()
    {
        const int SeedSearchRadius = 3;
        int cx = GRID_SIZE / 2;
        int cz = GRID_SIZE / 2;
        for (int ring = 0; ring <= SeedSearchRadius; ring++)
        {
            for (int dz = -ring; dz <= ring; dz++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    // Only the ring's own perimeter; inner cells were tested by
                    // an earlier, nearer pass.
                    if (ring > 0 && Mathf.Abs(dx) != ring && Mathf.Abs(dz) != ring)
                    {
                        continue;
                    }
                    int index = (cz + dz) * GRID_SIZE + (cx + dx);
                    if (_inRadius[index] && _connected[index])
                    {
                        return index;
                    }
                }
            }
        }
        return -1;
    }

    // Solidity is read with the LIGHTING opacity test (plain IsSolid, Barrier
    // included) rather than the mesher's. A closed door stamps Barrier into its
    // doorway so an occluder with no geometry still blocks sunlight, and a door
    // header has to disqualify its column the same way a solid one does.
    //
    // VoxelType.Opening is the authored escape hatch, and this is the ONLY place
    // it means anything: the void of a doorway or window reads as wall here, so
    // the wall above it is never cut into a slot. It cannot be inferred — a
    // 2-voxel doorway and a 2-voxel tunnel are the same column profile and the
    // same footprint — and it is scoped to the opening's own CELLS, not its
    // column, so an upper-storey window still cuts away with its wall when the
    // band is being sampled a floor below it.
    private static bool IsBandClear(WorldState world, int wx, int wz, int bandLow, int bandHigh)
    {
        for (int wy = bandLow; wy < bandHigh; wy++)
        {
            VoxelType voxel = world.GetVoxelWorld(wx, wy, wz);
            if (VoxelTypeInfo.IsSolid(voxel) || voxel == VoxelType.Opening)
            {
                return false;
            }
        }
        return true;
    }

    // Is there real cover above the clip plane in this column? Only decides
    // whether the cutaway engages at all, never which columns take part.
    //
    // SkyExposure is the maintained "is there cover straight up" field, so a
    // non-zero reading is open sky and no scan can contradict it — that is what
    // keeps the common outdoor case at one read per column. It counts CANOPY as
    // cover though, so a positive reading is confirmed against real solids
    // before arming; foliage must never engage the cutaway.
    private bool HasCoverAbove(WorldState world, int wx, int wz, int clipVoxelY)
    {
        if (world.GetSkyExposureWorld(wx, clipVoxelY, wz) != 0)
        {
            return false;
        }
        int top = clipVoxelY + CoverScanHeight;
        for (int wy = clipVoxelY; wy < top; wy++)
        {
            // GetSunOpaqueWorld covers the cover that isn't voxels — an authored
            // roof mesh, which the clip shaders cut exactly like a stone ceiling.
            if (VoxelTypeInfo.IsSolid(world.GetVoxelWorld(wx, wy, wz)) || world.GetSunOpaqueWorld(wx, wy, wz))
            {
                return true;
            }
        }
        return false;
    }

    // The clip height that applies in this column, given the caller's base (clear)
    // height: the same two-level step the shader resolves, so CPU-side consumers
    // agree with what is drawn. Infinite base stays infinite.
    public float ClipHeightAt(Vector3 worldPosition, float clearClipY)
    {
        int gx = Mathf.FloorToInt(worldPosition.X) - _minCellX;
        int gz = Mathf.FloorToInt(worldPosition.Z) - _minCellZ;
        if (gx < 0 || gz < 0 || gx >= GRID_SIZE || gz >= GRID_SIZE)
        {
            return clearClipY;
        }
        return clearClipY + WallOffset * (1f - _accumulatedClear[gz * GRID_SIZE + gx]);
    }

    // How much of a mesh's footprint sits over the player's own air region, as a
    // participation value for the whole instance.
    //
    // Supersedes sampling one point. A single sample has to pick a point, and
    // every choice is arbitrary for a mesh spanning tens of metres: the origin
    // read whatever was under the middle of the building, and the nearest
    // footprint point reads the rect's BOUNDARY, which is not the wall line — a
    // footprint painted a little larger than its building clamps to open street,
    // which is the player's region, so the roof cut from outside and stopped
    // cutting a step later when the clamp landed on the wall instead.
    //
    // Coverage has no such point to be wrong about. Standing outside, the street
    // region does not extend under the roof at all (its walls stop the flood), so
    // coverage is zero however the footprint is drawn; inside, the room under the
    // roof IS the region. The threshold is low and ramps to double itself, so a
    // roof spanning two rooms still cuts fully from either one.
    public float RegionCoverage(Vector2 minXz, Vector2 maxXz)
    {
        int gx0 = Mathf.FloorToInt(minXz.X) - _minCellX;
        int gz0 = Mathf.FloorToInt(minXz.Y) - _minCellZ;
        int gx1 = Mathf.FloorToInt(maxXz.X) - _minCellX;
        int gz1 = Mathf.FloorToInt(maxXz.Y) - _minCellZ;
        // Total spans the whole footprint, including any part outside the window —
        // a roof mostly beyond the mask's reach should read as mostly uncovered
        // rather than being judged on the sliver that happens to be in range.
        int total = (gx1 - gx0 + 1) * (gz1 - gz0 + 1);
        if (total <= 0)
        {
            return 0f;
        }
        gx0 = Mathf.Max(gx0, 0);
        gz0 = Mathf.Max(gz0, 0);
        gx1 = Mathf.Min(gx1, GRID_SIZE - 1);
        gz1 = Mathf.Min(gz1, GRID_SIZE - 1);

        float covered = 0f;
        for (int gz = gz0; gz <= gz1; gz++)
        {
            int row = gz * GRID_SIZE;
            for (int gx = gx0; gx <= gx1; gx++)
            {
                covered += _accumulated[row + gx] * _accumulatedClear[row + gx];
            }
        }
        float coverage = covered / total;
        float threshold = Mathf.Max(FootprintCoverageThreshold, 1e-3f);
        return Mathf.Clamp((coverage - threshold) / threshold, 0f, 1f);
    }

    // Does the cutaway remove geometry standing above ClipY in this column?
    // The CPU-side counterpart of the shader's mask sample, for prop culling and
    // HUD gating. Outside the window reads exempt, matching the shader.
    public bool IsClipped(Vector3 worldPosition)
    {
        int gx = Mathf.FloorToInt(worldPosition.X) - _minCellX;
        int gz = Mathf.FloorToInt(worldPosition.Z) - _minCellZ;
        if (gx < 0 || gz < 0 || gx >= GRID_SIZE || gz >= GRID_SIZE)
        {
            return false;
        }
        return _binary[gz * GRID_SIZE + gx];
    }

    // Can the mask have changed anything in this chunk's XZ footprint? Spans this
    // tick's window and last tick's, so a chunk the window just left is still
    // swept once and its props restored.
    public bool WindowTouchesChunk(Vector3I chunkCoord)
    {
        int minX = Mathf.Min(_minCellX, _prevMinCellX);
        int maxX = Mathf.Max(_minCellX, _prevMinCellX) + GRID_SIZE;
        int minZ = Mathf.Min(_minCellZ, _prevMinCellZ);
        int maxZ = Mathf.Max(_minCellZ, _prevMinCellZ) + GRID_SIZE;
        int chunkMinX = chunkCoord.X * ChunkState.SIZE;
        int chunkMinZ = chunkCoord.Z * ChunkState.SIZE;
        return chunkMinX + ChunkState.SIZE > minX && chunkMinX < maxX
            && chunkMinZ + ChunkState.SIZE > minZ && chunkMinZ < maxZ;
    }

    // One-line state dump for the clip_column_debug cvar: what the rule decided
    // at the player's own column, and why. Reports the band it tested, the first
    // solid above the clip plane that armed it (or the lack of one), and the
    // world Y of the surface that clip plane is supposed to cut — the value to
    // compare against ClipY when the cutaway looks like it did nothing.
    public string Describe(WorldState world, Vector3 playerPosition)
    {
        int wx = Mathf.FloorToInt(playerPosition.X);
        int wz = Mathf.FloorToInt(playerPosition.Z);
        int bandLow = _floorY;
        int bandHigh = _floorY + Mathf.FloorToInt(Headroom);
        int clipVoxelY = Mathf.CeilToInt(ClipY);

        int bandBlocker = int.MinValue;
        for (int wy = bandLow; wy < bandHigh && bandBlocker == int.MinValue; wy++)
        {
            if (VoxelTypeInfo.IsSolid(world.GetVoxelWorld(wx, wy, wz)))
            {
                bandBlocker = wy;
            }
        }
        int coverY = int.MinValue;
        for (int wy = clipVoxelY; wy < clipVoxelY + CoverScanHeight && coverY == int.MinValue; wy++)
        {
            if (VoxelTypeInfo.IsSolid(world.GetVoxelWorld(wx, wy, wz)) || world.GetSunOpaqueWorld(wx, wy, wz))
            {
                coverY = wy;
            }
        }

        return $"floorY={_floorY} clipY={ClipY} wallY={ClipY + WallOffset} band=[{bandLow},{bandHigh}) "
            + $"bandBlocker={(bandBlocker == int.MinValue ? "none" : bandBlocker.ToString())} "
            + $"sky@clip={world.GetSkyExposureWorld(wx, clipVoxelY, wz)} "
            + $"cover={(coverY == int.MinValue ? "none" : coverY.ToString())} "
            + $"region={RegionCells} walls={WallCells} "
            + $"armed={AnyClipped} open={IsOpen} maskHere={IsClipped(playerPosition)}";
    }

    private void Upload()
    {
        for (int i = 0; i < _accumulated.Length; i++)
        {
            _bytes[i * 2] = (byte)Mathf.Clamp(Mathf.RoundToInt(_accumulated[i] * 255f), 0, 255);
            _bytes[i * 2 + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt(_accumulatedClear[i] * 255f), 0, 255);
        }
        Image image = Image.CreateFromData(GRID_SIZE, GRID_SIZE, false, Image.Format.Rg8, _bytes);
        if (_texture == null)
        {
            _texture = ImageTexture.CreateFromImage(image);
            // The ImageTexture instance is stable across updates, so the global
            // only needs binding once.
            RenderingServer.GlobalShaderParameterSet("clip_mask_tex", _texture);
        }
        else
        {
            _texture.Update(image);
        }
    }
}
