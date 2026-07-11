using System;
using Godot;

// Always-resident map cache entry for one forge (see WorldSimState.ForgeMarkers).
// Carries just enough to draw the marker while the forge's chunk is unloaded.
public readonly struct ForgeMarkerInfo
{
    public readonly int ReactivateDay;
    public readonly int Level;
    public readonly EUpgradeSlot Slot;

    public ForgeMarkerInfo(int reactivateDay, int level, EUpgradeSlot slot)
    {
        ReactivateDay = reactivateDay;
        Level = level;
        Slot = slot;
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
    // The equipment slots, indexed by the position hash below.
    private static readonly EUpgradeSlot[] Slots =
    {
        EUpgradeSlot.Melee, EUpgradeSlot.Ranged, EUpgradeSlot.Armor,
    };

    // Position-derived slot fallback for a forge whose spawn entry didn't pin one
    // (ForgeSpawnEntry.forgeSlot == None): a stable per-position hash so a single
    // shared fixture still yields varied forges. Resolved once at bake time into
    // ForgeSimState.Slot; not called again at runtime (the forge reads its stored slot).
    public static EUpgradeSlot SlotFor(Vector3 worldPos)
    {
        return Slots[SlotHash(worldPos) % Slots.Length];
    }

    // Resolve the upgrade a forge with the given fixed `slot` offers on `today`:
    // filter the pool to entries ELIGIBLE for the slot (their upgradeSlot flags
    // include it), then pick one deterministically by (position, offer-day). Null
    // when nothing in the pool is eligible for the slot.
    public static StatusEffectData Resolve(Godot.Collections.Array<StatusEffectData> pool, Vector3 worldPos, int today, int reactivateDay, EUpgradeSlot slot)
    {
        if (pool == null || pool.Count == 0)
        {
            return null;
        }
        // Count eligible first so the modulo indexes only eligible entries — no
        // filtered-list allocation on this map-marker / offer path.
        int eligible = 0;
        for (int i = 0; i < pool.Count; i++)
        {
            if (pool[i] != null && (pool[i].upgradeSlot & slot) != 0)
            {
                eligible++;
            }
        }
        if (eligible == 0)
        {
            return null;
        }
        int offerDay = Math.Max(today, reactivateDay);
        int pick = OfferHash(worldPos, offerDay) % eligible;
        for (int i = 0; i < pool.Count; i++)
        {
            if (pool[i] == null || (pool[i].upgradeSlot & slot) == 0)
            {
                continue;
            }
            if (pick-- == 0)
            {
                return pool[i];
            }
        }
        return null;
    }

    // Day-independent position hash for the forge's fixed slot (distinct from
    // OfferHash, which folds in the day to roll the offered upgrade).
    public static int SlotHash(Vector3 pos)
    {
        unchecked
        {
            int h = 23;
            h = h * 31 + Mathf.RoundToInt(pos.X);
            h = h * 31 + Mathf.RoundToInt(pos.Y);
            h = h * 31 + Mathf.RoundToInt(pos.Z);
            return h & 0x7fffffff;
        }
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
