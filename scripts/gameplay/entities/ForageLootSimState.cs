using Godot;

// The transient pickup a ForageSpawner presents while ripe. It is NOT persisted
// in WorldState (the spawner is the single persistent record) — the spawner
// re-creates it on stream-in whenever it's ripe. Its only addition over plain
// Loot is a back-reference to the owning spawner: when the pickup is collected,
// OnRemovedFromWorld stamps the spawner's regrow deadline so the mushroom won't
// return until RegrowDays later. Chunk eviction frees the node WITHOUT calling
// this hook, so streaming the world out never counts as a harvest.
public class ForageLootSimState : LootSimState
{
    private readonly ForageSpawnerSimState _owner;

    public ForageLootSimState(Vector3 worldPosition, ItemData data, ForageSpawnerSimState owner)
        : base(worldPosition, data)
    {
        _owner = owner;
    }

    public override void OnRemovedFromWorld()
    {
        if (_owner == null)
        {
            return;
        }
        int today = Sim.Current?.DayNumber ?? 0;
        _owner.RegrowDay = today + Mathf.Max(1, _owner.RegrowDays);
    }
}
