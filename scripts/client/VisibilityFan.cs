using System;
using Godot;

// Player-centered horizontal visibility field, accumulated into a WORLD-SPACE
// grid. The clip shaders use it to remove exactly the geometry standing between
// the camera and ground the player can see.
//
// The grid is only half the answer. A fragment is not tested at its own
// position — it is projected forward along the view ray to the sight plane
// first, and the LANDING point is tested (see visibility_fan_cut in
// clip_dither.gdshaderinc). That is what makes the removed set "what occludes
// the visible ground" rather than "what happens to sit above it", and it is why
// there is no near-field term: a building between camera and player is culled
// through its full thickness because every part of it projects onto ground the
// player can see, however thick it is.
//
// The same construction is self-limiting in the other direction. Geometry that
// occludes nothing projects past the field and survives — so a wall on the far
// side of the player, or a distant ridgeline, is never touched, and the effect
// runs unconditionally without carving a bubble around the player.
//
// WHY A WORLD GRID AND NOT A RADIAL r(theta) STRIP: the temporal smoothing has
// to be anchored to the world. A strip indexed by bearing is implicitly
// anchored to the PLAYER, so a lagging radius gets reinterpreted from a new
// origin every frame — the stale shape is not "where I could see a moment ago",
// it is a different region entirely, and it visibly wobbles and breathes as the
// player walks. Accumulating per world cell makes a lagging cell mean exactly
// one thing regardless of where the player has moved to, so the lag reads as
// the reveal trailing smoothly behind. Same reason the volume maps and the
// ground-stain projector are world-anchored windows. It also drops the atan2
// and the radial compare out of the fragment path.
//
// Occlusion is read off the voxel grid with the LIGHTING opacity test (plain
// IsSolid, Barrier included), not the mesher's. A closed door stamps
// VoxelType.Barrier into its doorway (Door.ApplyOcclusion) specifically so an
// occluder with no geometry still blocks sunlight, and sight wants that same
// set. The mesher-side test used across WorldGen — `IsSolid(v) && v != Barrier`
// — would see straight through shut doors.
public class VisibilityFan
{
    // Bearings cast per tick. Must stay dense enough that neighbouring rays
    // land in overlapping grid cells at MaxRadius, or the far field speckles.
    // Guaranteed for every radius: ray spacing is r*TAU/RAY_COUNT and cell size
    // is 2*(r+1)/GRID_SIZE, and the first is smaller for all r > 0 at these
    // constants.
    private const int RAY_COUNT = 512;
    // Cells per side of the accumulation window. A buffer size the indexing and
    // upload depend on, not a tuning value — the window's world SIZE tracks
    // MaxRadius, so this sets resolution rather than reach.
    private const int GRID_SIZE = 96;
    // Value below which a cell counts as dark, for deciding the whole field is
    // shut and the shader term can be switched off.
    private const float CLOSED_EPSILON = 0.01f;
    // Share of the nominal per-frame travel that the smoothing is guaranteed to
    // cover regardless of how close it already is. Small enough to leave the
    // exponential's shape intact through the visible part of the motion.
    private const float TAIL_RATE_FRACTION = 0.15f;

    // Tuning, copied in by GameClient each tick from its [Export]s.
    public float MaxRadius = 12f;
    public float OpenSeconds = 0.35f;
    public float CloseSeconds = 0.5f;
    // Metres of ramp inside the boundary. Written on the CPU while rasterizing,
    // rather than as a shader-side smoothstep, because the boundary is no longer
    // a radius the fragment can compare against.
    public float EdgeSoftness = 0.6f;
    // Pulls the boundary back from whatever a ray hit, so the soft edge lands ON
    // the wall face rather than biting through it. Without it the ramp reaches
    // past the hit and shaves the top off the near face of every surface the
    // field stops against. Applied only to a real hit — a ray that runs clear
    // has no surface to sit behind.
    public float RaySetback = 0.5f;
    // Height band above the player's FEET that counts as eye level, in metres.
    // A column blocks sight only when it is solid across the whole band, so a
    // window opening anywhere in it reads as see-through.
    //
    // Sampling one slice does not work: a single FloorToInt lands on one 1m
    // voxel cell, and windows are authored roughly feet+1 to feet+2, so a slice
    // taken at feet+2 sits in the wall ABOVE the opening and the window never
    // registers. Whether any given window is caught then depends on where the
    // player's feet sit inside their own voxel, which is not a behavior anyone
    // can author against.
    public float SightLow = 1f;
    public float SightHigh = 2f;

    // Unit XZ direction per ray. Built once so nothing calls trig per frame.
    private static readonly Vector2[] RayDirections = BuildRayDirections();

    // Smoothed field (what the GPU sees) and this tick's raw target. Separate
    // because the smoothing is what turns a corner-rounding jump into a reveal
    // sweeping down the new sightline — and it low-passes a single frame of bad
    // ray output into a few percent of movement rather than a punched hole.
    private float[] _accumulated = new float[GRID_SIZE * GRID_SIZE];
    private float[] _scratch = new float[GRID_SIZE * GRID_SIZE];
    private readonly float[] _target = new float[GRID_SIZE * GRID_SIZE];
    private readonly byte[] _bytes = new byte[GRID_SIZE * GRID_SIZE];
    private ImageTexture _texture;

    // Window anchor, in whole cells of world space so the grid never slides
    // sub-cell — that is what keeps an accumulated cell meaning the same patch
    // of world from frame to frame.
    private int _minCellX;
    private int _minCellZ;
    private float _cellSize = 1f;
    private bool _anchored;

    // Local voxel occupancy, rebuilt per tick and indexed by the ray march. Flat
    // bool array so the march never touches the chunk dictionary.
    private bool[] _occupied;
    private int _occupiedSize;
    private int _occupiedMinX;
    private int _occupiedMinZ;

    // World Y of the surface view rays are projected onto (the player's eye
    // level). Mid-band, where the rays conceptually live.
    public float SightPlaneY { get; private set; }
    // World XZ of the grid's (0,0) corner, and the window's total world size.
    public Vector2 OriginXz => new Vector2(_minCellX * _cellSize, _minCellZ * _cellSize);
    public float Extent => GRID_SIZE * _cellSize;

    // False once the whole field has smoothed dark, so the caller can drop the
    // shader term (and its texture fetch) rather than sampling an empty field.
    public bool IsOpen { get; private set; }

    public Texture2D Texture => _texture;

    // `active` false skips the ray work entirely and drives the field toward
    // dark, so it closes on its authored curve instead of snapping — and costs
    // nothing at all once closed.
    public void Tick(bool active, WorldState world, Vector3 playerPosition, float deltaSeconds)
    {
        // Idle and already shut: nothing to smooth, nothing to upload. Keeps the
        // subsystem at literally zero cost whenever the feature is switched off.
        if (!active && !IsOpen)
        {
            return;
        }

        using var _prof = Profiler.Sample("VisibilityFan.Tick");

        SightPlaneY = playerPosition.Y + (SightLow + SightHigh) * 0.5f;
        // Window spans the full reach in every direction, plus a cell of margin
        // so a ray can never need a cell outside it.
        float cellSize = 2f * (Mathf.Max(MaxRadius, 0.01f) + 1f) / GRID_SIZE;
        Recenter(playerPosition, cellSize);

        Array.Clear(_target, 0, _target.Length);
        if (active && world != null)
        {
            BuildOccupancy(world, playerPosition);
            RasterizeVisibility(playerPosition);
        }

        bool anyOpen = false;
        for (int i = 0; i < _accumulated.Length; i++)
        {
            float target = _target[i];
            // Opening slower than closing: the reveal is worth watching, the
            // close is not. Mirrors clipFadeDownSeconds / clipFadeUpSeconds.
            float timeConstant = Mathf.Max(target > _accumulated[i] ? OpenSeconds : CloseSeconds, 1e-3f);
            float blend = 1f - Mathf.Exp(-deltaSeconds / timeConstant);
            float eased = Mathf.Lerp(_accumulated[i], target, blend);
            // Exponential smoothing has an infinite tail. Floor the absolute
            // rate so a closing field actually LANDS on dark and the subsystem
            // can switch itself off, instead of idling a hair above zero — and
            // still uploading — for several seconds after each use.
            float minStep = (deltaSeconds / timeConstant) * TAIL_RATE_FRACTION;
            _accumulated[i] = Mathf.MoveToward(eased, target, minStep);
            if (_accumulated[i] > CLOSED_EPSILON)
            {
                anyOpen = true;
            }
        }

        IsOpen = anyOpen;
        if (anyOpen)
        {
            Upload();
        }
    }

    // Moves the window to sit on the player, carrying accumulated cells with it
    // so each one keeps describing the same patch of world. Cells scrolling in
    // from the edge start dark and smooth up, which is the correct reading —
    // ground that just came into range was not visible a moment ago.
    private void Recenter(Vector3 playerPosition, float cellSize)
    {
        int half = GRID_SIZE / 2;
        // A cell-size change (MaxRadius retuned) invalidates every cell's world
        // meaning, so treat it as a fresh start rather than carrying garbage.
        bool reset = !_anchored || !Mathf.IsEqualApprox(cellSize, _cellSize);
        _cellSize = cellSize;

        int newMinX = Mathf.FloorToInt(playerPosition.X / cellSize) - half;
        int newMinZ = Mathf.FloorToInt(playerPosition.Z / cellSize) - half;
        if (reset)
        {
            Array.Clear(_accumulated, 0, _accumulated.Length);
            _minCellX = newMinX;
            _minCellZ = newMinZ;
            _anchored = true;
            return;
        }

        int dx = newMinX - _minCellX;
        int dz = newMinZ - _minCellZ;
        if (dx == 0 && dz == 0)
        {
            return;
        }
        if (Mathf.Abs(dx) >= GRID_SIZE || Mathf.Abs(dz) >= GRID_SIZE)
        {
            Array.Clear(_accumulated, 0, _accumulated.Length);
            _minCellX = newMinX;
            _minCellZ = newMinZ;
            return;
        }

        // Destination cell (gx, gz) holds the world cell that lived at
        // (gx + dx, gz + dz) in the old window. Ping-pong rather than shifting
        // in place so overlapping ranges can't clobber themselves.
        Array.Clear(_scratch, 0, _scratch.Length);
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
            Array.Copy(_accumulated, srcZ * GRID_SIZE + srcX, _scratch, gz * GRID_SIZE + dstX, copyWidth);
        }
        (_accumulated, _scratch) = (_scratch, _accumulated);
        _minCellX = newMinX;
        _minCellZ = newMinZ;
    }

    // Marches one ray per bearing and writes the lit span into the target grid.
    // The ramp is applied here rather than in the shader because the boundary is
    // no longer a radius a fragment can compare its own distance against.
    private void RasterizeVisibility(Vector3 playerPosition)
    {
        var origin = new Vector2(playerPosition.X, playerPosition.Z);
        float maxRadius = Mathf.Max(MaxRadius, 0.01f);
        float soft = Mathf.Max(EdgeSoftness, 1e-3f);
        // Step along the ray in cell-sized increments: fine enough that no cell
        // on the ray is skipped, coarse enough that this stays ~24 writes a ray.
        float step = Mathf.Max(_cellSize, 1e-3f);

        for (int i = 0; i < RAY_COUNT; i++)
        {
            Vector2 dir = RayDirections[i];
            float stop = CastRay(origin, dir, maxRadius, out bool hitWall);
            if (hitWall)
            {
                stop = Mathf.Max(stop - RaySetback, 0f);
            }
            for (float t = 0f; t <= stop; t += step)
            {
                Vector2 p = origin + dir * t;
                int gx = Mathf.FloorToInt(p.X / _cellSize) - _minCellX;
                int gz = Mathf.FloorToInt(p.Y / _cellSize) - _minCellZ;
                if (gx < 0 || gz < 0 || gx >= GRID_SIZE || gz >= GRID_SIZE)
                {
                    break;
                }
                // Rays overlap heavily near the player, so take the brightest
                // claim on a cell rather than letting a later ray dim it.
                float lit = Mathf.Min((stop - t) / soft, 1f);
                int index = gz * GRID_SIZE + gx;
                if (lit > _target[index])
                {
                    _target[index] = lit;
                }
            }
        }
    }

    // Fills the local voxel occupancy window centred on the player. Stays at 1m
    // voxel resolution — that is the resolution the occluders actually have.
    private void BuildOccupancy(WorldState world, Vector3 playerPosition)
    {
        int radiusCells = Mathf.CeilToInt(Mathf.Max(MaxRadius, 0.01f)) + 1;
        int size = radiusCells * 2 + 1;
        if (_occupied == null || _occupiedSize != size)
        {
            _occupied = new bool[size * size];
            _occupiedSize = size;
        }

        int lowY = Mathf.FloorToInt(playerPosition.Y + Mathf.Min(SightLow, SightHigh));
        int highY = Mathf.FloorToInt(playerPosition.Y + Mathf.Max(SightLow, SightHigh));
        _occupiedMinX = Mathf.FloorToInt(playerPosition.X) - radiusCells;
        _occupiedMinZ = Mathf.FloorToInt(playerPosition.Z) - radiusCells;

        for (int gz = 0; gz < size; gz++)
        {
            int row = gz * size;
            int wz = _occupiedMinZ + gz;
            for (int gx = 0; gx < size; gx++)
            {
                int wx = _occupiedMinX + gx;
                // Blocks sight only if solid all the way through the band — one
                // clear slice anywhere in it (a window, an arch, a gap under a
                // beam) makes the column see-through.
                bool blocks = true;
                for (int wy = lowY; wy <= highY; wy++)
                {
                    if (!VoxelTypeInfo.IsSolid(world.GetVoxelWorld(wx, wy, wz)))
                    {
                        blocks = false;
                        break;
                    }
                }
                _occupied[row + gx] = blocks;
            }
        }
    }

    // 2D Amanatides-Woo march over the occupancy window. Returns the world
    // distance to the first solid cell, or maxDist if the ray runs clear (`hit`
    // false — the caller skips the setback in that case, since there is no
    // surface to sit behind). The cell the player stands in is intentionally not
    // tested: a ray starting inside solid would otherwise report zero visibility
    // in every direction. Stepping one axis per iteration means a ray crossing
    // an exact cell corner still visits an edge-adjacent cell, so it cannot slip
    // diagonally between two blocks that meet only at that corner.
    private float CastRay(Vector2 origin, Vector2 dir, float maxDist, out bool hit)
    {
        hit = false;
        int cellX = Mathf.FloorToInt(origin.X);
        int cellZ = Mathf.FloorToInt(origin.Y);
        int stepX = dir.X >= 0f ? 1 : -1;
        int stepZ = dir.Y >= 0f ? 1 : -1;

        // Ray distance covered per whole cell crossed on each axis, and the
        // distance to the first grid line. Infinity on an axis the ray doesn't
        // move along makes that axis lose every comparison, which is correct.
        float invX = Mathf.Abs(dir.X) > 1e-6f ? 1f / Mathf.Abs(dir.X) : float.PositiveInfinity;
        float invZ = Mathf.Abs(dir.Y) > 1e-6f ? 1f / Mathf.Abs(dir.Y) : float.PositiveInfinity;
        float nextX = float.IsInfinity(invX)
            ? float.PositiveInfinity
            : (dir.X >= 0f ? (cellX + 1 - origin.X) : (origin.X - cellX)) * invX;
        float nextZ = float.IsInfinity(invZ)
            ? float.PositiveInfinity
            : (dir.Y >= 0f ? (cellZ + 1 - origin.Y) : (origin.Y - cellZ)) * invZ;

        while (true)
        {
            float travelled;
            if (nextX < nextZ)
            {
                cellX += stepX;
                travelled = nextX;
                nextX += invX;
            }
            else
            {
                cellZ += stepZ;
                travelled = nextZ;
                nextZ += invZ;
            }
            if (travelled >= maxDist)
            {
                return maxDist;
            }
            if (IsOccupied(cellX, cellZ))
            {
                hit = true;
                return travelled;
            }
        }
    }

    // Outside the window reads as occupied. The window covers MaxRadius, so any
    // such cell is already past the clamp and stopping there costs nothing.
    private bool IsOccupied(int worldX, int worldZ)
    {
        int gx = worldX - _occupiedMinX;
        int gz = worldZ - _occupiedMinZ;
        if (gx < 0 || gz < 0 || gx >= _occupiedSize || gz >= _occupiedSize)
        {
            return true;
        }
        return _occupied[gz * _occupiedSize + gx];
    }

    private void Upload()
    {
        for (int i = 0; i < _accumulated.Length; i++)
        {
            _bytes[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(_accumulated[i] * 255f), 0, 255);
        }
        Image image = Image.CreateFromData(GRID_SIZE, GRID_SIZE, false, Image.Format.R8, _bytes);
        if (_texture == null)
        {
            _texture = ImageTexture.CreateFromImage(image);
            // The ImageTexture instance is stable across Updates, so the global
            // only needs binding once.
            RenderingServer.GlobalShaderParameterSet("visibility_fan_tex", _texture);
        }
        else
        {
            _texture.Update(image);
        }
    }

    private static Vector2[] BuildRayDirections()
    {
        var directions = new Vector2[RAY_COUNT];
        for (int i = 0; i < RAY_COUNT; i++)
        {
            float angle = (i + 0.5f) / RAY_COUNT * Mathf.Tau;
            directions[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        }
        return directions;
    }
}
