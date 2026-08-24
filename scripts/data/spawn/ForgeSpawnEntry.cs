using System;
using Godot;

// Places a single smithing forge. Its Level is read from the zone's
// noise-modulated forge band — or, in a painted world, from the difficulty
// layer via SpawnContext.ForgeLevelOverride — and clamped into
// [levelMin, levelMax], then stamped onto the spawned ForgeSimState — scaling the
// upgrades the forge grants and the star pips on its HUD / map marker. Wants flat,
// grassy ground so the station doesn't tilt off a step edge.
[GlobalClass]
public partial class ForgeSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;
    // Pip range the forge is clamped to (0-4, matching the mob level scale so a
    // forge sits at the same tier as monsters in its zone). Level 0 shows no
    // pips and grants the mildest upgrade; the zone level decides where in this
    // range each forge lands, so a tougher zone yields a stronger forge.
    [Export] public int levelMin = 0;
    [Export] public int levelMax = 4;
    // The upgrade slot this forge grants into (which weapon/armor piece it improves,
    // and which upgrades it can offer). None (default) derives a stable slot from the
    // forge's position so a shared fixture still yields varied forges; set it to pin a
    // specific placement to melee / ranged / armor. See ForgeOffer.SlotFor.
    [Export] public EUpgradeSlot forgeSlot = EUpgradeSlot.None;
    // Radius (meters) around the forge where worldgen-painted detail sprites are
    // erased so scattered foliage doesn't share the station's footprint.
    [Export] public float detailSuppressionRadius = 2f;

    public override bool RequireFlatTerrain => true;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        int level = Math.Clamp(context?.ForgeLevel(position) ?? 0, levelMin, levelMax);
        // Resolve the concrete slot once, at bake time: the authored value if pinned,
        // else derived from position. Downstream reads the resolved ForgeSimState.Slot.
        EUpgradeSlot slot = forgeSlot != EUpgradeSlot.None ? forgeSlot : ForgeOffer.SlotFor(position);
        ws.AddEntity(new ForgeSimState(position, scene, level, slot));
        ws.ClearDetailVoxelsWithin(position, detailSuppressionRadius);
    }
}
