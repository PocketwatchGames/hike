public class ConsumableState : ItemState
{
	public override ConsumableData data => _data;
	private readonly ConsumableData _data;

	public bool isActive;

	public ConsumableState(ConsumableData d) : base(d)
	{
		_data = d;
	}

	public virtual void OnEquipped(Player player) { }
	public virtual void OnUnequipped(Player player) { }
}
