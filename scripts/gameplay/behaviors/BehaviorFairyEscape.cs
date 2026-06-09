using Godot;

// Terminal behavior: triggers the mob's vanish (rise into the sky + fade +
// permanent despawn) on entry and holds still while it plays out. The mob is
// physics-detached and removed by Mob.TickVanish, so once this runs the fairy
// is on its way out — there are no transitions back.
public partial class BehaviorFairyEscape : BehaviorBase
{
    private readonly FairyEscapeBehaviorData _data;

    public BehaviorFairyEscape(FairyEscapeBehaviorData data)
    {
        _data = data;
    }

    public override void OnEnter(Mob me, ulong time)
    {
        me.BeginVanish(_data.ascentHeight, _data.ascentSeconds);
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        // The vanish owns motion from here; just hold still. Mob.TickVanish
        // short-circuits the tick before this even runs again next frame.
        output.speed = 0f;
        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
