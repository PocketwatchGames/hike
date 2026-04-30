public class ItemState
{
	public virtual ItemData data => _data;
	private readonly ItemData _data;

	public int stackCount;

	public ulong cooldownExpireMs;

	public ItemState(ItemData d)
	{
		_data = d;
		stackCount = 1;
	}

	public bool IsSameKind(ItemState other)
	{
		return other != null && other.data == _data;
	}

	public int RemainingStackSpace()
	{
		if (_data == null)
		{
			return 0;
		}
		return _data.maxStack - stackCount;
	}
}
