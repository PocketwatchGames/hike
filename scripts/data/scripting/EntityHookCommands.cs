using System.Collections.Generic;
using Godot;

// One resolved hook command: what a placement's typed text (ActionCommand)
// came to once its names were looked up. This is what the sim state carries and
// the .hike stores — paths that ship, never names to scan for — so a loaded
// world does no directory search and cannot fail a lookup the bake passed.
public sealed class HookCommand
{
    public EActionCommand Kind;
    // SetVar: the variable id. StartQuest / CompleteQuest / Named: a res:// path.
    public string Ref;
    public int Op;
    public int Operand;

    // The live action this runs as — the same class a conversation cell
    // compiles to, so a door and a dialogue line behave identically. Null (with
    // an error) if the stored path no longer loads.
    public ConversationAction Materialize()
    {
        switch (Kind)
        {
            case EActionCommand.SetVar:
                return new SetScriptVarAction
                {
                    variable = Ref,
                    op = (EScriptVarSetOp)Op,
                    operand = Operand,
                };
            case EActionCommand.StartQuest:
                return new StartQuestAction { quest = LoadOrError<QuestData>() };
            case EActionCommand.CompleteQuest:
                return new CompleteQuestAction { quest = LoadOrError<QuestData>() };
            case EActionCommand.Named:
                return LoadOrError<ConversationAction>();
            default:
                GD.PushError($"HookCommand: {Kind} cannot run on a placement hook.");
                return null;
        }
    }

    private T LoadOrError<T>() where T : Resource
    {
        if (ResourceLoader.Load(Ref) is T loaded)
        {
            return loaded;
        }
        GD.PushError($"HookCommand: '{Ref}' did not load as a {typeof(T).Name}.");
        return null;
    }
}

// One hook as resolved at spawn: immutable, so every entity a placement files
// shares it, and each keeps its own fired flag (EntityHooks).
public sealed class EntityHookSpec
{
    public EEntityEvent On;
    public bool Once;
    public HookCommand[] Commands;
}

// Resolves a hook's typed commands against the project: the grammar is
// ActionCommand's (shared with the conversation importer), the lookups mirror
// the importer's folders and WorldScope's shadowing.
public static class EntityHookCommands
{
    private const string SharedQuests = "res://resources/data/worlds/shared/quests/";
    private const string ScriptActions = "res://resources/data/dialogue_scripts/actions/";
    private const string SharedActions = "res://resources/data/worlds/shared/conversation/actions/";

    // The first problem with `text`, or null when every command resolves.
    public static string FirstError(string text, string world)
    {
        var errors = new List<string>();
        Resolve(text, world, errors);
        return errors.Count > 0 ? errors[0] : null;
    }

    // Every command of `text` that resolves; each one that does not adds a
    // message to `errors` and is left out.
    public static HookCommand[] Resolve(string text, string world, List<string> errors)
    {
        var resolved = new List<HookCommand>();
        foreach (string token in ActionCommand.SplitCell(text))
        {
            ActionCommand command = ActionCommand.Parse(token, out string error);
            if (command == null)
            {
                errors.Add(error);
                continue;
            }
            HookCommand hook = ResolveOne(command, world, out error);
            if (hook == null)
            {
                errors.Add(error);
                continue;
            }
            resolved.Add(hook);
        }
        return resolved.ToArray();
    }

    // A placement's authored hooks, resolved for the events its entry raises.
    // Problems are reported with `where` (the placement) and the hook is
    // dropped; null when nothing is left.
    public static EntityHookSpec[] ResolveHooks(Godot.Collections.Array<EntityHook> hooks,
        EEntityEvent[] raised, string world, string where)
    {
        if (hooks == null || hooks.Count == 0)
        {
            return null;
        }
        var specs = new List<EntityHookSpec>();
        var errors = new List<string>();
        foreach (EntityHook hook in hooks)
        {
            if (hook == null)
            {
                continue;
            }
            if (System.Array.IndexOf(raised, hook.hookEvent) < 0)
            {
                GD.PushError($"{where}: a hook on {hook.hookEvent}, which this entity never raises. Dropped.");
                continue;
            }
            errors.Clear();
            HookCommand[] commands = Resolve(hook.commands, world, errors);
            foreach (string error in errors)
            {
                GD.PushError($"{where}: {hook.hookEvent} hook: {error}. Command dropped.");
            }
            if (commands.Length == 0)
            {
                continue;
            }
            specs.Add(new EntityHookSpec { On = hook.hookEvent, Once = hook.once, Commands = commands });
        }
        return specs.Count > 0 ? specs.ToArray() : null;
    }

    private static HookCommand ResolveOne(ActionCommand command, string world, out string error)
    {
        error = null;
        if (command.NeedsSpeaker)
        {
            error = $"'{command.Token}' acts on a speaker, and a placement has none";
            return null;
        }
        switch (command.Kind)
        {
            case EActionCommand.SetVar:
                if (System.Array.IndexOf(DeclaredVariables(), command.Name) < 0)
                {
                    error = $"'{command.Token}' writes '{command.Name}', which no script_variables registry declares";
                    return null;
                }
                return new HookCommand
                {
                    Kind = command.Kind,
                    Ref = command.Name,
                    Op = command.Op,
                    Operand = command.Operand,
                };
            case EActionCommand.StartQuest:
            case EActionCommand.CompleteQuest:
            {
                string path = Find(typeof(QuestData), command.Name, QuestRoots(world));
                if (path == null)
                {
                    error = $"unknown quest '{command.Name}' - no .tres by that basename under worlds/shared/quests/"
                        + (world != null ? $" or worlds/{world}/quests/" : "");
                    return null;
                }
                return new HookCommand { Kind = command.Kind, Ref = path };
            }
            case EActionCommand.Named:
            {
                string path = Find(typeof(ConversationAction), command.Name, ActionRoots(world));
                if (path == null)
                {
                    error = $"unknown action '{command.Name}' - no .tres by that name in dialogue_scripts/actions/, worlds/shared/conversation/actions/"
                        + (world != null ? $" or worlds/{world}/conversations/actions/" : "");
                    return null;
                }
                if (ResourceLoader.Load(path) is ConversationAction action && action.NeedsSpeaker)
                {
                    error = $"'{command.Name}' acts on a speaker, and a placement has none";
                    return null;
                }
                return new HookCommand { Kind = command.Kind, Ref = path };
            }
            case EActionCommand.Teach:
                error = $"'{command.Token}' - a lesson is identified by the file it comes from, and a hook is not one; teach from an authored action file instead";
                return null;
            default:
                error = $"'{command.Token}' cannot run on a placement hook";
                return null;
        }
    }

    // Most specific first: a world's own file shadows a shared one.
    private static string[] QuestRoots(string world)
    {
        return world != null
            ? new[] { WorldScope.RootOf(world) + "quests/", SharedQuests }
            : new[] { SharedQuests };
    }

    private static string[] ActionRoots(string world)
    {
        return world != null
            ? new[] { WorldScope.RootOf(world) + "conversations/actions/", SharedActions, ScriptActions }
            : new[] { SharedActions, ScriptActions };
    }

    private static string Find(System.Type type, string basename, string[] roots)
    {
        foreach (string root in roots)
        {
            foreach (string path in ResourceTypeIndex.In(type, new[] { root }))
            {
                if (path.GetFile().GetBaseName() == basename)
                {
                    return path;
                }
            }
        }
        return null;
    }

    // Every declared variable id, of either type.
    private static string[] DeclaredVariables()
    {
        var names = new List<string>();
        foreach (string path in ResourceTypeIndex.Candidates(typeof(ScriptVariableRegistry), null))
        {
            if (ResourceLoader.Load(path) is not ScriptVariableRegistry registry)
            {
                continue;
            }
            foreach (ScriptVariableData variable in registry.variables)
            {
                if (variable?.id != null && !variable.id.IsEmpty)
                {
                    names.Add(variable.id.ToString());
                }
            }
        }
        return names.ToArray();
    }
}
