using System.Collections.Generic;

// Builds an item's full display name by wrapping its base noun with the affixes
// of the permanent weapon mods composed onto it (see ItemDescriptor) — e.g. a
// bomb carrying the Fragile mod and a Lightning mod reads "Fragile bomb of
// Lightning". Read-side only: a pure function of the item's live status effects,
// mutating nothing. SimState.GetItemDisplayName routes identified items
// through here; unidentified items skip it so affixes don't leak the reveal.
//
// Grammar across languages lives in the loc templates, NOT in this code: each
// affix is placed via weapon_name_prefix (%0 = affix, %1 = name-so-far) or
// weapon_name_suffix (%0 = name-so-far, %1 = affix). A language with post-nominal
// adjectives swaps the placeholder order in its own .tsv. This code only decides
// which template each affix uses (from its EAffixPosition) and how multiple
// affixes nest — it never hardcodes word order or spacing.
public static class WeaponNameGenerator
{
	public static string Compose(string baseName, ItemState item)
	{
		if (item == null)
		{
			return baseName;
		}
		IReadOnlyList<StatusEffectState> effects = item.statusEffects.StatusEffects;
		if (effects.Count == 0)
		{
			return baseName;
		}

		// Split the contributing affixes by slot, preserving authoring order
		// within each. Lists stay null until a mod actually contributes, so a
		// plain item allocates nothing.
		List<string> prefixes = null;
		List<string> suffixes = null;
		for (int i = 0; i < effects.Count; i++)
		{
			StatusEffectData data = effects[i]?.data;
			WeaponModData mod = data?.weaponMod;
			if (mod == null)
			{
				continue;
			}
			// Empty affix falls back to the effect's displayName so an ordinary
			// adjective mod names the weapon with no extra authoring.
			string affix = !string.IsNullOrEmpty(mod.affix)
				? mod.affix.ToString()
				: data.displayName.ToString();
			if (string.IsNullOrEmpty(affix))
			{
				continue;
			}
			if (mod.affixPosition == EAffixPosition.Suffix)
			{
				(suffixes ??= new List<string>()).Add(affix);
			}
			else
			{
				(prefixes ??= new List<string>()).Add(affix);
			}
		}

		string name = baseName;
		// Suffixes attach first so they sit nearest the noun; prefixes then wrap
		// outward. Prefixes apply in reverse authoring order so the first-authored
		// one reads leftmost ("thorny fragile sword" when thorny is authored first).
		if (suffixes != null)
		{
			for (int i = 0; i < suffixes.Count; i++)
			{
				name = ApplyAffix(Loc.Keys.weapon_name_suffix, name, suffixes[i], $"{name} {suffixes[i]}");
			}
		}
		if (prefixes != null)
		{
			for (int i = prefixes.Count - 1; i >= 0; i--)
			{
				name = ApplyAffix(Loc.Keys.weapon_name_prefix, prefixes[i], name, $"{prefixes[i]} {name}");
			}
		}
		return name;
	}

	// Substitute %0/%1 in the slot's loc template. Falls back to a plain
	// space-join when the template is missing so a forgotten translation never
	// surfaces a "MISSING:" marker inside an item name.
	private static string ApplyAffix(Loc.Keys key, string arg0, string arg1, string fallback)
	{
		string template = Loc.Get(key);
		if (string.IsNullOrEmpty(template) || template.StartsWith("MISSING:"))
		{
			return fallback;
		}
		return template.Replace("%0", arg0).Replace("%1", arg1);
	}
}
