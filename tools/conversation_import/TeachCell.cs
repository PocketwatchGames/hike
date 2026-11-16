using System;
using System.Collections.Generic;

// One thing an NPC teaches, parsed out of a sheet's action cell:
// `teach:<kind> <name> [lesson percent%]`.
//
//   teach:language vyeshal                     the whole tongue
//   teach:language vyeshal 3 10%               lesson 3, worth 10% of it
//   teach:recipe health_potion                 how to craft an item
//   teach:meal stew_goblin_health              how to cook a cookpot meal
//   teach:region swamp
//   teach:item health_potion                   reveals its real name
//   teach:bestiary drake_mountain
//
// The kind is part of the cell because the teachables are genuinely different
// things drawn from different folders - `teach:swamp` would not say whether
// that is a region, and a basename shared by two kinds would be ambiguous. It
// also decides which extra words are legal: only a language takes a lesson.
//
// The importer emits the TeachAction and its TeachableConcept itself, so a
// one-concept lesson needs no authored .tres - that was the whole content of
// the old teach_vyeshal.tres. An authored .tres is still the answer when the
// lesson is a named concept reused across NPCs, when it grants several things
// at once, or when it teaches one of the
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
	// Language only: the lesson number and its percent, or -1 to leave both
	// off the sub-resource and take the export's default (the whole tongue).
	public int Lesson = -1;
	public float Percent = -1f;
}

static class TeachCell
{
	const string Prefix = "teach:";

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
				reference.Target = index.Item(world, name);
				where = $"no item .tres by that basename under resources/data/items/ or worlds/{world}/items/";
				break;
			case "meal":
				reference.Teachable = "MealTeachable";
				reference.Property = "meal";
				reference.Prefix = "Meal";
				reference.Target = index.Meal(world, name);
				where = "no MealData .tres has that basename";
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
				where = $"no .tres by that basename under resources/data/items/ or worlds/{world}/items/ (the same names `spawn_loot ?` lists in the console)";
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
			report.Error(row, $"'{token}' has more than a kind and a name - only a language takes a lesson, and ';'-separates several things taught");
			return null;
		}
		if (parts.Length == 3 || parts.Length > 4)
		{
			report.Error(row, $"'{token}' - a partial lesson is 'teach:language {name} <lesson> <percent>%', e.g. 'teach:language {name} 3 10%'; leave both off to teach the whole tongue");
			return null;
		}
		if (parts.Length == 4 && !ParseLesson(parts[2], parts[3], token, row, report, out reference.Lesson, out reference.Percent))
		{
			return null;
		}
		return reference;
	}

	// A lesson number (0 or more) and how much of the language it teaches.
	// Two sources with one number are one lesson, so each partial source wants
	// its own; the number is the only thing that stops one teaching twice.
	static bool ParseLesson(string lessonText, string percentText, string token, SheetRow row, Report report, out int lesson, out float percent)
	{
		percent = -1f;
		if (!int.TryParse(lessonText, out lesson) || lesson < 0)
		{
			report.Error(row, $"'{token}' names lesson '{lessonText}' - a lesson is a whole number from 0");
			return false;
		}
		string amount = percentText.EndsWith("%") ? percentText[..^1] : percentText;
		if (!float.TryParse(amount, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out percent)
			|| percent <= 0f || percent > 100f)
		{
			report.Error(row, $"'{token}' teaches '{percentText}' - say how much of the language, from 1% to 100%");
			return false;
		}
		return true;
	}

	static string KindList()
	{
		return "language, recipe, meal, region, item, bestiary";
	}
}
