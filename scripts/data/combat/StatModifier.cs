using Godot;

// A modifier that targets one character stat (MoveSpeed, MaxHealth, …). The op
// (multiply / add) is implicit per stat — StatModifierUtil.IsAdditive — so
// authors only pick "what stat" + "what value". The default `value = 1` is the
// multiplicative identity; an additive stat authors its value explicitly.
[Tool]
[GlobalClass]
public partial class StatModifier : Modifier
{
	private EStat _stat;
	[Export] public EStat stat
	{
		get => _stat;
		set
		{
			if (_stat == value) { return; }
			_stat = value;
			EmitChanged();
		}
	}
}
