using System;
using Godot;

// Always-resident map cache entry for one forge (see WorldSimState.ForgeMarkers).
// Carries just enough to draw the marker while the forge's chunk is unloaded.
public readonly struct ForgeMarkerInfo
{
    public readonly int ReactivateDay;
    public readonly int Level;

    public ForgeMarkerInfo(int reactivateDay, int level)
    {
        ReactivateDay = reactivateDay;
        Level = level;
    }
}

// Deterministic forge-offer resolution shared by the in-world Forge (which floats
// the offered slot's model) and the map-marker renderer (which draws the offered
// slot's icon). Both MUST resolve identically, so the logic lives here once.
//
// A forge offers one upgrade per day, chosen from SimData.forgeUpgrades by hashing
// (position, offer-day). The offer-day is the day the forge is next usable — today
// while ready, tomorrow (its ReactivateDay) while inert — so the preview always
// shows what the player will actually receive.
public static class ForgeOffer
{
    public static StatusEffectData Resolve(Godot.Collections.Array<StatusEffectData> pool, Vector3 worldPos, int today, int reactivateDay)
    {
        if (pool == null || pool.Count == 0)
        {
            return null;
        }
        int offerDay = Math.Max(today, reactivateDay);
        int idx = OfferHash(worldPos, offerDay) % pool.Count;
        return pool[idx];
    }

    // Stable non-negative hash of (position, day). Position is rounded to whole
    // meters to match the marker key quantization.
    public static int OfferHash(Vector3 pos, int day)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + Mathf.RoundToInt(pos.X);
            h = h * 31 + Mathf.RoundToInt(pos.Y);
            h = h * 31 + Mathf.RoundToInt(pos.Z);
            h = h * 31 + day;
            return h & 0x7fffffff;
        }
    }
}
