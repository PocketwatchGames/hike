using Godot;

// A barrel that detonates the first time anything damages it — a player swing,
// a stray arrow, a fire trap, or the blast of another exploding barrel (chain
// reactions come for free, since the blast hits every HurtBox in range,
// including neighbouring barrels').
//
// It stays a plain prop for placement (derives from PropInstance, so the prop
// brush / prop library / subscenes spawn it exactly like any other barrel).
// Breaking is its Destructible's — the intact barrel, the broken shell and the
// scorch mark are that component's two branches, so the remains persist and
// restore like any other breakable. The barrel only adds the blast, fired
// from Destroyed so a spawn that is already broken never re-detonates.
[GlobalClass]
public partial class ExplodingBarrel : PropInstance
{
    // The explosion. Its damage should author friendlyFire — a barrel belongs to
    // no side and hits everyone, neighbouring barrels included.
    [Export] private AreaBurstData _blast;

    // Height above the barrel's origin the explosion is centred at, so the blast
    // originates from the barrel's middle rather than the ground.
    [Export(PropertyHint.Range, "0,3,0.05")] private float _blastHeightOffset = 0.8f;

    public override void _Ready()
    {
        base._Ready();
        if (Destructible != null)
        {
            Destructible.Destroyed += OnDestroyed;
        }
    }

    private void OnDestroyed()
    {
        // Deferred: the hit that set us off can arrive inside the physics flush
        // (a hazard's area-entered signal), where the blast's shape query is not
        // allowed. Captured now, since the barrel node is replaced by its rubble
        // in the same deferred flush. Its own HurtBox may still catch the blast;
        // an already-broken Destructible ignores it.
        Node3D host = Sim.Current ?? GetParent() as Node3D;
        Vector3 at = GlobalPosition + Vector3.Up * _blastHeightOffset;
        AreaBurstData blast = _blast;
        Callable.From(() => AreaBurst.Fire(blast, host, at, null, ETeam.Neutral)).CallDeferred();
    }
}
