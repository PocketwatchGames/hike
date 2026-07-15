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
        var simState = new LootSimState(position, item);
        return FinishSpawnLoot(simState, position, impulse);
    }

    // Spawn loot from a pre-built ItemState — carries its stack count and any
    // permanent mods already composed onto its `statusEffects` controller (mob
    // loot that drops a modded item; see Mob.EjectLoot). The state IS the carried
    // item, so pickup deposits it as-is rather than synthesizing a fresh one.
    public Loot SpawnLoot(Vector3 position, Vector3 impulse, ItemState item)
    {
        if (item?.data == null)
        {
            return null;
        }
        var simState = new LootSimState(position, item.data) { Item = item };
        return FinishSpawnLoot(simState, position, impulse);
    }

    // Shared tail for the SpawnLoot overloads: instantiate the Loot scene, place
    // it in the world entity bookkeeping, and register it. Returns null if no
    // loot scene is configured.
    private Loot FinishSpawnLoot(LootSimState simState, Vector3 position, Vector3 impulse)
    {
        PackedScene scene = GameClient.Current?.lootScene;
        if (scene == null)
        {
            return null;
        }
        ComposeFairyBoons(simState);
        simState.Dropped = true;
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

    // Spawn a ForageSpawner's presented pickup (a mushroom). Unlike the SpawnLoot
    // overloads this is TRANSIENT — the sim state is NOT added to WorldState, so
    // the spawner remains the single persistent record and re-creates the pickup
    // on each stream-in while ripe (a persisted copy would double up on reload).
    // The pickup still rides the chunk's active-entity list so it's freed on
    // eviction; its ForageLootSimState re-arms the spawner's regrow deadline when
    // collected. Returns null if no loot scene is configured.
    public Loot SpawnForageLoot(ItemData item, Vector3 position, ForageSpawnerSimState owner)
    {
        PackedScene scene = GameClient.Current?.lootScene;
        if (item == null || scene == null)
        {
            return null;
        }
        var simState = new ForageLootSimState(position, item, owner);
        Loot loot = Loot.Create(this, simState, scene, Vector3.Zero);

        Vector3I coord = WorldToChunkCoord(position);
        if (!_activeEntities.TryGetValue(coord, out List<Node3D> entities))
        {
            entities = new List<Node3D>();
            _activeEntities[coord] = entities;
        }
        RegisterEntity(loot, entities, simState);
        return loot;
    }

    // Compose the fairy corpse's candidate boons onto its carried ItemState so
    // one can be applied (and chosen by the player) on use. Lives in the shared
    // spawn tail so every drop path gets it — a mob kill (ItemDescriptor loot,
    // which already created the state), the bare-ItemData overload, and player
    // drops — since the boons live on the per-instance state, not the shared
    // ConsumableData. Eager-creates the state for the lazy (ItemData) path so
    // pickup deposits the composed stack rather than synthesizing a fresh one.
    // Idempotent: skips loot that isn't the fairy corpse or already has boons.
    private void ComposeFairyBoons(LootSimState simState)
    {
        SimData simData = SimData;
        if (simData == null || simState.Data != simData.fairyLoot || simData.fairyBoons.Count == 0)
        {
            return;
        }
        simState.Item ??= simData.fairyLoot.CreateState();
        ItemState state = simState.Item;
        if (state.possibleBoons.Count > 0)
        {
            return;
        }
        foreach (BoonData boon in simData.fairyBoons)
        {
            if (boon != null)
            {
                state.possibleBoons.Add(boon);
            }
        }
    }

    // Spawn an arrow drop at the impact point of a hitscan shot. The arrow
    // binds back to the firing WeaponState — recovering it (player pickup, or
    // the weapon's central ammoRechargeSeconds timer reclaiming it oldest-first)
    // routes through ArrowLootSimState and returns 1 ammo to the source weapon.
    // The weapon also tracks the arrow in its outstandingArrows list so the
    // binding survives the player dropping the bow (the weapon instance lives
    // in inventory and outlives the bow's equip state).
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
        simState.Dropped = true;
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

    // Spawn a mob from a MobDescriptor at `position` right now and return the live
    // Mob node — the on-demand analog of the chunk-streaming drain path, used for
    // player summons (the summoner weapon). The sim state is registered in
    // WorldState so the mob is bookkept and persisted like any other; the node
    // is created synchronously via Mob.Create (which parents it + runs _Ready),
    // then registered into the chunk's active-entity list. Caller is expected
    // to be standing in a loaded chunk (a summon lands within aim range), so
    // the target chunk is resident. Returns null if the descriptor has no scene.
    public Mob SpawnMob(MobDescriptor descriptor, Vector3 position)
    {
        MobSimState simState = descriptor?.CreateState(position, 0f);
        if (simState == null)
        {
            return null;
        }
        _worldState.AddEntity(simState);
        Mob mob = Mob.Create(this, simState);

        Vector3I coord = WorldToChunkCoord(position);
        if (!_activeEntities.TryGetValue(coord, out List<Node3D> entities))
        {
            entities = new List<Node3D>();
            _activeEntities[coord] = entities;
        }
        RegisterEntity(mob, entities, simState);

        return mob;
    }

    // Transient sibling of SpawnMob: creates a live mob node WITHOUT recording
    // its sim state in WorldState. For ambient spawners (the night gellies) that
    // want a population computed live around the player rather than persisted —
    // so these mobs vanish with their chunk on eviction and never accumulate or
    // re-materialize the way a worldgen-placed mob does. `conditions` is stamped
    // onto the sim state so the off-condition cleanup can fade them when their
    // window ends (Night mobs at dawn). `level` raises the mob's difficulty tier
    // (2^level health/armor/damage) but never below the descriptor's authored
    // floor, so an ambient spawner can scale toughness (e.g. by time of night).
    // Spawns only onto an already-resident entity chunk (whose active-entity list
    // frees the node on eviction); returns null if the descriptor has no scene or
    // that chunk isn't loaded — callers pass a position they've already confirmed
    // has resident ground.
    public Mob SpawnMobTransient(MobDescriptor descriptor, Vector3 position, ESpawnConditions conditions, int level = 0)
    {
        // Raise the descriptor's authored floor to the ambient spawner's tier (never
        // lower it); the resolved level scales the mob's vitals at construction.
        int spawnLevel = descriptor != null ? Mathf.Max(descriptor.level, level) : 0;
        MobSimState simState = descriptor?.CreateState(position, 0f, levelOverride: spawnLevel);
        if (simState == null)
        {
            return null;
        }
        simState.SpawnConditions = conditions;

        Vector3I coord = WorldToChunkCoord(position);
        if (!_activeEntities.TryGetValue(coord, out List<Node3D> entities))
        {
            return null;
        }
        Mob mob = Mob.Create(this, simState);
        RegisterEntity(mob, entities, simState);
        return mob;
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
        if (dugBlock?.digItem != null)
        {
            SpawnLoot(position + Vector3.Up * DIG_YIELD_POP_HEIGHT, BuildDigYieldImpulse(), dugBlock.digItem);
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
