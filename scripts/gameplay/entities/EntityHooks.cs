// A placed entity's hooks at runtime: the placement's resolved specs (shared,
// immutable) plus this entity's own fired flags, which are saved with it.
// Null on a state whose placement authored none.
public sealed class EntityHooks
{
    public readonly EntityHookSpec[] Specs;
    public readonly bool[] Fired;

    // Built on first raise, per spec.
    private ConversationAction[][] _actions;

    public EntityHooks(EntityHookSpec[] specs, bool[] fired = null)
    {
        Specs = specs;
        Fired = fired ?? new bool[specs.Length];
    }

    // Runs every hook on `on` that may still fire, in authored order.
    public void Raise(EEntityEvent on, ConversationContext ctx)
    {
        for (int i = 0; i < Specs.Length; i++)
        {
            EntityHookSpec spec = Specs[i];
            if (spec.On != on || (spec.Once && Fired[i]))
            {
                continue;
            }
            Fired[i] = true;
            foreach (ConversationAction action in ActionsOf(i))
            {
                action?.Execute(ctx);
            }
        }
    }

    private ConversationAction[] ActionsOf(int index)
    {
        _actions ??= new ConversationAction[Specs.Length][];
        if (_actions[index] == null)
        {
            HookCommand[] commands = Specs[index].Commands;
            var actions = new ConversationAction[commands.Length];
            for (int c = 0; c < commands.Length; c++)
            {
                actions[c] = commands[c].Materialize();
            }
            _actions[index] = actions;
        }
        return _actions[index];
    }
}
