using Godot;

// A volumetric hazard that hangs in the world, optionally for a fixed
// duration, and damages anything inside its danger zone. The canonical use
// is a poison cloud spawned by a trap or shattered vial, but the same node
// also serves boss-spawned acid pools or environmental fumaroles —
// composition (start fx, persistent fx, danger zone shape, damage data)
// lives in the .tscn as instanced child scenes, so the whole hazard is
// authored in one scene the artist can open and tune in context.
[GlobalClass]
public partial class GasCloud : Node3D
{
    // Seconds the cloud lives before removing itself. <= 0 means it persists
    // until something else frees the node (a TriggerSource, a vent
    // controller, etc.).
    [Export] public float lifetimeSeconds = 0f;

    // Area3D that does the periodic damage ticking. Wired in the .tscn;
    // disabled when the cloud expires so the final free frame doesn't deal
    // a stray tick after the visuals are gone.
    [Export] private DamageZone _dangerZone;

    private float _ageSeconds;

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
