using Godot;

// Spawns damaging LightningStrike entities around the player based on
// the current weather lightning intensity. Lives as a sibling of
// ThunderScheduler under World — same input signal
// (AmbienceController.LightningIntensity), different output: thunder
// fires distant audio rumbles for atmosphere, this fires gameplay
// strikes that can damage and knock back actors near the player.
//
// Cadence is exponentially-jittered around a mean inter-strike
// interval that lerps from weatherSpawnIntervalAtFloor at the
// intensity floor to weatherSpawnIntervalAtPeak at full intensity.
// Dormant (no scheduled strike) when intensity is below the floor,
// so a clear day costs nothing.
//
// Spawn position: random direction in an annulus around the player,
// ground-snapped via a downward Environment raycast. If the column
// has no ground (off the map, mid-air), the strike is skipped — we
// don't want bolts hanging in space.
[GlobalClass]
public partial class WeatherLightningSpawner : Node
{
    // Strike position ray span (Sim.TryFindGroundByRaycast) — cast from this far
    // above the spawn point down through it. Generous enough to clear any sky-
    // island geometry the player might be standing on. Lightning is open-sky
    // weather, so the raycast surface finder is the right tool here.
    private const float GROUND_RAY_HEIGHT_OFFSET = 80f;
    private const float GROUND_RAY_DEPTH_OFFSET = 80f;

    private double _timeUntilNext;
    private readonly RandomNumberGenerator _rng = new RandomNumberGenerator();

    public override void _Ready()
    {
        _rng.Randomize();
        // Initial wait so a freshly loaded world doesn't fire a
        // strike on frame 0 before the player has even seen the sky.
        _timeUntilNext = 3.0;
    }

    public override void _Process(double delta)
    {
        LightningData data = Sim.Current?.SimData?.weatherLightning;
        if (data == null)
        {
            return;
        }
        // STORM GATE: destination intensity (end of current variance
        // crossfade). Mirrors ThunderScheduler so strikes and ambient
        // thunder always travel together — no thunder without
        // possible strikes, no strikes without ambient thunder.
        // Cadence inside the gate uses CURRENT intensity so an
        // approaching storm gets sparse early strikes that build up.
        AmbienceState s = AmbienceController.Current?.State ?? default;
        float destIntensity = s.DestinationLightningIntensity;
        float intensity = s.LightningIntensity;
        if (destIntensity <= data.weatherSpawnIntensityFloor)
        {
            // Park the timer at a reasonable wait so we don't fire
            // on the same frame intensity crosses the floor.
            _timeUntilNext = SampleInterval(data, data.weatherSpawnIntensityFloor);
            return;
        }
        _timeUntilNext -= delta;
        if (_timeUntilNext > 0.0)
        {
            return;
        }
        TrySpawnStrike(data, intensity);
        _timeUntilNext = SampleInterval(data, intensity);
        if (CVars.lightningLog.Value)
        {
            GD.Print($"[lightning] intensity={intensity:F3} dest={destIntensity:F3} next_interval={_timeUntilNext:F1}s");
        }
    }

    private void TrySpawnStrike(LightningData data, float intensity)
    {
        Sim sim = Sim.Current;
        Player player = sim?.player;
        if (player == null)
        {
            return;
        }
        Vector3 playerPos = player.GlobalPosition;

        float yaw = _rng.RandfRange(0f, Mathf.Tau);
        float maxR = Mathf.Max(0f, data.weatherSpawnRadius);
        // sqrt of uniform gives a uniform distribution across disk
        // area (more area at larger radii = more strikes farther out).
        float r = Mathf.Sqrt(_rng.Randf()) * maxR;
        Vector2 offset = new Vector2(Mathf.Cos(yaw), Mathf.Sin(yaw)) * r;
        Vector3 query2d = playerPos + new Vector3(offset.X, 0f, offset.Y);

        if (!sim.TryFindGroundByRaycast(query2d, out Vector3 groundPos, GROUND_RAY_HEIGHT_OFFSET, GROUND_RAY_DEPTH_OFFSET))
        {
            if (CVars.lightningLog.Value)
            {
                GD.Print($"[lightning] skip: no ground at ({query2d.X:F1}, {query2d.Y:F1}, {query2d.Z:F1})");
            }
            return;
        }
        LightningStrike.Create(sim, groundPos, data);
        if (CVars.lightningLog.Value)
        {
            GD.Print($"[lightning] FIRE at ({groundPos.X:F1}, {groundPos.Y:F1}, {groundPos.Z:F1}) (intensity={intensity:F3})");
        }
    }

    // Exponentially-jittered interval around the intensity-blended
    // mean. Same Poisson-like distribution shape ThunderScheduler
    // uses, but with a separate (typically much longer) mean so
    // strikes land less often than the distant rumbles.
    //
    // Intensity is remapped: raw lightningAmount peaks around
    // 0.1-0.3 in realistic storms (it's lightningMax * smoothstep
    // gate * variance, see WeatherSimulation), rarely reaching 1.0.
    // A linear lerp on raw intensity would leave the mean parked at
    // atFloor for the entire useful range. Remap (intensity - floor)
    // / (atFullIntensity - floor) clamped to [0, 1], then sqrt-curve
    // so even moderate above-floor activity pulls the mean toward
    // atPeak.
    private double SampleInterval(LightningData data, float intensity)
    {
        float floor = data.weatherSpawnIntensityFloor;
        float ceiling = Mathf.Max(floor + 1e-3f, data.weatherSpawnIntensityForPeak);
        float t = Mathf.Clamp((intensity - floor) / (ceiling - floor), 0f, 1f);
        t = Mathf.Sqrt(t);
        float mean = Mathf.Lerp(data.weatherSpawnIntervalAtFloor, data.weatherSpawnIntervalAtPeak, t);
        if (mean < 0.1f) { mean = 0.1f; }
        float u = Mathf.Max(_rng.Randf(), 1e-4f);
        float wait = mean * -Mathf.Log(u);
        float waitMax = mean * 4f;
        if (wait > waitMax) { wait = waitMax; }
        if (wait < 0.1f) { wait = 0.1f; }
        return wait;
    }
}
