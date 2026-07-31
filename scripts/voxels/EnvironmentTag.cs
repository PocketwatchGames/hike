// The four ORIGINAL classes of space, kept because their values are pinned to
// the first four entries of SimData.interiorAmbiences. ChunkState.EnvTag stores
// a palette index now, not one of these — but every .hike and .hikescene written
// before the palette existed holds these values, so pinning them costs one
// authored ordering constraint and saves a wire-format bump plus a remap on read.
//
// Worldgen still seeds with them (EnvTagGen), since it only ever needs "open" vs
// "sealed" and those two are guaranteed to be at a known index. Authors paint
// any index in the palette; nothing else in the codebase reads these names.
//
// Do NOT add members. A new class of space is a new .tres appended to
// SimData.interiorAmbiences — that is the whole point of the palette.
public enum EnvironmentTag : byte
{
    Outdoor = 0,
    Building = 1,
    Cave = 2,
    Tunnel = 3,
}

// A blended InteriorAmbienceData sample at a point: the eight surrounding env
// cells, each contributing its entry's fields weighted by trilinear distance.
// Blending the VALUES rather than returning per-class weights is what lets the
// palette grow — this struct's size is fixed no matter how many classes exist,
// where the old per-class weight struct grew a float per class and is why there
// were only ever four.
//
// Weights sum to 1 when every sampled corner is loaded. Unloaded corners drop
// their contribution, so the sum slips below 1; that underflow is the caller's
// "no data here" signal (see AmbienceBusDriver, which spends it on Outdoor so
// missing data biases dry rather than toward whatever corner happened to load).
public struct InteriorAmbience
{
    public float TotalWeight;

    public float DustFloor;
    public float WindSuppression;
    public float Openness;

    public float ReverbWet;
    public float ReverbRoomSize;
    public float ReverbPredelayMs;
    public float ReverbDamping;
    public float LowpassCutoffHz;

    // Drift every field toward another class by `t`, in place. Used for the
    // geometric-enclosure pull, where a raycast says the space is tighter than
    // the authored cell claims.
    //
    // The target is scaled by TotalWeight so the blend stays in the same
    // weighted space this struct accumulated in — lerping a partially-weighted
    // sample toward raw resource values would inflate it back toward full
    // weight and quietly cancel the "no data here" signal.
    public void BlendToward(InteriorAmbienceData target, float t)
    {
        if (target == null || t <= 0f)
        {
            return;
        }
        t = t > 1f ? 1f : t;
        float scale = TotalWeight;
        DustFloor += (target.dustFloor * scale - DustFloor) * t;
        WindSuppression += (target.windSuppression * scale - WindSuppression) * t;
        Openness += (target.openness * scale - Openness) * t;
        ReverbWet += (target.reverbWet * scale - ReverbWet) * t;
        ReverbRoomSize += (target.reverbRoomSize * scale - ReverbRoomSize) * t;
        ReverbPredelayMs += (target.reverbPredelayMs * scale - ReverbPredelayMs) * t;
        ReverbDamping += (target.reverbDamping * scale - ReverbDamping) * t;
        LowpassCutoffHz += (target.lowpassCutoffHz * scale - LowpassCutoffHz) * t;
    }

    public void Accumulate(InteriorAmbienceData data, float weight)
    {
        if (data == null || weight <= 0f)
        {
            return;
        }
        TotalWeight += weight;
        DustFloor += data.dustFloor * weight;
        WindSuppression += data.windSuppression * weight;
        Openness += data.openness * weight;
        ReverbWet += data.reverbWet * weight;
        ReverbRoomSize += data.reverbRoomSize * weight;
        ReverbPredelayMs += data.reverbPredelayMs * weight;
        ReverbDamping += data.reverbDamping * weight;
        LowpassCutoffHz += data.lowpassCutoffHz * weight;
    }
}
