using Godot;

// One entry in a stat-modifier list — a per-stat value the receiver folds
// into the matching stat's composition. The op (multiply / add) is implicit
// per `stat`, hardcoded on the receiver side via StatModifierUtil.IsAdditive,
// so authors only have to think about "what stat" + "what value". Default
// `value = 1` is the neutral identity for the multiplicative stats (the
// common case); the additive stats (Camouflage / MaxStamina / ColdResist /
// HeatResist / MaxHealth / MaxArmor — see StatModifierUtil.IsAdditive)
// author the value explicitly and treat the default-1 as a benign +1 the
// author will notice during tuning rather than an immediate failure mode.
//
// Multiple sources (inherent data + equipped armor + active status effects)
// stack per stat — multiplicatively for multiply-stats, additively for
// add-stats — across however many entries each source contributes.
//
// `stat` should be a single bit. Multi-bit values would erroneously match
// multiple lookups; authoring tooling treats each tag as a separate entry.
[Tool]
[GlobalClass]
public partial class StatModifier : Resource
{
	private EStat _stat;
	[Export, CompactFlags] public EStat stat
	{
		get => _stat;
		set
		{
			if (_stat == value) { return; }
			_stat = value;
			EmitChanged();
		}
	}

	// Numeric magnitude. Multiplicative stats: 1 neutral, 0 immunity, <1
	// reduce, >1 amplify. Additive stats: 0 neutral, +/- shifts the
	// underlying value. See StatModifierUtil.IsAdditive for the per-stat op.
	[Export] public float value = 1f;
}
