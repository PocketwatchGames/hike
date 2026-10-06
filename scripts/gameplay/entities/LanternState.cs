// Carryable-lantern runtime state. Distinct from TorchSimState, which is the
// world-placed torch prop. A lantern is lit exactly while it is equipped, so
// there is no lit state of its own: the Use tap unequips it, and water, heavy
// rain or an empty tank unequip it too (Player.DouseCarriedLantern /
// TickLanternFuel). An empty lantern can't be equipped (Inventory.Equip).
public class LanternState : ItemState
{
	public override LanternData data => _lanternData;
	private readonly LanternData _lanternData;

	// Remaining burn budget, in sim-ms. Counts down only while equipped
	// (Player.TickLanternFuel) and is refilled at a campfire or a fountain
	// (Refuel). Ignored entirely when the lantern has unlimited fuel.
	public long FuelRemainingMs;

	public override void WriteSubclassState(System.IO.BinaryWriter w)
	{
		w.Write(FuelRemainingMs);
	}

	public override void ReadSubclassState(System.IO.BinaryReader r)
	{
		FuelRemainingMs = r.ReadInt64();
	}

	public LanternState(LanternData d) : base(d)
	{
		_lanternData = d;
		FuelRemainingMs = d.BurnTimeMs;
	}

	// Whether the lantern can be lit / stay lit: either it burns forever or it
	// still has fuel left. The relight gate reads this.
	public bool HasFuel => !_lanternData.HasLimitedFuel || FuelRemainingMs > 0;

	// Recharge to a full tank — the sunrise / respawn / fountain refuel.
	public void Refuel()
	{
		FuelRemainingMs = _lanternData.BurnTimeMs;
	}

	// Spends up to `ms` of the fuel budget, clamping the tank at 0 — the
	// continuous while-lit burn, and a fuel-costed action (a lantern spell
	// cast), where a near-empty lantern still pays what it can and bottoms out.
	// No-op for unlimited lanterns.
	public void BurnFuel(long ms)
	{
		if (!_lanternData.HasLimitedFuel)
		{
			return;
		}
		FuelRemainingMs = System.Math.Max(0, FuelRemainingMs - ms);
	}
}
