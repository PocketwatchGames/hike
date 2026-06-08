using System.Collections.Generic;
using Godot;

// World — spawn factories for transient, non-streamed entities created on
// demand by gameplay (loot drops, recovered arrows, player-dropped items,
// footprint decals). Distinct from the chunk-driven streaming in
// World.EntityStreaming.cs. See World.cs for the file split.
public partial class World
{
    public Loot SpawnLoot(Vector3 position, Vector3 impulse, ItemData item)
    {
        if (item == null)
        {
            return null;
        }
        GameClient gc = GameClient.Current;
        PackedScene scene = gc?.lootScene;
        if (scene == null)
        {
            return null;
        }
        var simState = new LootSimState(position, item);
        _worldState.AddEntity(simState);
        Loot loot = Loot.Create(this, simState, scene, impulse);

        Vector3I coord = WorldToChunkCoord(position);
        if (!_activeEntities.TryGetValue(coord, out List<Node3D> entities))
        {
            entities = new List<Node3D>();
            _activeEntities[coord] = entities;
        }
        RegisterEntity(loot, entities, simState);

        return loot;
    }

    // Spawn an arrow drop at the impact point of a hitscan shot. The arrow
    // binds back to the firing WeaponState — recovering it (player pickup,
    // 30s LootData.removeTimeMs timeout) routes through ArrowLootSimState
    // and returns 1 ammo to the source weapon. The weapon also tracks the
    // arrow in its outstandingArrows list so the binding survives the
    // player dropping the bow (the weapon instance lives in inventory and
    // outlives the bow's equip state).
    public Loot SpawnArrowLoot(Vector3 position, Vector3 impulse, ArrowLootData data, WeaponState sourceWeapon)
    {
        if (data == null || sourceWeapon == null)
        {
            return null;
        }
        GameClient gc = GameClient.Current;
        PackedScene scene = gc?.lootScene;
        if (scene == null)
        {
            return null;
        }
        var simState = new ArrowLootSimState(position, data, sourceWeapon);
        _worldState.AddEntity(simState);
        sourceWeapon.RegisterArrow(simState);
        Loot loot = Loot.Create(this, simState, scene, impulse);

        Vector3I coord = WorldToChunkCoord(position);
        if (!_activeEntities.TryGetValue(coord, out List<Node3D> entities))
        {
            entities = new List<Node3D>();
            _activeEntities[coord] = entities;
        }
        RegisterEntity(loot, entities, simState);

        return loot;
    }

    // Spawn a pickup carrying a specific ItemState (player-dropped item path).
    // requireInteract latches the dropped pile into "press Interact to pick
    // up" mode so the player doesn't immediately re-pick up what they just
    // threw. Loot.Create swaps in the item's worldSprite on spawn.
    public Loot DropItem(ItemState item, Vector3 position, Vector3 impulse, bool requireInteract = false)
    {
        if (item == null || item.data == null)
        {
            return null;
        }
        GameClient gc = GameClient.Current;
        PackedScene scene = gc?.lootScene;
        if (scene == null)
        {
            return null;
        }

        var simState = new LootSimState(position, item.data);
        simState.Item = item;
        simState.RequireInteract = requireInteract;
        _worldState.AddEntity(simState);
        Loot pickup = Loot.Create(this, simState, scene, impulse);

        Vector3I coord = WorldToChunkCoord(position);
        if (!_activeEntities.TryGetValue(coord, out List<Node3D> entities))
        {
            entities = new List<Node3D>();
            _activeEntities[coord] = entities;
        }
        RegisterEntity(pickup, entities, simState);

        return pickup;
    }

    // Spawn a transient footprint decal at `position`. Parented directly to
    // World (not registered in _activeEntities) because footprints have no
    // persistent sim state and self-despawn via QueueFree once their fade
    // hits zero. The two shared scenes (player / mob) live on SimData;
    // `gated` picks the perception-gated variant for mob-laid prints.
    // `yaw` rotates the decal box around Y so the texture aligns with the
    // direction the actor is facing — toe of the print points where they
    // were walking.
    public Footprint SpawnFootprint(Texture2D texture, Vector2 size, Color tint, Vector3 position, float yaw, float durationSeconds, bool gated)
    {
        SimData sim = SimData;
        if (sim == null || texture == null)
        {
            return null;
        }
        PackedScene scene = gated ? sim.FootprintDiscoverable : sim.FootprintVisible;
        if (scene == null)
        {
            return null;
        }
        Footprint fp = scene.Instantiate<Footprint>();
        // Set transform before AddChild so the Discoverable's _Ready light
        // sample (perception tick) reads the correct world-space coordinate
        // on the first tick rather than seeing origin.
        fp.Position = position;
        fp.Rotation = new Vector3(0f, yaw, 0f);
        AddChild(fp);
        fp.Initialize(this, texture, size, tint, durationSeconds);
        return fp;
    }
}
