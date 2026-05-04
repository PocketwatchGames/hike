using Godot;

// A volumetric hazard that hangs in the world, optionally for a fixed
// duration, and damages anything inside its danger zone. The canonical use
// is a poison cloud spawned by a trap or shattered vial, but the same node
// also serves boss-spawned acid pools or environmental fumaroles —
// composition (start fx, persistent fx, danger zone shape, damage data)
// lives in the .tscn.
[GlobalClass]
public partial class GasCloud : Node3D
{
    // Seconds the cloud lives before removing itself. <= 0 means it persists
    // until something else frees the node (a TriggerSource, a vent
    // controller, etc.).
    [Export] public float lifetimeSeconds = 0f;

    // Optional one-shot fx played the moment the cloud spawns (a burst of
    // gas plume, a shattering vial). Parented to our parent so it survives
    // independently of the cloud.
    [Export] private PackedScene _startFxScene;

    // Optional looping fx that runs for the cloud's lifetime. Parented under
    // the cloud so it follows our transform and is freed with us.
    [Export] private PackedScene _persistentFxScene;

    // Area3D that does the periodic damage ticking. Wired in the .tscn;
    // disabled when the cloud expires so the final free frame doesn't deal
    // a stray tick after the visuals are gone.
    [Export] private DamageZone _dangerZone;

    private Fx _persistentFx;
    private float _ageSeconds;

    public override void _Ready()
    {
        // Defer so Fx.Create's AddChild doesn't trip Godot's "Parent node is
        // busy setting up children" rejection when the cloud is spawned from
        // another node's _Ready (e.g. a trap event handler). Mirrors
        // CarrierLight._Ready.
        CallDeferred(MethodName.SpawnFx);
    }

    private void SpawnFx()
    {
        if (_startFxScene != null)
        {
            Fx.Create(_startFxScene, GetParent() ?? this, GlobalPosition);
        }
        if (_persistentFxScene != null)
        {
            _persistentFx = Fx.Create(_persistentFxScene, this, Vector3.Zero);
        }
    }

    public override void _Process(double delta)
    {
        if (lifetimeSeconds <= 0f)
        {
            return;
        }
        _ageSeconds += (float)delta;
        if (_ageSeconds >= lifetimeSeconds)
        {
            Expire();
        }
    }

    private void Expire()
    {
        if (_dangerZone != null)
        {
            _dangerZone.SetActive(false);
        }
        QueueFree();
    }
}
