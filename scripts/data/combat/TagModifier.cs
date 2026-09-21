using Godot;

// A modifier that scales anything arriving with one EHitTag — always
// multiplicative. On a hit tag it scales that hit at its application site
// (Fire 0.5 halves fire damage); on a status family it scales the buildup
// feeding that family (Poisoned 0.5 halves poison buildup). See EHitTag.
[Tool]
[GlobalClass]
public partial class TagModifier : Modifier
{
	private EHitTag _tag;
	[Export] public EHitTag tag
	{
		get => _tag;
		set
		{
			if (_tag == value) { return; }
			_tag = value;
			EmitChanged();
		}
	}
}
