using Godot;

// Shared base for entities that become inert when used/harvested and re-arm
// after an in-world cooldown. One serialized deadline (`RegrowAtClock`) drives
// every station: fountains and forges, berry trees and forage spawners.
//
// `RegrowAtClock` is a WorldState.WorldClockDays value: the moment the entity is
// available again. 0 = ready now. A cooldown is counted in SUNRISES, so a station
// used any time today is back at the next dawn whatever the hour — a night's
// sleep always refills it. Stamp it with StartRegrow on use; resident
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

    // Go inert until the `dawns`th sunrise from now (whole clock values are
    // sunrises, so flooring finds the one this day started on).
    public void StartRegrow(double nowClock, int dawns)
    {
        RegrowAtClock = System.Math.Floor(nowClock) + dawns;
    }
}
