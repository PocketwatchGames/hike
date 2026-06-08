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

    // One-shot fx scenes. Wired in the .tscn; any may be null.
    [Export] public PackedScene warningEffect;
    [Export] public PackedScene emergeEffect;
    [Export] public PackedScene retractEffect;
    [Export] public PackedScene disarmEffect;
}
