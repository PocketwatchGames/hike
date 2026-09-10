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
	const string NpcPrefix = "npcvar:";
	const string GlobalPrefix = "var:";

	// EScriptVarCompareOp ordinals.
	const int IsTrue = 0;
	const int IsFalse = 1;
	const int Equal = 2;
	const int NotEqual = 3;
	const int GreaterThan = 4;
	const int GreaterOrEqual = 5;
	const int LessThan = 6;
	const int LessOrEqual = 7;

	// EScriptVarSetOp ordinals.
	const int Set = 0;
	const int Add = 1;

	// Longest first so "+=" is not read as "=" and ">=" is not read as ">".
	static readonly string[] Operators = { "+=", "!=", ">=", "<=", "==", "=", ">", "<" };

	// True when the cell token is one of the inline forms rather than the name
	// of an authored .tres.
	public static bool IsVarToken(string token)
	{
		return token.StartsWith(NpcPrefix, StringComparison.OrdinalIgnoreCase)
			|| token.StartsWith(GlobalPrefix, StringComparison.OrdinalIgnoreCase);
	}

	public static ScriptVarRef ParseCondition(string token, string character, SheetRow row, Report report)
	{
		if (!Split(token, character, row, report, out ScriptVarRef reference, out string op, out string value))
		{
			return null;
		}
		if (op == "+=")
		{
			report.Error(row, $"'{token}' adds to a variable - a condition reads one, it does not write it");
			return null;
		}
		if (op.Length == 0)
		{
			// A bare name is the common case: "is this flag set".
			reference.Op = IsTrue;
			reference.IsBool = true;
			return reference;
		}
		if (TryParseBool(value, out bool flag))
		{
			reference.IsBool = true;
			if (op == "=" || op == "==")
			{
				reference.Op = flag ? IsTrue : IsFalse;
				return reference;
			}
			if (op == "!=")
			{
				reference.Op = flag ? IsFalse : IsTrue;
				return reference;
			}
			report.Error(row, $"'{token}' orders a true/false variable - only = and != mean anything on a bool");
			return null;
		}
		if (!int.TryParse(value, out int operand))
		{
			report.Error(row, $"'{token}' compares against '{value}', which is neither true/false nor a whole number");
			return null;
		}
		reference.Operand = operand;
		reference.Op = op switch
		{
			"=" => Equal,
			"==" => Equal,
			"!=" => NotEqual,
			">" => GreaterThan,
			">=" => GreaterOrEqual,
			"<" => LessThan,
			"<=" => LessOrEqual,
			_ => -1,
		};
		if (reference.Op < 0)
		{
			report.Error(row, $"'{token}' uses an operator '{op}' no condition understands");
			return null;
		}
		return reference;
	}

	public static ScriptVarRef ParseAction(string token, string character, SheetRow row, Report report)
	{
		if (!Split(token, character, row, report, out ScriptVarRef reference, out string op, out string value))
		{
			return null;
		}
		if (op.Length == 0)
		{
			report.Error(row, $"'{token}' names a variable but not what to write - say '{token}=true'");
			return null;
		}
		if (op == "+=")
		{
			if (!int.TryParse(value, out int delta))
			{
				report.Error(row, $"'{token}' adds '{value}', which is not a whole number");
				return null;
			}
			reference.Op = Add;
			reference.Operand = delta;
			return reference;
		}
		if (op != "=" && op != "==")
		{
			report.Error(row, $"'{token}' compares - an action writes, so it takes = or += only");
			return null;
		}
		reference.Op = Set;
		if (TryParseBool(value, out bool flag))
		{
			reference.IsBool = true;
			reference.Operand = flag ? 1 : 0;
			return reference;
		}
		if (!int.TryParse(value, out int operand))
		{
			report.Error(row, $"'{token}' writes '{value}', which is neither true/false nor a whole number");
			return null;
		}
		reference.Operand = operand;
		return reference;
	}

	// Splits `<prefix><name><op><value>` and resolves the id. `op` is "" when
	// the token is a bare name.
	static bool Split(string token, string character, SheetRow row, Report report, out ScriptVarRef reference, out string op, out string value)
	{
		reference = null;
		op = "";
		value = "";

		bool npcVar = token.StartsWith(NpcPrefix, StringComparison.OrdinalIgnoreCase);
		string body = token.Substring(npcVar ? NpcPrefix.Length : GlobalPrefix.Length).Trim();

		// Earliest operator wins; on a tie the longest does, so "customs+=1"
		// splits on "+=" rather than on the "=" one character later.
		int split = body.Length;
		foreach (string candidate in Operators)
		{
			int at = body.IndexOf(candidate, StringComparison.Ordinal);
			if (at < 0)
			{
				continue;
			}
			if (at < split || (at == split && candidate.Length > op.Length))
			{
				split = at;
				op = candidate;
			}
		}
		string name = body.Substring(0, split).Trim();
		if (op.Length > 0)
		{
			value = body.Substring(split + op.Length).Trim();
		}
		if (name.Length == 0)
		{
			report.Error(row, $"'{token}' names no variable");
			return false;
		}
		if (op.Length > 0 && value.Length == 0)
		{
			report.Error(row, $"'{token}' ends on '{op}' with nothing after it");
			return false;
		}
		reference = new ScriptVarRef
		{
			Id = npcVar ? character + "_" + name : name,
			IsNpcVar = npcVar,
		};
		return true;
	}

	static bool TryParseBool(string value, out bool flag)
	{
		if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
		{
			flag = true;
			return true;
		}
		if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
		{
			flag = false;
			return true;
		}
		flag = false;
		return false;
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
