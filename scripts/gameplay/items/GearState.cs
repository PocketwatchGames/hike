using Godot;

// Runtime state shared by every piece of gear: its durability. Wear is spent by
// the player only, at the moment of use:
//   weapon       — authored on its events: ItemEvent.durabilityCostOnHit when
//                  the attack first strikes a target, the UseDurability flag
//                  (ItemEvent.durabilityCost) as the event fires. An ammo round
//                  whose strike breaks it drops no recoverable arrow
//                  (WeaponState.Wear)
//   shield       — the damage a block absorbs from the guard, or a parry negates
//   armor        — the damage the armor pool soaks, split across worn pieces
// A broken piece keeps working at a reduced level: armor stops recharging, a
// shield can neither block nor parry, a melee weapon only fires its uncharged
// tiers. An ammo weapon never stays broken — it trades capacity instead.
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

	public virtual bool IsBroken => Wears && durability <= 0f;

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
	public virtual void Wear(float amount = 1f)
	{
		if (!Wears || amount <= 0f)
		{
			return;
		}
		durability = Mathf.Max(0f, durability - amount * ComposeWearMultiplier());
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
