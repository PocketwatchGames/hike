using Godot.Collections;

// A modifier list split by kind into plain managed arrays — the hot-path read
// form of an authored Array<Modifier>. Indexing a Godot Array from C#
// marshals a Variant per element, and ComposeStat / ComposeTagMul run several
// times per tick per actor, so each owning *Data resource builds one of these
// lazily and every consumer reads it (see MobData.ModifiersFlat). Never cache
// one per consuming instance — 139 mobs sharing five MobData assets should
// share five sets.
public sealed class ModifierSet
{
	public static readonly ModifierSet Empty = new(System.Array.Empty<StatModifier>(), System.Array.Empty<TagModifier>());

	public readonly StatModifier[] stats;
	public readonly TagModifier[] tags;

	private ModifierSet(StatModifier[] stats, TagModifier[] tags)
	{
		this.stats = stats;
		this.tags = tags;
	}

	public static ModifierSet From(Array<Modifier> entries)
	{
		int count = entries?.Count ?? 0;
		if (count == 0)
		{
			return Empty;
		}
		var stats = new System.Collections.Generic.List<StatModifier>(count);
		var tags = new System.Collections.Generic.List<TagModifier>(count);
		for (int i = 0; i < count; i++)
		{
			switch (entries[i])
			{
				case StatModifier s:
					stats.Add(s);
					break;
				case TagModifier t:
					tags.Add(t);
					break;
			}
		}
		return new ModifierSet(stats.ToArray(), tags.ToArray());
	}
}

// Composition helpers over a ModifierSet. Two folds, one per modifier kind:
//
//   * Fold (StatModifier): one stat's final value. Seed with NeutralValue(stat)
//     and fold each source; the op is intrinsic to the stat.
//
//   * FoldTags (TagModifier): the product of every entry whose tag overlaps the
//     mask. Always multiplicative. Hit sites pass the hit's tags; the buildup
//     site passes the effect's status family.
//
// Both take a running value so callers chain sources (inherent → armor →
// status effects) without intermediate allocations.
public static class StatModifierUtil
{
	// True for the stats whose composition is "+= entry.value" with a
	// neutral identity of 0. Every other stat is "*= entry.value" with a
	// neutral identity of 1. Adding a new additive stat means appending here.
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

	public static float NeutralValue(EStat stat) => IsAdditive(stat) ? 0f : 1f;

	public static float Fold(EStat stat, ModifierSet set, float running)
	{
		if (set == null || stat == EStat.None)
		{
			return running;
		}
		StatModifier[] entries = set.stats;
		bool additive = IsAdditive(stat);
		for (int i = 0; i < entries.Length; i++)
		{
			StatModifier m = entries[i];
			if (m.stat != stat)
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

	public static float FoldTags(EHitTag mask, ModifierSet set, float product)
	{
		if (set == null || mask == EHitTag.None)
		{
			return product;
		}
		TagModifier[] entries = set.tags;
		for (int i = 0; i < entries.Length; i++)
		{
			TagModifier m = entries[i];
			if ((m.tag & mask) != 0)
			{
				product *= m.value;
			}
		}
		return product;
	}
}
