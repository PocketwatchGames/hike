using System;
using System.Collections.Generic;

// One thing an NPC teaches, parsed out of a sheet's action cell:
// `teach:<kind> <name> [percent%]`.
//
//   teach:language vyeshal                     the whole tongue
//   teach:language vyeshal 10%                 10% of it
//   teach:recipe health_potion                 how to craft an item
//   teach:meal stew_goblin_health              how to cook a cookpot meal
//   teach:region swamp
//   teach:item health_potion                   reveals its real name
//   teach:bestiary drake_mountain
//
// The kind is part of the cell because the teachables are genuinely different
// things drawn from different folders - `teach:swamp` would not say whether
// that is a region, and a basename shared by two kinds would be ambiguous. It
// also decides which extra words are legal: only a language takes an amount.
//
// A language lesson is identified by the cell it is written in (the generated
// concept is named after it - TresWriter.AppendLesson), so the same cell
// teaches once however often the player picks it, and two cells are two
// lessons.
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
	// Language only: how much of it, or -1 to take the export's default (the
	// whole tongue).
	public float Percent = -1f;
}

static class TeachCell
{
	// Resolves a parsed `teach:` (ActionCommand): which concept class teaches the
	// kind, and the resource it names.
	public static TeachRef Resolve(ActionCommand command, ResourceIndex index, string world, SheetRow row, Report report)
	{
		string name = command.Name;
		var reference = new TeachRef { Percent = command.Percent };
		string where;
		switch (command.TeachKind)
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
				report.Error(row, $"'{command.Token}' teaches a '{command.TeachKind}', which this importer has no concept for");
				return null;
		}
		if (reference.Target == null)
		{
			report.Error(row, $"unknown {command.TeachKind} '{name}' - {where}");
			return null;
		}
		return reference;
	}
}
