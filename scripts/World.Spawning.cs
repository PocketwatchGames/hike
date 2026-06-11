using System;
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

    // Dig at `position`: uncover the nearest un-excavated buried spot within
    // `radius`, or — failing that — force the nearest burrowed/burrowing mob in
    // range to surface and notice `digger`. Returns the dig's result class so
    // the caller can play the matching completion effect. Iterates only LOADED
    // entities (the chunk under the player is resident), staying compatible
    // with the streaming-world rule of never scanning the whole world.
    public EDigResult TryDig(Vector3 position, float radius, Player digger)
    {
        float r2 = radius * radius;

        BuriedSpot bestSpot = null;
        float bestSpotDist = r2;
        foreach (BuriedSpot spot in GetEntities<BuriedSpot>())
        {
            if (spot.Excavated)
            {
                continue;
            }
            float d = spot.GlobalPosition.DistanceSquaredTo(position);
            if (d <= bestSpotDist)
            {
                bestSpotDist = d;
                bestSpot = spot;
            }
        }
        if (bestSpot != null && bestSpot.Dig(digger))
        {
            return bestSpot.ResultClass;
        }

        Mob bestMob = null;
        float bestMobDist = r2;
        foreach (Mob mob in GetEntities<Mob>())
        {
            if (!mob.burrowed && !mob.burrowing)
            {
                continue;
            }
            float d = mob.GlobalPosition.DistanceSquaredTo(position);
            if (d <= bestMobDist)
            {
                bestMobDist = d;
                bestMob = mob;
            }
        }
        if (bestMob != null)
        {
            bestMob.DigUp(digger);
            // A creature clawing out of the ground is a "common" surprise for
            // the shovel's feedback (distinct effect from finding loot/treasure
            // would need a fourth class — not worth it).
            return EDigResult.Common;
        }

        // Nothing buried and nothing burrowed — fall back to the dug block's
        // own yield. Some ground scoops up a material when you dig a bare hole
        // in it (marsh → mud); the block authors what via BlockData.DigItem.
        // Spawn it as loose loot popping out of the hole and report Common so
        // the shovel plays its "found something" cue. Most blocks leave DigItem
        // null and the dig comes up empty.
        BlockData dugBlock = GroundTypeResolver.ResolveBlock(_worldState, position);
        if (dugBlock?.DigItem != null)
        {
            SpawnLoot(position + Vector3.Up * DIG_YIELD_POP_HEIGHT, BuildDigYieldImpulse(), dugBlock.DigItem);
            return EDigResult.Common;
        }

        return EDigResult.Nothing;
    }

    // Loose-loot pop for a bare-ground dig yield: a 45° upward arc on a random
    // horizontal heading so the scooped material tumbles out of the hole. Same
    // arc shape the berry tree / chest ejects use.
    private const float DIG_YIELD_POP_HEIGHT = 0.5f;
    private const float DIG_YIELD_POP_SPEED = 3f;
    private static Vector3 BuildDigYieldImpulse()
    {
        float horizontal = DIG_YIELD_POP_SPEED * Mathf.Cos(Mathf.Pi / 4f);
        float vertical = DIG_YIELD_POP_SPEED * Mathf.Sin(Mathf.Pi / 4f);
        float angle = (float)GD.RandRange(0.0, Mathf.Tau);
        return new Vector3(horizontal * Mathf.Cos(angle), vertical, horizontal * Mathf.Sin(angle));
    }

    // Roll a single SpawnEntryData payload at `position` and materialize its
    // entity (or entities) into the live scene right now, rather than waiting
    // for a chunk reload as the worldgen drain path does. Used by BuriedSpot
    // when the player digs up its payload (chest / loot / mob). If a dug-up
    // entity is a mob, it is emerged + alerted toward `digger`.
    //
    // Materialization diffs the new tail of the spot-chunk's entity list, so
    // single-entity payloads (the buried-item cases) appear immediately; a
    // payload that scatters into neighbouring chunks would only fill those on
    // the normal streaming pass, which buried items never do.
    public void SpawnEntryImmediate(SpawnEntryData entry, Vector3 position, Player digger)
    {
        if (entry == null)
        {
            return;
        }
        Vector3I coord = WorldToChunkCoord(position);
        int before = _worldState.GetEntities(coord)?.Count ?? 0;

        // entry.Spawn appends the new EntitySimState(s) via WorldState.AddEntity.
        entry.Spawn(_worldState, position, new Random(), null);

        List<EntitySimState> states = _worldState.GetEntities(coord);
        if (states == null)
        {
            return;
        }
        if (!_activeEntities.TryGetValue(coord, out List<Node3D> entities))
        {
            entities = new List<Node3D>();
            _activeEntities[coord] = entities;
        }
        for (int i = before; i < states.Count; i++)
        {
            EntitySimState state = states[i];
            Node3D node = state.CreateEntity(this);
            if (node == null)
            {
                continue;
            }
            RegisterEntity(node, entities, state);
            if (node is Mob mob && digger != null)
            {
                mob.DigUp(digger);
            }
        }
    }

    // Lay down a transient footprint mark at `position`. Delegated to
    // FootprintScatter, which batches every print of the same actor texture
    // into one MultiMesh (no per-print Node) and owns the lifetime fade plus
    // the mob-print discovery gate. `gated` requests the perception-gated
    // behavior for mob-laid prints; `yaw` aligns the print with the actor's
    // facing so the toe points where they were walking.
    public void SpawnFootprint(Texture2D texture, Vector2 size, Color tint, Vector3 position, float yaw, float durationSeconds, bool gated)
    {
        _footprintScatter?.Spawn(texture, size, tint, position, yaw, durationSeconds, gated);
    }
}
