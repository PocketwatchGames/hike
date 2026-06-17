using Godot;

[GlobalClass]
public partial class SpikeTrapData : Resource
{
    // Damage applied to each body in the trigger area when spikes emerge,
    // and to any body that enters while spikes are out.
    [Export] public DamageData damageData;

    // Seconds between trigger and spikes-out. The warning sound plays at the
    // start of this window so a paying-attention player has a beat to react.
    [Export] public float warningDelay = 0.5f;

    // Seconds spikes remain extended (and dangerous on entry).
    [Export] public float activeDuration = 1.5f;

    // Seconds after spikes retract before the trap re-arms. Inert during this
    // window so a player can't get hit twice in immediate succession by
    // walking back through.
    [Export] public float resetTime = 5f;

    // Discoverable.prominence while the trap is hidden and armed — how easily
    // the player spots the unsprung trap. Applied to the host Discoverable at
    // spawn (SpikeDeployer._Ready) so the placement owns this, not the scene.
    [Export] public float armedProminence = 0.6f;

    // Discoverable.prominence the trap jumps to the moment it fires: a sprung
    // trap with spikes out is far more conspicuous than the hidden armed one,
    // so the player notices it from much farther away afterward. Applied
    // alongside the immediate ForceDiscover so it also governs re-perception
    // if discovery is ever reset (re-arm, save/load).
    [Export] public float firedProminence = 2f;

    // One-shot fx scenes. Wired in the .tscn; any may be null.
    [Export] public PackedScene warningEffect;
    [Export] public PackedScene emergeEffect;
    [Export] public PackedScene retractEffect;
    [Export] public PackedScene disarmEffect;
}
