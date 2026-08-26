using System;
using System.Collections.Generic;
using Godot;

// Central per-frame producer of AmbienceState. Every audio consumer
// (global layers, reverb bus, positional palettes) reads from
// AmbienceController.Current.State — the controller is the single seam
// between world state and audio, mirroring how SkyController is the
// single seam between world state and the sky/lighting palette.
//
// The cheap fields (wetness, wind, env-tag, fog at listener, biome id)
// are sampled per-frame here. The expensive density fields (foliage,
// water, shoreline) get filled in by the density-sampling pass at a
// slower jittered cadence — that pass writes back into State directly.
//
// The listener is the player's world position. Sim.Current.player is
// the source of truth; the controller no-ops when no player is up
// (loading, main menu) so the State struct stays at safe defaults.
[GlobalClass]
public partial class AmbienceController : Node3D
{
    public static AmbienceController Current { get; private set; }

    private AmbienceState _state;
    public ref readonly AmbienceState State => ref _state;

    // Density sampler tick interval. The expensive voxel-box scan only
    // runs ~5 Hz; the world doesn't change fast enough to need higher.
    // Jittered per-instance at construction so two AmbienceControllers
    // (eventual multiplayer) wouldn't sample on the same frame.
    private double _densityAccumSec;
    private double _densityIntervalSec;

    // Deduplicated runtime layer players. One AmbienceLayerPlayer per
    // unique AmbienceLayerData Resource referenced by any zone's
    // globalLayers — so a base_daytime layer reused across all 4 zones
    // yields a single AudioStreamPlayer rather than one per zone.
    // _zoneLayerIndices[i] lists the indices into _layerPlayers that
    // zone i references; per-frame each unique layer's effective weight
    // is the sum of zone weights of the zones that include it. This
    // is acoustically equivalent to ticking one player per (zone,
    // layer) pair at its zone weight (identical streams sum coherently)
    // but at a fraction of the AudioStreamPlayer count.
    //
    // Lazy-built on the first frame WorldState.Zones is non-empty and
    // rebuilt if the zone count changes (defensive — currently zones
    // are write-once at world creation). When a zone's ambience entry
    // is null, _zoneLayerIndices[i] is null.
    private List<AmbienceLayerPlayer> _layerPlayers;
    private int[][] _zoneLayerIndices;

    // Smoothed per-layer weight, slewed toward the per-frame target with
    // an exponential time constant. The 5-second TAU produces a slow,
    // crossfade-feel transition between zone layer sets — biome
    // borders fade in/out over multiple seconds rather than the sub-
    // second cross-fade you get from ZoneBlend's spatial kernel alone.
    private float[] _smoothedLayerWeights;
    private const float LAYER_WEIGHT_SLEW_TAU = 5.0f;

    // Cap on simultaneously contributing zones. With many small
    // zones visible inside the blend kernel the long tail of tiny
    // weights smears the audio across a dozen layer sets at near-mute
    // volumes; clamping to the loudest few keeps the mix focused on
    // the zones the player is actually near.
    private const int MAX_CONTRIBUTING_ZONES = 3;

    public override void _Ready()
    {
        Current = this;
        // 200 ms base + up to 40 ms jitter per controller.
        _densityIntervalSec = 0.20 + GD.Randf() * 0.04;
    }

    public override void _ExitTree()
    {
        if (Current == this) { Current = null; }
    }

    public override void _Process(double delta)
    {
        using var _prof = Profiler.Sample("AmbienceController.Process");
        Sim w = Sim.Current;
        if (w == null) { return; }
        WorldState ws = w.WorldState;
        if (ws == null) { return; }
        Player player = w.player;
        if (player == null) { return; }

        Sample(ws, player.GlobalPosition);

        _densityAccumSec += delta;
        if (_densityAccumSec >= _densityIntervalSec)
        {
            _densityAccumSec = 0.0;
            SampleDensity(ws, player.GlobalPosition);
        }

        AmbienceBusDriver.Apply(_state, ws.SimData?.GetInteriorAmbience(0));
        TickLayers(ws, player.GlobalPosition, (float)delta);
    }

    // Half-extents (in voxels) of the box scanned for water/shoreline/
    // foliage densities. Roughly the listener's "audible foreground" —
    // big enough that walking up to a forest edge lifts foliage rustle
    // before you're inside, small enough to keep the per-tick voxel
    // count under 10K so this stays under a millisecond at 5 Hz.
    private const int DENSITY_HORIZ_RADIUS = 12;
    private const int DENSITY_VERT_RADIUS = 6;

    // DetailStrength threshold that qualifies a voxel as "has foliage".
    // The painted scatter rolls per-instance against strength/255, so
    // even strength=20 produces some sprites — kept low to count thinly
    // scattered grass.
    private const int DETAIL_MIN_STRENGTH = 20;

    // Cell-count saturation for FoliageDensity normalization. A box of
    // (2*HORIZ+1)² × (2*VERT+1) ≈ 8K cells; ~12% with detail reads as
    // "full forest floor", which matches typical scatter density.
    private const float FOLIAGE_SATURATION_FRACTION = 0.12f;

    private void SampleDensity(WorldState ws, Vector3 listenerPos)
    {
        int lx = Mathf.FloorToInt(listenerPos.X);
        int ly = Mathf.FloorToInt(listenerPos.Y);
        int lz = Mathf.FloorToInt(listenerPos.Z);

        int totalCount = 0;
        int waterCount = 0;
        int shorelineCount = 0;
        int detailCount = 0;

        for (int dy = -DENSITY_VERT_RADIUS; dy <= DENSITY_VERT_RADIUS; dy++)
        {
            int wy = ly + dy;
            for (int dz = -DENSITY_HORIZ_RADIUS; dz <= DENSITY_HORIZ_RADIUS; dz++)
            {
                int wz = lz + dz;
                for (int dx = -DENSITY_HORIZ_RADIUS; dx <= DENSITY_HORIZ_RADIUS; dx++)
                {
                    int wx = lx + dx;
                    totalCount++;

                    int v = ws.GetBlockWorld(wx, wy, wz);
                    if (Blocks.IsWater(v))
                    {
                        waterCount++;
                        if (IsShoreline(ws, wx, wy, wz))
                        {
                            shorelineCount++;
                        }
                    }

                    if (ws.GetDetailStrengthWorld(wx, wy, wz) >= DETAIL_MIN_STRENGTH)
                    {
                        detailCount++;
                    }
                }
            }
        }

        if (totalCount == 0)
        {
            _state.WaterDensity = 0f;
            _state.ShorelineFactor = 0f;
            _state.FoliageDensity = 0f;
            return;
        }

        _state.WaterDensity = (float)waterCount / totalCount;
        _state.ShorelineFactor = (float)shorelineCount / totalCount;

        float foliageSaturationCells = totalCount * FOLIAGE_SATURATION_FRACTION;
        float foliage = detailCount / foliageSaturationCells;
        if (foliage > 1f) { foliage = 1f; }
        _state.FoliageDensity = foliage;
    }

    // A water voxel is "shoreline" when at least one of its four
    // horizontal neighbors is solid land (not water, not air). This
    // produces the count of water cells facing land — the lap audio's
    // natural source.
    private static bool IsShoreline(WorldState ws, int wx, int wy, int wz)
    {
        return IsLand(ws.GetBlockWorld(wx + 1, wy, wz))
            || IsLand(ws.GetBlockWorld(wx - 1, wy, wz))
            || IsLand(ws.GetBlockWorld(wx, wy, wz + 1))
            || IsLand(ws.GetBlockWorld(wx, wy, wz - 1));
    }

    private static bool IsLand(int v)
    {
        return v != Blocks.AirId && !Blocks.IsWater(v);
    }

    private void TickLayers(WorldState ws, Vector3 listenerPos, float deltaTime)
    {
        EnsureLayerPlayers(ws);

        if (_layerPlayers == null || _layerPlayers.Count == 0) { return; }

        int zoneCount = _zoneLayerIndices.Length;
        Span<float> zoneWeights = zoneCount <= 32 ? stackalloc float[zoneCount] : new float[zoneCount];
        if (!ZoneBlend.SampleWeights(listenerPos, ws, zoneWeights))
        {
            // No data this frame — fade everything down rather than
            // freezing at last-known weights, since "no zone under the
            // listener" usually means the listener is outside the world
            // (debug fly-cam) and audio should mute.
            for (int i = 0; i < zoneCount; i++) { zoneWeights[i] = 0f; }
        }

        // Keep only the top-K zones and renormalize so they sum to 1.
        // Without this, a player at the meeting point of four small
        // zones would smear across all four layer sets at ~0.25 each.
        KeepTopKAndRenormalize(zoneWeights, MAX_CONTRIBUTING_ZONES);

        // Accumulate per-layer weight as the sum of weights of zones
        // that reference this layer. ZoneBlend weights sum to 1 across
        // zones, so a layer present in every zone settles at 1.0 and
        // a layer in only one zone scales with that zone's weight —
        // matching the previous per-(zone, layer) behavior with a
        // single player per unique layer.
        int layerCount = _layerPlayers.Count;
        Span<float> layerTargets = layerCount <= 64 ? stackalloc float[layerCount] : new float[layerCount];
        layerTargets.Clear();
        for (int r = 0; r < zoneCount; r++)
        {
            int[] indices = _zoneLayerIndices[r];
            if (indices == null) { continue; }
            float w = zoneWeights[r];
            if (w <= 0f) { continue; }
            for (int j = 0; j < indices.Length; j++)
            {
                layerTargets[indices[j]] += w;
            }
        }

        // Exponential slew from the smoothed weight toward the target.
        // Long TAU (5s) over a 60Hz tick is dt/tau ≈ 0.0033, so this
        // really does crawl — biome boundaries audibly take seconds.
        float alpha = deltaTime / LAYER_WEIGHT_SLEW_TAU;
        if (alpha > 1f) { alpha = 1f; }

        float tod = (float)ws.TimeOfDay01;
        for (int i = 0; i < layerCount; i++)
        {
            _smoothedLayerWeights[i] += (layerTargets[i] - _smoothedLayerWeights[i]) * alpha;
            _layerPlayers[i].Tick(_state, _smoothedLayerWeights[i], tod, deltaTime);
        }
    }

    // Zero out all but the K largest entries in `weights` and rescale
    // the survivors so they sum to 1. K=0 or K>=count is a no-op-ish
    // (just renormalizes). Operates in place; O(N*K) which is fine for
    // the small zone counts we have.
    private static void KeepTopKAndRenormalize(Span<float> weights, int k)
    {
        if (k <= 0 || k >= weights.Length)
        {
            float sumAll = 0f;
            for (int i = 0; i < weights.Length; i++) { sumAll += weights[i]; }
            if (sumAll <= 0f) { return; }
            float invAll = 1f / sumAll;
            for (int i = 0; i < weights.Length; i++) { weights[i] *= invAll; }
            return;
        }

        // Find the K-th largest weight via partial selection.
        Span<int> topIdx = stackalloc int[k];
        for (int i = 0; i < k; i++) { topIdx[i] = -1; }
        for (int i = 0; i < weights.Length; i++)
        {
            float w = weights[i];
            if (w <= 0f) { continue; }
            // Find the slot to displace: the smallest current top, if
            // it's smaller than w (or empty).
            int worstSlot = 0;
            float worstVal = topIdx[0] >= 0 ? weights[topIdx[0]] : -1f;
            for (int s = 1; s < k; s++)
            {
                float sv = topIdx[s] >= 0 ? weights[topIdx[s]] : -1f;
                if (sv < worstVal) { worstSlot = s; worstVal = sv; }
            }
            if (topIdx[worstSlot] < 0 || w > worstVal) { topIdx[worstSlot] = i; }
        }

        // Zero everything not in the top-K, sum survivors, renormalize.
        Span<bool> keep = stackalloc bool[weights.Length];
        for (int s = 0; s < k; s++) { if (topIdx[s] >= 0) { keep[topIdx[s]] = true; } }
        float sum = 0f;
        for (int i = 0; i < weights.Length; i++)
        {
            if (!keep[i]) { weights[i] = 0f; }
            else { sum += weights[i]; }
        }
        if (sum <= 0f) { return; }
        float inv = 1f / sum;
        for (int i = 0; i < weights.Length; i++) { weights[i] *= inv; }
    }

    private void EnsureLayerPlayers(WorldState ws)
    {
        ZoneState[] zones = ws.Zones;
        int zoneCount = zones != null ? zones.Length : 0;
        if (_zoneLayerIndices != null && _zoneLayerIndices.Length == zoneCount) { return; }

        // Zone count changed — tear down and rebuild. Currently only
        // happens at first frame (null → real). If zones ever become
        // mutable at runtime this branch will preserve correctness.
        if (_layerPlayers != null)
        {
            for (int i = 0; i < _layerPlayers.Count; i++)
            {
                _layerPlayers[i].QueueFree();
            }
        }

        _layerPlayers = new List<AmbienceLayerPlayer>();
        _zoneLayerIndices = new int[zoneCount][];
        var dataToIndex = new Dictionary<AmbienceLayerData, int>();

        for (int i = 0; i < zoneCount; i++)
        {
            ZoneData rd = zones[i].Data;
            ZoneAmbienceData ambience = rd?.ambience;
            if (ambience == null || ambience.globalLayers == null || ambience.globalLayers.Length == 0)
            {
                _zoneLayerIndices[i] = null;
                continue;
            }

            var indices = new List<int>(ambience.globalLayers.Length);
            for (int j = 0; j < ambience.globalLayers.Length; j++)
            {
                AmbienceLayerData data = ambience.globalLayers[j];
                if (data == null) { continue; }
                if (!dataToIndex.TryGetValue(data, out int idx))
                {
                    idx = _layerPlayers.Count;
                    dataToIndex[data] = idx;
                    var player = new AmbienceLayerPlayer();
                    player.Name = $"Layer{idx}";
                    AddChild(player);
                    player.Configure(data);
                    _layerPlayers.Add(player);
                }
                indices.Add(idx);
            }
            _zoneLayerIndices[i] = indices.ToArray();
        }

        _smoothedLayerWeights = new float[_layerPlayers.Count];
    }

    // Non-density fields only — this is the cheap per-frame pass. Density
    // fields (FoliageDensity / WaterDensity / ShorelineFactor) are the
    // density-sampler's responsibility and are not touched here so its
    // slower cadence isn't fighting this faster one.
    private void Sample(WorldState ws, Vector3 listenerPos)
    {
        _state.Wetness = ws.WetnessLevel;
        _state.WindSpeed = ws.SampleWindFactor(listenerPos);
        // SkyController rewrites the blended WeatherData in place each
        // frame, so .lightningAmount here is the simulated current
        // value (already gated by storm conditions + variance) rather
        // than the authored zone max. Mirrors how Wetness reads
        // ws.WetnessLevel instead of the authored rain ceiling.
        _state.LightningIntensity = SkyController.Current?.Weather?.lightningAmount ?? 0f;
        _state.DestinationLightningIntensity = SkyController.Current?.Weather?.destinationLightningAmount ?? 0f;
        // Visual rain intensity. Reads palette.RainIntensity — the
        // SLEWED display value SkyController writes after the rain-
        // effect look-ahead pass — rather than the raw simulated
        // rainAmount. Keeps rain audio in lock-step with the visible
        // particle effect.
        _state.RainIntensity = SkyController.Current?.Palette.RainIntensity ?? 0f;
        InteriorAmbience ambience = ws.SampleInteriorAmbience(listenerPos);

        _state.Interior = ambience;

        _state.Openness = ambience.Openness;
        _state.Caveness = Mathf.Clamp(ambience.TotalWeight - ambience.Openness, 0f, 1f);

        // Fog at the listener. WorldState.GetFogWorld returns 0..255 per
        // voxel; normalize. Single-voxel sample is fine for audio (vs
        // shaders which trilinearly filter the FogMap) — listener
        // localized to one voxel produces no audible boundary.
        int fwx = Mathf.FloorToInt(listenerPos.X);
        int fwy = Mathf.FloorToInt(listenerPos.Y);
        int fwz = Mathf.FloorToInt(listenerPos.Z);
        _state.FogDensity = ws.GetFogWorld(fwx, fwy, fwz) / 255f;

        // Biome id = ZoneIndex of the listener's chunk. Unloaded chunk
        // (impossible at the listener, but defensive) returns -1 so
        // consumers can fall back to a "no biome" default.
        Vector3I cc = Sim.WorldToChunkCoord(listenerPos);
        ChunkState chunk = ws.GetChunk(cc);
        _state.BiomeId = chunk != null ? chunk.ZoneIndex : -1;
    }

    // Listener-height offset for the enclosure rays. Shooting from
    // GlobalPosition (foot level) would routinely hit the ground voxel
    // straight ahead and read as "enclosed" outdoors. This puts the rays
    // at roughly head height where a real listener would be.
    private const float LISTENER_EAR_HEIGHT = 1.5f;

}
