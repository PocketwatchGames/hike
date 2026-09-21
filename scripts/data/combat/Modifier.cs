using Godot;

// One entry in a modifier list — a StatModifier (a character stat) or a
// TagModifier (a hit tag or status family). Both share one authored list per
// source so a cloak that resists fire and adds stamina is one list; the
// owner's ModifierSet splits them for the two fold paths.
//
// [Tool] because every owner that is [Tool] reaches this through a typed
// [Export] (see the Key Conventions note in CLAUDE.md).
[Tool]
[GlobalClass]
public abstract partial class Modifier : Resource
{
	// Multiplicative targets: 1 neutral, 0 immunity, <1 reduce, >1 amplify.
	// Additive stats: 0 neutral, +/- shifts the value.
	[Export] public float value = 1f;
}
