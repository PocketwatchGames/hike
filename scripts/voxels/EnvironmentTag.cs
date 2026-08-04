// Index 0 of SimData.interiorAmbiences is the OUTDOOR entry by contract —
// EnvTagGen writes it for open cells and every fallback resolves to it. The
// rest of the list is free to reorder; the old pinned Outdoor/Building/Cave/
// Tunnel enum is gone, along with the wire compatibility it was buying.

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
