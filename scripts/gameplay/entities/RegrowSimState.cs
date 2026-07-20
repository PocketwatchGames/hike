using Godot;

// Shared base for entities that become inert when used/harvested and re-arm on a
// future in-world day. One serialized deadline (`RegrowDay`) drives every daily
// and multi-day station: fountains and forges (re-arm next sunrise, +1 day),
// berry trees and forage spawners (regrow after an authored number of days).
//
// `RegrowDay` is a `Sim.DayNumber` value: the day on/after which the entity is
// available again. 0 = ready now. Stamp it to `DayNumber + N` on use; the entity
// re-arms at the next sunrise the deadline has passed (nodes subscribe to
// `Sim.OnNewDay` to flip their ready/inert visual in place). Persisted so the
// cooldown survives chunk eviction and save/load.
public abstract class RegrowSimState : EntitySimState
{
    public int RegrowDay;

    protected RegrowSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    // True once the world day has reached this entity's regrow deadline.
    public bool IsRegrown(int dayNumber) => dayNumber >= RegrowDay;
}
