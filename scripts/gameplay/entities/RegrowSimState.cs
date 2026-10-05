using Godot;

// Shared base for entities that become inert when used/harvested and re-arm
// after an in-world cooldown. One serialized deadline (`RegrowAtClock`) drives
// every station: fountains and forges, berry trees and forage spawners.
//
// `RegrowAtClock` is a WorldState.WorldClockDays value: the moment the entity is
// available again. 0 = ready now. Stamp it with StartRegrow on use; resident
// nodes re-check on Sim.OnDeadlinesSwept to flip their ready/inert visual in
// place. Persisted so the cooldown survives chunk eviction and save/load.
public abstract class RegrowSimState : EntitySimState
{
    public double RegrowAtClock;

    protected RegrowSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    // True once the in-world clock has reached this entity's regrow deadline.
    public bool IsRegrown(double nowClock) => nowClock >= RegrowAtClock;

    // Go inert for `days` of in-world time from now.
    public void StartRegrow(double nowClock, double days)
    {
        RegrowAtClock = nowClock + days;
    }
}
