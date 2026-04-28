using Godot;

// Per-region runtime state. WorldState.Regions[] holds one of these per
// region in the loaded world; ChunkState.RegionIndex picks one per chunk.
//
// `Data` is the authored theme + weather profile (colors, dust amount,
// water properties, baseline weather). It's static and shared across runs.
//
// `WindDirection` and `Elevation` are runtime — set by WorldGen (or a
// future editor / save layer) and serialized with the world. They're
// region-intrinsic in the sense that they don't change with weather
// state, but they DO vary per world generation, so they live here rather
// than on the authored RegionData resource.
public struct RegionState
{
    public RegionData Data;

    // Compass heading of the prevailing wind in world XZ. Y component
    // unused; magnitude ignored (consumers normalize).
    public Vector3 WindDirection;

    // Normalized elevation in [0, 1]: 0 = sea level, 1 = high alpine.
    // Feeds WeatherSimulation — high elevation cools baseline temperature,
    // dries baseline humidity, raises baseline wind, and amplifies dust lift.
    public float Elevation;
}
