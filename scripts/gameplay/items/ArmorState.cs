public class ArmorState : ItemState
{
	public override ArmorData data => _data;
	private readonly ArmorData _data;

	public ArmorState(ArmorData d) : base(d)
	{
		_data = d;
	}
}
