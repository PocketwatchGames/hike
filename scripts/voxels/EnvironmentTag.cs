// Coarse classification of a pocket of space — written per-cell into
// ChunkState.EnvTag (4³ cells per chunk) and trilinearly sampled at the
// listener to produce {Outdoor, Building, Cave, Tunnel} weights for the
// audio system. Drives reverb-bus blending and outdoor-layer attenuation;
// stacked on top of a per-frame geometric-enclosure raycast for fine
// variation. Authored in the editor (when one exists); worldgen seeds a
// default from the wind/sunlight signal.
//
// Wire values are stable — appended only, never reused. The byte stored
// in ChunkState.EnvTag is the underlying enum value, so any new tag must
// take the next free integer and existing world files keep loading.
public enum EnvironmentTag : byte
{
    // Open sky overhead — outdoor ambience plays at full volume, reverb
    // is near-dry. Default for newly-allocated chunks.
    Outdoor = 0,

    // Enclosed authored interior (house, ruin, fortress). Outdoor layers
    // attenuate; reverb is medium room.
    Building = 1,

    // Natural rock cavity. Wet, dark reverb; outdoor layers fully ducked.
    Cave = 2,

    // Long narrow corridor (mineshaft, dungeon hallway). Reverb has more
    // delay, less wet — distinct from the open boom of a Cave.
    Tunnel = 3,
}

// Trilinearly-sampled tag weights at the listener — the eight surrounding
// cells each contribute their tag with a fractional weight. Audio reverb
// blending crossfades preset parameters by these weights instead of
// snapping presets at cell boundaries. Total weight sums to 1 when every
// sampled corner is loaded; unloaded corners drop their contribution and
// the sum slips below 1, which the listener treats as "no data here" and
// falls back to outdoor defaults.
//
// Add a new field if EnvironmentTag gains a value. Match the order in
// WorldState.SampleEnvTagWeights' switch and keep field names aligned
// with the enum names.
public struct EnvTagWeights
{
    public float Outdoor;
    public float Building;
    public float Cave;
    public float Tunnel;

    public void Add(EnvironmentTag tag, float weight)
    {
        switch (tag)
        {
            case EnvironmentTag.Outdoor:
            {
                Outdoor += weight;
                break;
            }
            case EnvironmentTag.Building:
            {
                Building += weight;
                break;
            }
            case EnvironmentTag.Cave:
            {
                Cave += weight;
                break;
            }
            case EnvironmentTag.Tunnel:
            {
                Tunnel += weight;
                break;
            }
        }
    }
}
