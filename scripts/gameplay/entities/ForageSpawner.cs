using Godot;

// Invisible, persistent anchor for a forageable resource (a mushroom, an herb
// clump). While ripe it presents a transient Loot pickup at its position; when
// that pickup is collected it goes inert until RegrowDays later (tracked on the
// inherited RegrowDay deadline of its sim state). Re-arming is event-driven off
// Sim.OnNewDay — the same daily-station pattern as Fountain/Forge — so a
// harvested patch regrows at the sunrise its deadline passes.
//
// The spawner keeps Loot dumb: the mushroom is a plain transient Loot (with the
// full magnet / bob / pickup feel), and only the persistence + regrow timer live
// here on the anchor. The pickup is NOT stored in WorldState — the spawner is the
// single persistent record and re-creates the pickup on every stream-in while
// ripe.
[GlobalClass]
public partial class ForageSpawner : Node3D, IWorldEntity
{
    private ForageSpawnerSimState _simState;
    private Sim _world;
    // The pickup currently presented, if any. Null / freed once harvested or
    // streamed out; PresentIfRipe re-creates it.
    private Loot _liveMushroom;

    public void OnSpawned(Sim sim) { }

    public override void _ExitTree()
    {
        if (_world != null)
        {
            _world.OnNewDay -= HandleNewDay;
        }
    }

    // The patch regrows at sunrise: re-present the pickup once the day rolls past
    // the regrow deadline.
    private void HandleNewDay(int day)
    {
        PresentIfRipe();
    }

    // Spawn the pickup if the patch is ripe and isn't already showing one. The
    // presented Loot is transient — not added to WorldState — so on harvest it
    // simply despawns and this spawner (the sole persistent record) re-presents
    // when its deadline next passes.
    private void PresentIfRipe()
    {
        if (_world == null || _simState == null || _simState.Item == null)
        {
            return;
        }
        if (IsInstanceValid(_liveMushroom))
        {
            return;
        }
        if (!_simState.IsRegrown(_world.DayNumber))
        {
            return;
        }
        _liveMushroom = _world.SpawnForageLoot(_simState.Item, _simState.WorldPosition, _simState);
    }

    public static ForageSpawner Create(Sim sim, ForageSpawnerSimState data)
    {
        var instance = data.Scene.Instantiate<ForageSpawner>();
        instance.Position = data.WorldPosition;
        instance._simState = data;
        instance._world = sim;
        sim.AddChild(instance);
        instance.PresentIfRipe();
        sim.OnNewDay += instance.HandleNewDay;
        return instance;
    }
}
