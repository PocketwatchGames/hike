using Godot.Collections;

// Composition helpers for StatModifier lists. Two modes:
//
//   * Single-stat compose (ambient stats — MoveSpeed, Vision, Camouflage,
//     ColdResist, …): caller asks for one stat's final value, the helper
//     seeds with the correct neutral identity (1 for multiply, 0 for add)
//     and folds matching entries from each source.
//
//   * Mask fold (hit-side damage tags — Fire | Magical, etc.): caller passes
//     the hit's tag mask, the helper folds any modifier whose single-bit
//     stat overlaps the mask into a running multiplicative product. Used
//     for healthDamage scaling, armor-penetration-chance scaling, blunt-chip scaling,
//     knockback magnitude scaling, buildup scaling — receivers seed and
//     accumulate per gameplay site.
//
// Both modes accept a running value/product so callers can chain sources
// (inherent → armor → status effects) without intermediate allocations.
public static class StatModifierUtil
{
	// Subset of EStat values that scale a hit's healthDamage. Receivers AND
	// this with hit.tags to determine which mask to compose against the
	// damage-scaling site. ArmorPenetration / Blunt / Knockback / Dizzy intentionally
	// don't participate in damage scaling — they have dedicated application
	// sites (armor-bypass chance, armor-chip mult, knockback magnitude,
	// buildup-meter feed) that scale their own thing instead.
	public const EStat DamageScaleTags =
		EStat.Damage
		| EStat.Fire
		| EStat.Magical
		| EStat.Poison
		| EStat.Electrical
		| EStat.Ranged
		| EStat.Melee;


	// True for the stats whose composition is "+= entry.value" with a
	// neutral identity of 0. Every other stat is "*= entry.value" with a
	// neutral identity of 1. The split is intrinsic to the stat — adding a
	// new additive stat means appending to this switch.
	public static bool IsAdditive(EStat stat)
	{
		switch (stat)
		{
			case EStat.Camouflage:
			case EStat.MaxStamina:
			case EStat.MaxHealth:
			case EStat.MaxArmor:
			case EStat.ColdResist:
			case EStat.HeatResist:
				return true;
			default:
				return false;
		}
	}

	// Neutral identity for `stat` — 0 for additive stats, 1 otherwise. Used
	// as the seed when a caller starts composing fresh; chained callers pass
	// the running value through Fold instead.
	public static float NeutralValue(EStat stat) => IsAdditive(stat) ? 0f : 1f;

	// Fold every entry whose `stat` equals `stat` into the running value
	// using the stat's intrinsic op. Caller seeds with NeutralValue(stat) or
	// chains across multiple sources.
	public static float Fold(EStat stat, Array<StatModifier> entries, float running)
	{
		if (entries == null || stat == EStat.None)
		{
			return running;
		}
		bool additive = IsAdditive(stat);
		// Count is a native call — hoist it, or the loop crosses the managed/
		// native boundary twice per element instead of once.
		int count = entries.Count;
		for (int i = 0; i < count; i++)
		{
			StatModifier m = entries[i];
			if (m == null || m.stat != stat)
			{
				continue;
			}
			if (additive)
			{
				running += m.value;
			}
			else
			{
				running *= m.value;
			}
		}
		return running;
	}

	// Copy an authored modifier list into a managed array for per-tick reads.
	// Callers cache the result on the owning *Data resource (see MobData
	// .ModifiersFlat) — never per consuming instance, or every mob sharing that
	// resource pays for its own copy.
	public static StatModifier[] Flatten(Array<StatModifier> entries)
	{
		int count = entries?.Count ?? 0;
		if (count == 0)
		{
			return System.Array.Empty<StatModifier>();
		}
		var flat = new StatModifier[count];
		for (int i = 0; i < count; i++)
		{
			flat[i] = entries[i];
		}
		return flat;
	}

	// Plain-array overloads of Fold / FoldMask, identical in semantics to the
	// Godot.Collections.Array versions. Indexing a Godot Array from C# marshals
	// a Variant per element; a caller that folds the SAME immutable modifier
	// list every tick should flatten it into a plain array once and use these
	// instead (see Mob's baked modifier array). Authored data still lives in
	// Godot.Collections.Array — this is strictly a hot-path read form.
	public static float Fold(EStat stat, StatModifier[] entries, float running)
	{
		if (entries == null || stat == EStat.None)
		{
			return running;
		}
		bool additive = IsAdditive(stat);
		for (int i = 0; i < entries.Length; i++)
		{
			StatModifier m = entries[i];
			if (m == null || m.stat != stat)
			{
				continue;
			}
			if (additive)
			{
				running += m.value;
			}
			else
			{
				running *= m.value;
			}
		}
		return running;
	}

	public static float FoldMask(EStat mask, StatModifier[] entries, float product)
	{
		if (entries == null || mask == EStat.None)
		{
			return product;
		}
		for (int i = 0; i < entries.Length; i++)
		{
			StatModifier m = entries[i];
			if (m == null)
			{
				continue;
			}
			if ((m.stat & mask) != 0)
			{
				product *= m.value;
			}
		}
		return product;
	}

	// Multiplicative fold across every entry whose single-bit stat overlaps
	// `mask`. Used by the hit-side resistance paths (damage / bypass / blunt
	// chip / knockback / buildup) where the mask is the hit's tag set and
	// matching modifiers compose as a product. Always multiplicative — the
	// additive stats (Camouflage, MaxStamina, ColdResist, HeatResist) don't
	// participate in hit-side masks by design (they're identified, not
	// tagged on a hit), so this fold can assume multiply semantics.
	public static float FoldMask(EStat mask, Array<StatModifier> entries, float product)
	{
		if (entries == null || mask == EStat.None)
		{
			return product;
		}
		int count = entries.Count;
		for (int i = 0; i < count; i++)
		{
			StatModifier m = entries[i];
			if (m == null)
			{
				continue;
			}
			if ((m.stat & mask) != 0)
			{
				product *= m.value;
			}
		}
		return product;
	}
}
