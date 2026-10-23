using Godot;

// Runtime state shared by every piece of gear: its durability. Wear is spent by
// the player only, at the moment of use:
//   weapon       — authored on its events: ItemEvent.durabilityCostOnHit when
//                  the attack first strikes a target, the UseDurability flag
//                  (ItemEvent.durabilityCost) as the event fires — a ranged
//                  weapon's every shot
//   shield       — the damage a block absorbs from the guard, or a parry negates
//   armor        — the damage the armor pool soaks, split across worn pieces
// A broken piece stays equipped: armor stops recharging, a shield can neither
// block nor parry, and a weapon can't fire at all — the melee slot swings bare
// hands instead. Durability comes back only by repair at a crafting bench
// (CraftQueue).
public class GearState : ItemState
{
	public enum ECondition
	{
		Sound,
		Worn,
		Broken,
	}

	public float durability;

	public override GearData data => _data;
	private readonly GearData _data;

	public GearState(GearData d) : base(d)
	{
		_data = d;
		durability = d?.maxDurability ?? 0;
	}

	public bool Wears => _data != null && _data.maxDurability > 0;

	public bool IsBroken => Wears && durability <= 0f;

	// Worn = under `warningFraction` of max durability (SimData
	// .durabilityWarningFraction), the HUD warning and its one-shot cue.
	public ECondition Condition(float warningFraction)
	{
		if (IsBroken)
		{
			return ECondition.Broken;
		}
		if (Wears && durability < warningFraction * _data.maxDurability)
		{
			return ECondition.Worn;
		}
		return ECondition.Sound;
	}

	// Per-item wear scale from the mods composed onto this piece
	// (ItemDescriptor.statusEffects carrying an EStat.DurabilityWear modifier).
	public float ComposeWearMultiplier()
	{
		return statusEffects.FoldStat(EStat.DurabilityWear, 1f);
	}

	// Spend `amount` of durability (uses, or damage for armor and shields),
	// scaled by this piece's wear mods.
	public void Wear(float amount = 1f)
	{
		if (!Wears || amount <= 0f)
		{
			return;
		}
		durability = Mathf.Max(0f, durability - amount * ComposeWearMultiplier());
	}

	// Below full durability, with an authored GearData.repair cost to restore it.
	public bool IsRepairable => Wears && durability < _data.maxDurability
		&& _data.repair != null && _data.repair.Count > 0;

	// What restoring this piece to full costs right now: each GearData.repair row
	// scaled by the fraction of durability missing, rounded up — a scratched piece
	// costs one of each, a broken one the full list. Empty when not repairable.
	public System.Collections.Generic.List<MaterialCount> ComposeRepairCost()
	{
		var cost = new System.Collections.Generic.List<MaterialCount>();
		if (!IsRepairable)
		{
			return cost;
		}
		float missing = MissingFraction();
		foreach (MaterialCount row in _data.repair)
		{
			if (row?.item == null || row.count <= 0)
			{
				continue;
			}
			cost.Add(new MaterialCount
			{
				item = row.item,
				count = Mathf.CeilToInt(row.count * missing),
			});
		}
		return cost;
	}

	// In-world hours restoring this piece to full takes: GearData.repairHours
	// scaled by the fraction of durability missing.
	public double ComposeRepairHours()
	{
		return IsRepairable ? _data.repairHours * MissingFraction() : 0.0;
	}

	private float MissingFraction() => 1f - durability / _data.maxDurability;

	public void Repair()
	{
		if (Wears)
		{
			durability = _data.maxDurability;
		}
	}

	public override void WriteSubclassState(System.IO.BinaryWriter w)
	{
		w.Write(durability);
	}

	public override void ReadSubclassState(System.IO.BinaryReader r)
	{
		durability = r.ReadSingle();
	}
}
