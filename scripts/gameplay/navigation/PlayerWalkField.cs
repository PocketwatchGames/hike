using Godot;

// The player's standability view of the world, served from the same sampler
// the mob pathfinder uses (WalkabilityGrid.SampleColumn). Consumers ask
// "is there a surface I could stand on at this XZ, near this Y?" and get back
// the surface height — which is what the mantle probe needs to decide whether
// there is a ledge in front worth climbing, and where it lands.
//
// Sampling goes through SampleColumn DIRECTLY rather than WalkabilityGrid.Sample,
// for the same reason NavGridDebug does: Sample populates the process-wide
// SharedWalkabilityCache, whose miss path runs SampleColumn across a ~37x37
// window (~4ms). The player crosses a cache quantum every few metres, so
// routing per-tick queries through it would buy a periodic frame hitch to
// answer a question about nine columns. Here we memo a small window locally and
// refresh it only when the player leaves it.
public sealed class PlayerWalkField
{
    // Half-extent of the memoized window, in voxels. Must exceed one tick of
    // travel plus the guard's probe reach, or a fast player queries outside the
    // window and forces a resample mid-tick. 3 -> a 7x7 column window, ~1.5m of
    // slack past the capsule at sprint speed.
    private const int WindowRadius = 3;
    private const int WindowSize = WindowRadius * 2 + 1;

    // Vertical drift from the sample anchor tolerated before the window is
    // rebuilt. SampleColumn scans +/-SurfaceSearchRadius (12) around the anchor,
    // so this only has to stay well inside that to keep every layer in view.
    private const int AnchorRestampY = 4;

    private WalkabilityCell[] _cells;
    private int _originX;
    private int _originZ;
    private int _anchorY;
    private bool _valid;

    // Rebuild the memo if the player has left the window it was sampled for.
    // Cheap and idempotent — call once per tick before querying.
    public void Refresh(WorldState ws, Sim sim, in TraversalProfile profile, Vector3 position)
    {
        int px = Mathf.FloorToInt(position.X);
        int py = Mathf.FloorToInt(position.Y);
        int pz = Mathf.FloorToInt(position.Z);
        int wantOriginX = px - WindowRadius;
        int wantOriginZ = pz - WindowRadius;

        if (_valid
            && _originX == wantOriginX
            && _originZ == wantOriginZ
            && Mathf.Abs(py - _anchorY) <= AnchorRestampY)
        {
            return;
        }

        _cells ??= new WalkabilityCell[WindowSize * WindowSize * WalkabilityGrid.MaxColumnLayers];
        _originX = wantOriginX;
        _originZ = wantOriginZ;
        _anchorY = py;

        for (int j = 0; j < WindowSize; j++)
        {
            for (int i = 0; i < WindowSize; i++)
            {
                WalkabilityGrid.SampleColumn(ws, sim, profile,
                    _originX + i, _anchorY, _originZ + j,
                    _cells, (j * WindowSize + i) * WalkabilityGrid.MaxColumnLayers);
            }
        }
        _valid = true;
    }

    // Standable surface at column (wx, wz) whose height is nearest refY.
    // Returns false when the column has no standable layer at all, when it sits
    // in an unloaded chunk, or when the query is outside the memoized window —
    // all three are "do not walk here", which is the answer the guard wants.
    //
    // Out-of-window reads returning false is deliberate rather than an error:
    // it fails safe (the player stops) instead of silently reading a stale
    // column, and Refresh's window is sized so it cannot happen in normal play.
    public bool TryGetSurface(int wx, int wz, float refY, out float surfaceY, out bool isWater,
        out bool isSwim)
    {
        surfaceY = 0f;
        isWater = false;
        isSwim = false;
        if (!_valid)
        {
            return false;
        }
        int i = wx - _originX;
        int j = wz - _originZ;
        if (i < 0 || j < 0 || i >= WindowSize || j >= WindowSize)
        {
            return false;
        }

        int baseIdx = (j * WindowSize + i) * WalkabilityGrid.MaxColumnLayers;
        if ((_cells[baseIdx].flags & CellFlags.OutOfBounds) != 0)
        {
            return false;
        }

        bool found = false;
        float bestDist = float.MaxValue;
        for (int layer = 0; layer < WalkabilityGrid.MaxColumnLayers; layer++)
        {
            WalkabilityCell c = _cells[baseIdx + layer];
            if (!c.Walkable)
            {
                break;
            }
            float d = Mathf.Abs(c.surfaceY - refY);
            if (d < bestDist)
            {
                bestDist = d;
                surfaceY = c.surfaceY;
                isWater = c.IsWater;
                isSwim = c.IsSwim;
                found = true;
            }
        }
        return found;
    }

    // Standable surface in column (wx, wz) inside [minY, maxY], picking the one
    // closest to refY when the column offers several.
    //
    // The band is what makes this directed where TryGetSurface is not: a mantle
    // asks "what could I climb onto, up or down", and the caller expresses which
    // by where it puts the band relative to refY. Closest-within-band is right
    // for both — climbing a low wall under a balcony takes the wall, and
    // dropping down a terrace takes the first step, not the canyon floor.
    public bool TryGetSurfaceInBand(int wx, int wz, float minY, float maxY, float refY,
        out float surfaceY, out bool isWater)
    {
        surfaceY = 0f;
        isWater = false;
        if (!_valid)
        {
            return false;
        }
        int i = wx - _originX;
        int j = wz - _originZ;
        if (i < 0 || j < 0 || i >= WindowSize || j >= WindowSize)
        {
            return false;
        }

        int baseIdx = (j * WindowSize + i) * WalkabilityGrid.MaxColumnLayers;
        if ((_cells[baseIdx].flags & CellFlags.OutOfBounds) != 0)
        {
            return false;
        }

        bool found = false;
        float bestDist = float.MaxValue;
        for (int layer = 0; layer < WalkabilityGrid.MaxColumnLayers; layer++)
        {
            WalkabilityCell c = _cells[baseIdx + layer];
            if (!c.Walkable)
            {
                break;
            }
            if (c.surfaceY < minY || c.surfaceY > maxY)
            {
                continue;
            }
            float d = Mathf.Abs(c.surfaceY - refY);
            if (d < bestDist)
            {
                bestDist = d;
                surfaceY = c.surfaceY;
                isWater = c.IsWater;
                found = true;
            }
        }
        return found;
    }

    // Convenience for world-space points.
    public bool TryGetSurface(Vector3 worldPos, out float surfaceY, out bool isWater,
        out bool isSwim)
    {
        return TryGetSurface(
            Mathf.FloorToInt(worldPos.X),
            Mathf.FloorToInt(worldPos.Z),
            worldPos.Y, out surfaceY, out isWater, out isSwim);
    }

    // Invalidate the memo — call when the player teleports, respawns, or the
    // world mutates under them, so the next Refresh rebuilds unconditionally.
    public void Invalidate()
    {
        _valid = false;
    }
}
