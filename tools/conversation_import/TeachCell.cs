using System;
using System.Collections.Generic;

// One thing an NPC teaches, parsed out of a sheet's action cell:
// `teach:<kind> <name> [components]`.
//
//   teach:language vyeshal                     the whole tongue
//   teach:language vyeshal grammar,vocabulary1  just those pieces
//   teach:recipe recipe_stew_goblin_health
//   teach:spell birds_eye
//   teach:region swamp
//   teach:item health_potion                   reveals its real name
//   teach:bestiary drake_mountain
//
// The kind is part of the cell because the teachables are genuinely different
// things drawn from different folders - `teach:swamp` would not say whether
// that is a region, and a basename shared by two kinds would be ambiguous. It
// also decides which extra words are legal: only a language takes components.
//
// The importer emits the TeachAction and its TeachableConcept itself, so a
// one-concept lesson needs no authored .tres - that was the whole content of
// the old teach_vyeshal.tres. An authored .tres is still the answer when the
// lesson is a named concept reused across NPCs, when it grants several things
// at once, when it wants TeachAction.learnEffect, or when it teaches one of the
// two concepts whose payload is authored TEXT rather than a resource reference
// (ScriptFlagTeachable.conceptName, TreasureMapTeachable.treasureName).
class TeachRef
{
	// The TeachableConcept subclass to instantiate, and the [Export] on it that
	// names what is taught.
	public string Teachable;
	public string Property;
	public ResRef Target;
	// [ext_resource] id prefix for Target, so the generated file reads
	// Lang_vyeshal rather than Teach_vyeshal.
	public string Prefix;
	// Language only: the ELanguageComponents bitset, or -1 to leave the property
	// off the sub-resource entirely and take the export's default (All).
	public int Components = -1;
}

static class TeachCell
{
	const string Prefix = "teach:";

	// ELanguageComponents bits. Restated rather than read off the enum - the
	// importer is a plain console app with no Godot assembly to reflect over -
	// so a bit APPENDED to that enum has to be appended here too.
	static readonly (string Name, int Bit)[] Components =
	{
		("grammar", 1 << 0),
		("numbers", 1 << 1),
		("vocabulary1", 1 << 2),
		("vocabulary2", 1 << 3),
		("vocabulary3", 1 << 4),
	};

	// True when the cell token is the inline teach form rather than the name of
	// an authored .tres. The separator is the colon, so an authored action may
	// still be named teach_something.
	public static bool IsTeachToken(string token)
	{
		return token.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
	}

	public static TeachRef Parse(string token, ResourceIndex index, string world, SheetRow row, Report report)
	{
		string body = token.Substring(Prefix.Length).Trim();
		string[] parts = body.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0)
		{
			report.Error(row, $"'{token}' names nothing - say 'teach:{KindList()} <name>'");
			return null;
		}

		string kind = parts[0].ToLowerInvariant();
		if (parts.Length < 2)
		{
			report.Error(row, $"'{token}' names a kind but nothing to teach - say 'teach:{kind} <name>'");
			return null;
		}
		string name = parts[1];

		var reference = new TeachRef();
		string where;
		switch (kind)
		{
			case "language":
				reference.Teachable = "LanguageTeachable";
				reference.Property = "language";
				reference.Prefix = "Lang";
				reference.Target = index.Language(world, name);
				where = "no LanguageData under resources/ declares that id";
				break;
			case "recipe":
				reference.Teachable = "RecipeTeachable";
				reference.Property = "recipe";
				reference.Prefix = "Recipe";
				reference.Target = index.Recipe(world, name);
				where = "no RecipeData .tres has that basename";
				break;
			case "spell":
				reference.Teachable = "SpellTeachable";
				reference.Property = "spell";
				reference.Prefix = "Spell";
				reference.Target = index.Spell(world, name);
				where = "no SpellData .tres has that basename";
				break;
			case "region":
				reference.Teachable = "RegionTeachable";
				reference.Property = "region";
				reference.Prefix = "Region";
				reference.Target = index.Region(world, name);
				where = "no RegionData .tres has that basename";
				break;
			case "item":
				reference.Teachable = "ItemTeachable";
				reference.Property = "item";
				reference.Prefix = "Item";
				reference.Target = index.Item(world, name);
				where = $"no .tres by that basename under resources/data/items/ or worlds/{world}/items/ (the same names `give ?` lists in the console)";
				break;
			case "bestiary":
				reference.Teachable = "MobTeachable";
				reference.Property = "species";
				reference.Prefix = "Species";
				reference.Target = index.Species(world, name);
				where = "no SpeciesData .tres has that basename";
				break;
			default:
				report.Error(row, $"'{token}' teaches a '{kind}', which is not a kind of thing - say one of {KindList()}");
				return null;
		}

		if (reference.Target == null)
		{
			report.Error(row, $"unknown {kind} '{name}' - {where}");
			return null;
		}

		if (parts.Length > 2 && kind != "language")
		{
			report.Error(row, $"'{token}' has more than a kind and a name - only a language takes components, and ';'-separates several lessons");
			return null;
		}
		if (parts.Length > 3)
		{
			report.Error(row, $"'{token}' has more than a kind, a name and a component list - ','-separate the components");
			return null;
		}
		if (parts.Length == 3 && !ParseComponents(parts[2], token, row, report, out reference.Components))
		{
			return null;
		}
		return reference;
	}

	// A ','-separated subset of the language pieces. Omitted entirely means the
	// whole tongue, which is what an author writing one lesson usually means.
	static bool ParseComponents(string list, string token, SheetRow row, Report report, out int bits)
	{
		bits = 0;
		foreach (string piece in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
		{
			string want = piece.Trim().ToLowerInvariant();
			int bit = 0;
			foreach ((string Name, int Bit) component in Components)
			{
				if (component.Name == want)
				{
					bit = component.Bit;
					break;
				}
			}
			if (bit == 0)
			{
				report.Error(row, $"'{token}' teaches a '{want}', which is not a piece of a language - say one or more of {ComponentList()}");
				return false;
			}
			bits |= bit;
		}
		if (bits == 0)
		{
			report.Error(row, $"'{token}' names no components - leave the list off to teach the whole tongue");
			return false;
		}
		return true;
	}

	static string KindList()
	{
		return "language, recipe, spell, region, item, bestiary";
	}

	static string ComponentList()
	{
		var names = new List<string>();
		foreach ((string Name, int Bit) component in Components)
		{
			names.Add(component.Name);
		}
		return string.Join(", ", names);
	}
}
