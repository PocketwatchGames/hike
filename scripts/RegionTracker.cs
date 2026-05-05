using Godot;

// Per-player current named-region state with hysteresis.
//
// Reads the zone under the player each tick (via ChunkState.ZoneIndex
// → WorldState.Zones[i].Data.region) and turns the raw stream of
// "what zone am I in?" into a stable "what named region am I in?"
// signal. World owns one of these and ticks it from World.Tick; the
// World fires onRegionEntered when the current region changes to a
// non-null value, which the HUD banner subscribes to.
//
// Hysteresis rules:
//   - When the underfoot zone has a named region different from
//     `Current`, start a dwell timer. Only swap (and fire the
//     banner) once the player has been continuously inside that
//     region's zones for DwellSeconds OR has moved
//     EnterDistanceChunks past where the dwell started.
//   - When the underfoot zone is a border (region == null),
//     `Current` stays the same until the player's distance from
//     `_currentEnterPos` exceeds BorderTravelChunks. At that point
//     `Current` clears silently (no banner) — the next named region
//     they walk into gets its own banner.
public class RegionTracker
{
    // Time the player must spend continuously inside a candidate
    // region's zones before the swap is committed. Tuned so
    // wiggling on a seam doesn't flicker the banner; long enough
    // that an intentional crossing still fires within a step or two.
    public const float DwellSeconds = 1.5f;

    // Alternative dwell trigger: distance walked into the candidate
    // region (in chunks) since dwell started. A confident stride
    // past the boundary commits the swap before the timer expires.
    public const float EnterDistanceChunks = 1.0f;

    // Cap on how far the player can travel through border zones
    // while keeping the previous region "sticky." Beyond this,
    // Current clears. A bit larger than ZoneBlend.BlendRadiusChunks
    // (= 2) so the visible cross-blend band is fully inside the
    // sticky range.
    public const float BorderTravelChunks = 3.0f;

    public RegionData Current { get; private set; }

    private RegionData _pending;
    private Vector3 _pendingEnterPos;
    private float _pendingElapsed;
    private Vector3 _currentEnterPos;

    // Drive this from World.Tick (gated by pause). Reads the zone
    // under playerPos and updates state. onChanged fires only on
    // entry into a named region (Current null → non-null OR Current
    // → different non-null). Clearing to null is silent so the
    // border-cap doesn't double-pulse the banner.
    public void Tick(Vector3 playerPos, WorldState ws, double delta, System.Action<RegionData> onEntered)
    {
        if (ws == null) { return; }

        RegionData candidate = SampleZoneRegion(playerPos, ws);

        if (candidate == null)
        {
            // Border zone (or unloaded chunk). Cancel any pending
            // swap — we left the candidate's territory before the
            // dwell completed.
            _pending = null;
            _pendingElapsed = 0f;

            if (Current != null)
            {
                float d = ChunkDistanceXZ(playerPos, _currentEnterPos);
                if (d > BorderTravelChunks)
                {
                    Current = null;
                }
            }
            return;
        }

        if (candidate == Current)
        {
            // Re-entered the current region after dipping into a
            // border. Cancel any pending swap and re-anchor the
            // sticky center so subsequent border travel is measured
            // from this fresh entry.
            _pending = null;
            _pendingElapsed = 0f;
            _currentEnterPos = playerPos;
            return;
        }

        // candidate is a different named region — run the dwell.
        if (candidate != _pending)
        {
            _pending = candidate;
            _pendingEnterPos = playerPos;
            _pendingElapsed = 0f;
        }
        else
        {
            _pendingElapsed += (float)delta;
        }

        bool dwellMet = _pendingElapsed >= DwellSeconds;
        bool distMet = ChunkDistanceXZ(playerPos, _pendingEnterPos) >= EnterDistanceChunks;
        if (dwellMet || distMet)
        {
            Current = candidate;
            _currentEnterPos = playerPos;
            _pending = null;
            _pendingElapsed = 0f;
            onEntered?.Invoke(Current);
        }
    }

    private static RegionData SampleZoneRegion(Vector3 playerPos, WorldState ws)
    {
        Vector3I cc = World.WorldToChunkCoord(playerPos);
        ChunkState chunk = ws.GetChunk(cc);
        if (chunk == null) { return null; }
        if (ws.Zones == null || chunk.ZoneIndex >= ws.Zones.Length) { return null; }
        return ws.Zones[chunk.ZoneIndex].Data?.region;
    }

    private static float ChunkDistanceXZ(Vector3 a, Vector3 b)
    {
        float dx = (a.X - b.X) / ChunkState.SIZE;
        float dz = (a.Z - b.Z) / ChunkState.SIZE;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
