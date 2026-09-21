using Godot;

// A barrel that detonates the first time anything damages it — a player swing,
// a stray arrow, a fire trap, or the blast of another exploding barrel (chain
// reactions come for free, since the blast hits every HurtBox in range,
// including neighbouring barrels').
//
// It stays a plain prop for placement (derives from PropInstance, so the prop
// brush / prop library / subscenes spawn it exactly like any other barrel) but
// adds a HurtBox to receive hits. On detonation it fires its AreaBurst (the blast
// fx + one instant hit), then swaps its own intact model for a broken shell and
// reveals a lingering scorch stain.
[GlobalClass]
public partial class ExplodingBarrel : PropInstance
{
    // Receives incoming hits. Any hit detonates the barrel — there is no health
    // pool, a barrel goes up on the first strike.
    [Export] private HurtBox _hurtBox;

    // Movement collider, disabled on detonation so the player can walk through
    // the flattened remains instead of an invisible full-height barrel.
    [Export] private CollisionShape3D _bodyCollision;

    // Shown until detonation; hidden after.
    [Export] private Node3D _intactModel;

    // Hidden until detonation; the broken shell left behind.
    [Export] private Node3D _brokenModel;

    // Hidden until detonation; the flat scorch mark on the ground (a layer-5
    // ground-stain quad — see GroundStainProjector).
    [Export] private Node3D _scorchStain;

    // The explosion. Its damage should author friendlyFire — a barrel belongs to
    // no side and hits everyone, neighbouring barrels included.
    [Export] private AreaBurstData _blast;

    // Height above the barrel's origin the explosion is centred at, so the blast
    // originates from the barrel's middle rather than the ground.
    [Export(PropertyHint.Range, "0,3,0.05")] private float _blastHeightOffset = 0.8f;

    private bool _exploded;

    public override void _Ready()
    {
        base._Ready();
        if (_hurtBox != null)
        {
            _hurtBox.OnHit = OnHurtBoxHit;
            _hurtBox.PredictHit = _ => new HitPrediction(EHitResult.Object, EDamageTriggerFlags.None);
        }
        if (_brokenModel != null)
        {
            _brokenModel.Visible = false;
        }
        if (_scorchStain != null)
        {
            _scorchStain.Visible = false;
        }
    }

    private void OnHurtBoxHit(HitInfo hit)
    {
        Explode();
    }

    private void Explode()
    {
        if (_exploded)
        {
            return;
        }
        _exploded = true;

        // Deferred: the hit that set us off can arrive inside the physics flush
        // (a hazard's area-entered signal), where the blast's shape query is not
        // allowed.
        Callable.From(FireBlast).CallDeferred();

        // Swap intact barrel for the broken shell + scorch mark.
        if (_intactModel != null)
        {
            _intactModel.Visible = false;
        }
        if (_brokenModel != null)
        {
            _brokenModel.Visible = true;
        }
        if (_scorchStain != null)
        {
            _scorchStain.Visible = true;
        }

        // Stop receiving hits and blocking movement. Deferred because a hit can
        // arrive mid-physics-step (a DamageZone tick), and toggling collision
        // state during the physics flush is unsafe.
        if (_hurtBox != null)
        {
            _hurtBox.OnHit = null;
            _hurtBox.SetDeferred(Area3D.PropertyName.Monitorable, false);
            _hurtBox.SetDeferred(Area3D.PropertyName.Monitoring, false);
        }
        if (_bodyCollision != null)
        {
            _bodyCollision.SetDeferred(CollisionShape3D.PropertyName.Disabled, true);
        }
    }

    private void FireBlast()
    {
        Node3D host = Sim.Current ?? GetParent() as Node3D;
        AreaBurst.Fire(_blast, host, GlobalPosition + Vector3.Up * _blastHeightOffset, this, ETeam.Neutral, _hurtBox?.GetRid());
    }
}
