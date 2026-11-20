using System;
using System.Collections.Generic;

// A scripting-variable read or write parsed out of a sheet's condition /
// action cell. The cell normally NAMES an authored .tres; these two prefixes
// are the exception, because a per-NPC quest flag is not a reusable verb —
// its whole content is (variable, op, value), and making an author write a
// .tres plus a ScriptVariableData plus a registry line for that is the
// authoring cost the sheet exists to remove.
//
//   npcvar:<name>   the flag is this character's own. The id is namespaced
//                   with the character name (npcvar:customs on intro_watchman
//                   is `intro_watchman_customs`) and DECLARED by the sheet —
//                   the importer collects it into the generated registry.
//   var:<name>      a globally authored variable from
//                   resources/data/worlds/shared/script_variables/. The name
//                   must already be declared; a typo is an error here rather
//                   than a silent always-false gate at runtime.
//
// Condition forms: `npcvar:customs`, `=true`, `=false`, `!=true`, `=2`,
// `>=2`, `>2`, `<=2`, `<2`, `!=2`. A bare name reads as `=true`.
// Action forms: `=true`, `=false`, `=<int>` (Set) and `+=<int>` (Add).
class ScriptVarRef
{
	public string Id;
	// EScriptVarCompareOp / EScriptVarSetOp ordinal, per the parse that made it.
	public int Op;
	public int Operand;
	// True when this use reads or writes a bool, so the generated declaration
	// can pick an EScriptVarType.
	public bool IsBool;
	// Namespaced npcvar (the sheet declares it) vs a global authored var.
	public bool IsNpcVar;
}

static class ScriptVarCell
{
	// The grammar is the game's (ActionCommand, linked in); this only namespaces
	// an npcvar with the character it belongs to.
	public static bool IsVarToken(string token)
	{
		return ActionCommand.IsVarToken(token);
	}

	public static ScriptVarRef ParseCondition(string token, string character, SheetRow row, Report report)
	{
		return ToRef(ActionCommand.ParseVarTest(token, out string error), error, character, row, report);
	}

	public static ScriptVarRef ParseAction(string token, string character, SheetRow row, Report report)
	{
		return ToRef(ActionCommand.ParseVarWrite(token, out string error), error, character, row, report);
	}

	public static ScriptVarRef FromCommand(ActionCommand command, string character)
	{
		return new ScriptVarRef
		{
			Id = command.NpcVar ? character + "_" + command.Name : command.Name,
			IsNpcVar = command.NpcVar,
			Op = command.Op,
			Operand = command.Operand,
			IsBool = command.IsBool,
		};
	}

	static ScriptVarRef ToRef(ActionCommand command, string error, string character, SheetRow row, Report report)
	{
		if (command == null)
		{
			report.Error(row, error);
			return null;
		}
		return FromCommand(command, character);
	}
}

// Every npcvar the sheets named, accumulated across all worlds so the importer
// can write the one generated ScriptVariableRegistry that declares them. An
// npcvar is declared by being used; a `var:` is not, and is checked against the
// authored declarations instead.
class VarDeclarations
{
	class Declaration
	{
		public bool IsBool = true;
		public string Source;
	}

	readonly Dictionary<string, Declaration> _byId = new Dictionary<string, Declaration>(StringComparer.Ordinal);
	readonly List<string> _order = new List<string>();

	public void Declare(ScriptVarRef reference, string world, string character)
	{
		if (!_byId.TryGetValue(reference.Id, out Declaration declaration))
		{
			declaration = new Declaration { Source = $"{world}/{character}" };
			_byId[reference.Id] = declaration;
			_order.Add(reference.Id);
		}
		// One integer use anywhere makes the variable an Int.
		declaration.IsBool = declaration.IsBool && reference.IsBool;
	}

	public int Count => _order.Count;

	public IEnumerable<string> Ids => _order;

	public bool IsBool(string id)
	{
		return _byId[id].IsBool;
	}

	public string Source(string id)
	{
		return _byId[id].Source;
	}
}
