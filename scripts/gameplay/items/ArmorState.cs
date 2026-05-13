public class ArmorState : ItemState
{
	public int exp;
	public int level;

	public override ArmorData data => _data;
	private readonly ArmorData _data;

	public ArmorState(ArmorData d) : base(d)
	{
		_data = d;
	}

	// Adds exp and promotes level while the running total has crossed the
	// next threshold in SimData.ExpPerLevel. ArmorData.maxLevel caps how
	// many entries this armor may consume — a piece with maxLevel=0 never
	// levels regardless of the table contents.
	public void AddExp(int amount, Godot.Collections.Array<int> thresholds)
	{
		if (amount <= 0 || _data == null)
		{
			return;
		}
		exp += amount;
		if (thresholds == null)
		{
			return;
		}
		int cap = System.Math.Min(_data.maxLevel, thresholds.Count);
		while (level < cap && exp >= thresholds[level])
		{
			level++;
		}
	}
}
