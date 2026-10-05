using System.Collections.Generic;
using Godot;

// Death sacks: a fallen member's gear, left where they died. The party never loses
// a member — death wakes them at the campfire — so walking back to the sack is the
// whole price of dying.
public partial class Sim
{
    // Every unopened sack in the world, resident or not. The map draws a grave at
    // each, and a sack is almost never resident while the map is open (the party
    // wakes far away), so this can't be a node-registered LiveMapMarker. Derived from
    // the entity buckets — rebuilt in Initialize, after a save's buckets are applied —
    // and kept current by DropDeathSack / OpenDeathSack.
    private readonly List<DeathSackSimState> _deathSacks = new();
    public IReadOnlyList<DeathSackSimState> DeathSacks => _deathSacks;

    private void IndexDeathSacks()
    {
        _deathSacks.Clear();
        foreach (EntitySimState state in _worldState.AllChunkEntities())
        {
            if (state is DeathSackSimState sack)
            {
                _deathSacks.Add(sack);
            }
        }
    }

    // Empty `inventory`'s death drop into a new sack at `position`. Nothing is taken
    // if no sack can be made, so a missing scene costs an error, never the gear.
    public void DropDeathSack(Vector3 position, Inventory inventory)
    {
        if (_worldState == null || inventory == null)
        {
            return;
        }
        PackedScene scene = SimData?.deathSackScene;
        if (scene == null)
        {
            GD.PushError("[DeathSack] SimData.deathSackScene is not set; the fallen member keeps their gear.");
            return;
        }
        List<ItemState> contents = inventory.TakeDeathDrop();
        if (contents.Count == 0)
        {
            return;
        }
        var sack = new DeathSackSimState(position, scene);
        sack.Contents.AddRange(contents);
        _worldState.AddEntity(sack);
        _deathSacks.Add(sack);

        Vector3I coord = WorldToChunkCoord(position);
        if (_activeEntities.TryGetValue(coord, out List<Node3D> entities))
        {
            Node3D node = sack.CreateEntity(this);
            if (node != null)
            {
                RegisterEntity(node, entities, sack);
            }
        }
    }

    // Spill the sack's contents as ordinary pickups and remove it for good. The
    // pickups are dropped loot, so the next sunrise sweeps whatever is left lying.
    public void OpenDeathSack(DeathSack node, DeathSackSimState sack)
    {
        if (sack == null)
        {
            return;
        }
        Vector3 origin = (node?.GlobalPosition ?? sack.WorldPosition) + Vector3.Up;
        foreach (ItemState item in sack.Contents)
        {
            SpawnLoot(origin, BuildEjectImpulse(), item);
        }
        sack.Contents.Clear();
        _deathSacks.Remove(sack);
        if (node != null)
        {
            DestroyEntity(node);
        }
        else
        {
            _worldState?.RemoveEntity(sack);
        }
    }
}
