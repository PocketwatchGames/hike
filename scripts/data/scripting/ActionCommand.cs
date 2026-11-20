using System;
using System.Collections.Generic;

// The typed-command grammar an action cell is written in — a conversation
// sheet's `on start` / `on complete` and a placement hook's `commands` alike:
//
//   var:<name>=true | =false | =<int> | +=<int>     write a declared variable
//   npcvar:<name>=…                                 the speaker's own variable
//   spawn_loot:<item> [count]                       the speaker drops an item
//   teach:<kind> <name> [percent%]                  a lesson
//   start_quest:<name> / complete_quest:<name>
//   behavior:<node>                                 the speaker's brain node
//   reappear:<place>                                the speaker moves there
//   <name>                                          an authored action .tres
//
// `;` separates commands. This file only PARSES — what a name resolves to is the
// consumer's business, because the two consumers resolve against different
// things: tools/conversation_import scans the filesystem (it compiles this file
// as a link, so it must stay plain C#: no Godot types, no game classes), and a
// placement hook resolves against the running project (EntityHookCommands).
// One grammar is what keeps a door and a dialogue cell spelled the same.
public enum EActionCommand
{
    SetVar,
    SpawnLoot,
    Teach,
    StartQuest,
    CompleteQuest,
    Behavior,
    Reappear,
    // An authored action file, by basename.
    Named,
}

public sealed class ActionCommand
{
    public const string NpcVarPrefix = "npcvar:";
    public const string VarPrefix = "var:";
    public const string SpawnLootPrefix = "spawn_loot:";
    public const string TeachPrefix = "teach:";
    public const string StartQuestPrefix = "start_quest:";
    public const string CompleteQuestPrefix = "complete_quest:";
    public const string BehaviorPrefix = "behavior:";
    public const string ReappearPrefix = "reappear:";
    public const string UnderstandsPrefix = "understands";

    // EScriptVarSetOp ordinals.
    public const int SetOp = 0;
    public const int AddOp = 1;

    public static readonly string[] TeachKinds = { "language", "recipe", "meal", "region", "item", "bestiary" };

    public EActionCommand Kind;
    // The command as written, for error messages.
    public string Token;
    // The variable (un-namespaced for an npcvar), item, quest, teach target,
    // brain node, place or action file.
    public string Name;
    // SetVar.
    public bool NpcVar;
    public int Op;
    public int Operand;
    public bool IsBool;
    // SpawnLoot.
    public int Count = 1;
    // Teach: one of TeachKinds, and the share of a language (-1 = whole).
    public string TeachKind;
    public float Percent = -1f;

    // Does this command act on whoever is speaking? Such a command means nothing
    // on a source with no speaker (a door). A Named command may too — that is
    // the resolved action's to say (ConversationAction.NeedsSpeaker).
    public bool NeedsSpeaker => NpcVar || Kind is EActionCommand.SpawnLoot
        or EActionCommand.Behavior or EActionCommand.Reappear;

    // The non-empty `;`-separated tokens of a cell, trimmed.
    public static List<string> SplitCell(string cell)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(cell))
        {
            return tokens;
        }
        foreach (string part in cell.Split(';'))
        {
            string token = part.Trim();
            if (token.Length > 0)
            {
                tokens.Add(token);
            }
        }
        return tokens;
    }

    // Is this token one of the condition forms rather than an action?
    public static bool IsConditionToken(string token)
    {
        return token.StartsWith(UnderstandsPrefix, StringComparison.OrdinalIgnoreCase);
    }

    // Is this token one of the inline action forms (anything but a file name)?
    public static bool IsInlineActionToken(string token)
    {
        return Has(token, SpawnLootPrefix) || Has(token, TeachPrefix) || Has(token, StartQuestPrefix)
            || Has(token, CompleteQuestPrefix) || Has(token, BehaviorPrefix) || Has(token, ReappearPrefix);
    }

    public static bool IsVarToken(string token)
    {
        return Has(token, NpcVarPrefix) || Has(token, VarPrefix);
    }

    // One action token, or null with `error` saying why.
    public static ActionCommand Parse(string token, out string error)
    {
        error = null;
        token = token?.Trim() ?? "";
        if (token.Length == 0)
        {
            error = "an empty command";
            return null;
        }
        if (IsConditionToken(token))
        {
            error = $"'{token}' tests what the player understands - that is a condition, not an action";
            return null;
        }
        if (IsVarToken(token))
        {
            return ParseVarWrite(token, out error);
        }
        if (Has(token, SpawnLootPrefix))
        {
            return ParseSpawnLoot(token, out error);
        }
        if (Has(token, TeachPrefix))
        {
            return ParseTeach(token, out error);
        }
        if (Has(token, StartQuestPrefix) || Has(token, CompleteQuestPrefix))
        {
            bool start = Has(token, StartQuestPrefix);
            string prefix = start ? StartQuestPrefix : CompleteQuestPrefix;
            string quest = OneWord(token, prefix);
            if (quest == null)
            {
                error = $"'{token}' must name exactly one quest - say '{prefix}find_festering_man'";
                return null;
            }
            return new ActionCommand
            {
                Kind = start ? EActionCommand.StartQuest : EActionCommand.CompleteQuest,
                Token = token,
                Name = quest,
            };
        }
        if (Has(token, BehaviorPrefix))
        {
            string node = OneWord(token, BehaviorPrefix);
            if (node == null)
            {
                error = $"'{token}' must name one behavior node of the speaker's brain - say 'behavior:Escape'";
                return null;
            }
            return new ActionCommand { Kind = EActionCommand.Behavior, Token = token, Name = node };
        }
        if (Has(token, ReappearPrefix))
        {
            string place = OneWord(token, ReappearPrefix);
            if (place == null)
            {
                error = $"'{token}' must name one place - a named painter placement, e.g. 'reappear:bear_body'";
                return null;
            }
            return new ActionCommand { Kind = EActionCommand.Reappear, Token = token, Name = place };
        }
        if (token.IndexOfAny(new[] { ' ', '\t', ':', '=' }) >= 0)
        {
            error = $"'{token}' is not a command - an action file is named by its basename alone, and the inline forms are var:, npcvar:, spawn_loot:, teach:, start_quest:, complete_quest:, behavior:, reappear:";
            return null;
        }
        return new ActionCommand { Kind = EActionCommand.Named, Token = token, Name = token };
    }

    // ---- Variables ------------------------------------------------------------

    // EScriptVarCompareOp ordinals.
    public const int IsTrue = 0;
    public const int IsFalse = 1;
    public const int Equal = 2;
    public const int NotEqual = 3;
    public const int GreaterThan = 4;
    public const int GreaterOrEqual = 5;
    public const int LessThan = 6;
    public const int LessOrEqual = 7;

    // Longest first so "+=" is not read as "=" and ">=" is not read as ">".
    private static readonly string[] Operators = { "+=", "!=", ">=", "<=", "==", "=", ">", "<" };

    // A `var:` / `npcvar:` WRITE: `=true`, `=false`, `=<int>` (Set) or `+=<int>` (Add).
    public static ActionCommand ParseVarWrite(string token, out string error)
    {
        if (!SplitVar(token, out ActionCommand command, out string op, out string value, out error))
        {
            return null;
        }
        if (op.Length == 0)
        {
            error = $"'{token}' names a variable but not what to write - say '{token}=true'";
            return null;
        }
        if (op == "+=")
        {
            if (!int.TryParse(value, out int delta))
            {
                error = $"'{token}' adds '{value}', which is not a whole number";
                return null;
            }
            command.Op = AddOp;
            command.Operand = delta;
            return command;
        }
        if (op != "=" && op != "==")
        {
            error = $"'{token}' compares - an action writes, so it takes = or += only";
            return null;
        }
        command.Op = SetOp;
        if (TryParseBool(value, out bool flag))
        {
            command.IsBool = true;
            command.Operand = flag ? 1 : 0;
            return command;
        }
        if (!int.TryParse(value, out int operand))
        {
            error = $"'{token}' writes '{value}', which is neither true/false nor a whole number";
            return null;
        }
        command.Operand = operand;
        return command;
    }

    // A `var:` / `npcvar:` READ, for a condition cell: `=true`, `!=true`, `=2`,
    // `>=2`, `<2`…, or a bare name (`=true`). `Op` is an EScriptVarCompareOp
    // ordinal on the returned command.
    public static ActionCommand ParseVarTest(string token, out string error)
    {
        if (!SplitVar(token, out ActionCommand command, out string op, out string value, out error))
        {
            return null;
        }
        if (op == "+=")
        {
            error = $"'{token}' adds to a variable - a condition reads one, it does not write it";
            return null;
        }
        if (op.Length == 0)
        {
            command.Op = IsTrue;
            command.IsBool = true;
            return command;
        }
        if (TryParseBool(value, out bool flag))
        {
            command.IsBool = true;
            if (op == "=" || op == "==")
            {
                command.Op = flag ? IsTrue : IsFalse;
                return command;
            }
            if (op == "!=")
            {
                command.Op = flag ? IsFalse : IsTrue;
                return command;
            }
            error = $"'{token}' orders a true/false variable - only = and != mean anything on a bool";
            return null;
        }
        if (!int.TryParse(value, out int operand))
        {
            error = $"'{token}' compares against '{value}', which is neither true/false nor a whole number";
            return null;
        }
        command.Operand = operand;
        command.Op = op switch
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
        if (command.Op < 0)
        {
            error = $"'{token}' uses an operator '{op}' no condition understands";
            return null;
        }
        return command;
    }

    // Splits `<prefix><name><op><value>`. `op` is "" for a bare name.
    private static bool SplitVar(string token, out ActionCommand command, out string op, out string value, out string error)
    {
        command = null;
        op = "";
        value = "";
        error = null;

        bool npcVar = Has(token, NpcVarPrefix);
        string body = token.Substring(npcVar ? NpcVarPrefix.Length : VarPrefix.Length).Trim();

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
            error = $"'{token}' names no variable";
            return false;
        }
        if (op.Length > 0 && value.Length == 0)
        {
            error = $"'{token}' ends on '{op}' with nothing after it";
            return false;
        }
        command = new ActionCommand { Kind = EActionCommand.SetVar, Token = token, Name = name, NpcVar = npcVar };
        return true;
    }

    private static bool TryParseBool(string value, out bool flag)
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

    // ---- The rest -------------------------------------------------------------

    private static ActionCommand ParseSpawnLoot(string token, out string error)
    {
        error = null;
        string body = token.Substring(SpawnLootPrefix.Length).Trim();
        if (body.Length == 0)
        {
            error = $"'{token}' names no item - say 'spawn_loot:lantern'";
            return null;
        }
        string[] parts = body.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 2)
        {
            error = $"'{token}' has more than an item and a count - one gift per token, ';'-separate several";
            return null;
        }
        int count = 1;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out count) || count < 1))
        {
            error = $"'{token}' has a count of '{parts[1]}', which is not a whole number of 1 or more";
            return null;
        }
        return new ActionCommand { Kind = EActionCommand.SpawnLoot, Token = token, Name = parts[0], Count = count };
    }

    private static ActionCommand ParseTeach(string token, out string error)
    {
        error = null;
        string kinds = string.Join(", ", TeachKinds);
        string body = token.Substring(TeachPrefix.Length).Trim();
        string[] parts = body.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = $"'{token}' names nothing - say 'teach:{kinds} <name>'";
            return null;
        }
        string kind = parts[0].ToLowerInvariant();
        if (Array.IndexOf(TeachKinds, kind) < 0)
        {
            error = $"'{token}' teaches a '{kind}', which is not a kind of thing - say one of {kinds}";
            return null;
        }
        if (parts.Length < 2)
        {
            error = $"'{token}' names a kind but nothing to teach - say 'teach:{kind} <name>'";
            return null;
        }
        string name = parts[1];
        if (parts.Length > 2 && kind != "language")
        {
            error = $"'{token}' has more than a kind and a name - only a language takes an amount, and ';'-separates several things taught";
            return null;
        }
        if (parts.Length > 3)
        {
            error = $"'{token}' - a partial lesson is 'teach:language {name} <percent>%', e.g. 'teach:language {name} 10%'; leave it off to teach the whole tongue";
            return null;
        }
        var command = new ActionCommand { Kind = EActionCommand.Teach, Token = token, TeachKind = kind, Name = name };
        if (parts.Length == 3)
        {
            string percentText = parts[2];
            string amount = percentText.EndsWith("%") ? percentText[..^1] : percentText;
            if (!float.TryParse(amount, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float percent)
                || percent <= 0f || percent > 100f)
            {
                error = $"'{token}' teaches '{percentText}' - say how much of the language, from 1% to 100%";
                return null;
            }
            command.Percent = percent;
        }
        return command;
    }

    private static bool Has(string token, string prefix)
    {
        return token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // The single word after `prefix`, or null when there is none or more than one.
    private static string OneWord(string token, string prefix)
    {
        string word = token.Substring(prefix.Length).Trim();
        return word.Length == 0 || word.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? null : word;
    }
}
