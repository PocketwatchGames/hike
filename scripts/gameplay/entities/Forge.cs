using System;
using System.Collections.Generic;
using Godot;

// Smithing forge. On interact (while off cooldown) it opens the ForgeScreen
// offering a few weapons/armor drawn at random from SimData.forgeItems, each
// minted at this forge's Level. Selecting one creates an ephemeral, leveled
// item and equips it on the player; the forge then goes inert until the next
// in-world sunrise — a sim-clock deadline persisted on the sim state so the
// cooldown survives chunk streaming and save/load.
//
// Distinct from the Campfire cooking station: no lit/doused state, no jobs.
[GlobalClass]
public partial class Forge : Node3D, IInteractive, IWorldEntity
{
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    [Export] private Discoverable _discoverable;
    [Export] private Node3D _hudNode;
    // How many weapon/armor choices the forge offers per use.
    [Export] private int _choiceCount = 3;

    private ForgeSimState _simState;
    private World _world;
    private readonly Random _rng = new();

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

    public void OnSpawned(World world) { }

    public bool CanInteract()
    {
        // Inert until the sim clock passes the reactivation deadline (stamped to
        // the next sunrise on use). 0 = ready.
        ulong now = World.Current?.GameTimeMs ?? 0;
        return _simState == null || now >= _simState.ReactivateMs;
    }

    public bool CanActorInteract(Player player)
    {
        return CanInteract() && (_discoverable == null || _discoverable.IsDiscovered);
    }

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        if (!CanActorInteract(player))
        {
            return null;
        }
        return _actions != null && _actions.Count > 0 ? _actions : null;
    }

    public void Complete(int actionIndex)
    {
        if (!CanInteract())
        {
            return;
        }
        GameClient gc = GameClient.Current;
        Player player = gc?.Player;
        if (gc == null || player == null)
        {
            return;
        }
        List<ItemState> choices = RollChoices();
        if (choices.Count == 0)
        {
            return;
        }
        gc.OpenForgeScreen(choices, chosen =>
        {
            if (chosen == null)
            {
                return;
            }
            player.EquipItem(chosen);
            BeginCooldown();
        });
    }

    // Draw up to _choiceCount distinct items from the pool, each minted at the
    // forge's level as an ephemeral (sunrise-expiring) instance.
    private List<ItemState> RollChoices()
    {
        var result = new List<ItemState>();
        Godot.Collections.Array<ItemData> pool = _world?.SimData?.forgeItems;
        if (pool == null || pool.Count == 0)
        {
            return result;
        }
        int level = _simState?.Level ?? 0;
        var indices = new List<int>(pool.Count);
        for (int i = 0; i < pool.Count; i++)
        {
            indices.Add(i);
        }
        int take = Math.Min(_choiceCount, indices.Count);
        for (int n = 0; n < take; n++)
        {
            int pick = _rng.Next(indices.Count);
            ItemData data = pool[indices[pick]];
            indices.RemoveAt(pick);
            if (data == null)
            {
                continue;
            }
            ItemState state = data.CreateState();
            state.level = level;
            state.ephemeral = true;
            result.Add(state);
        }
        return result;
    }

    private void BeginCooldown()
    {
        if (_simState == null)
        {
            return;
        }
        _simState.ReactivateMs = World.Current?.NextSunriseMs() ?? 0;
    }

    public static Forge Create(World world, ForgeSimState data)
    {
        var instance = data.Scene.Instantiate<Forge>();
        instance.Position = data.WorldPosition;
        instance._simState = data;
        instance._world = world;
        world.AddChild(instance);
        return instance;
    }
}
