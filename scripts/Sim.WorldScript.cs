using Godot;

// Dispatches sim events to the world's script (WorldScriptData's hooks). Only a
// game binds one — the editor's player-less Sim never does, so a script never
// runs while a world is being authored.
public partial class Sim
{
    private WorldScriptApi _scriptApi;

    // WorldScriptApi.After calls waiting on the sim clock, in no order. Not
    // saved: a save is a sunrise wake, and nothing a script delays outlives one.
    private readonly System.Collections.Generic.List<(ulong dueMs, System.Action<WorldScriptApi> then)> _scriptDelays = new();

    // Bind this world's authored scripted content (quests, script hooks) onto
    // the runtime state. The hooks read ScriptData at dispatch, so nothing here
    // captures the script itself.
    public void BindScriptData(WorldScriptData scriptData)
    {
        if (_worldState == null)
        {
            return;
        }
        _worldState.ScriptData = scriptData;
        if (_scriptApi != null)
        {
            return;
        }
        _scriptApi = new WorldScriptApi(this);
        OnDawn += () => _worldState.ScriptData?.OnDawn(_scriptApi);
        OnRest += () => _worldState.ScriptData?.OnRest(_scriptApi);
        OnNightfall += () => _worldState.ScriptData?.OnNightfall(_scriptApi);
        onMobKilled += (species, byPlayer) => _worldState.ScriptData?.OnMobKilled(_scriptApi, species, byPlayer);
        ScriptVariableBank vars = _worldState.SimState?.ScriptVars;
        if (vars != null)
        {
            vars.OnChanged += DispatchVariableChanged;
        }
    }

    // The bank belongs to SimState, which is not freed with this node.
    private void UnbindWorldScript()
    {
        _scriptDelays.Clear();
        ScriptVariableBank vars = _worldState?.SimState?.ScriptVars;
        if (_scriptApi != null && vars != null)
        {
            vars.OnChanged -= DispatchVariableChanged;
        }
    }

    // Called by GameClient once a New Game is playable — not an event like the
    // others, because only the client knows when the intro story is done.
    public void DispatchNewGame()
    {
        if (_scriptApi != null)
        {
            _worldState?.ScriptData?.OnNewGame(_scriptApi);
        }
    }

    public void ScheduleScriptDelay(double seconds, System.Action<WorldScriptApi> then)
    {
        _scriptDelays.Add((GameTimeMs + (ulong)(System.Math.Max(0.0, seconds) * 1000.0), then));
    }

    // Called from Tick. A callback may schedule another, so due ones are taken
    // out before any runs.
    private void TickScriptDelays()
    {
        if (_scriptDelays.Count == 0 || _scriptApi == null)
        {
            return;
        }
        ulong now = GameTimeMs;
        System.Collections.Generic.List<System.Action<WorldScriptApi>> due = null;
        for (int i = _scriptDelays.Count - 1; i >= 0; i--)
        {
            if (_scriptDelays[i].dueMs <= now)
            {
                due ??= new();
                due.Add(_scriptDelays[i].then);
                _scriptDelays.RemoveAt(i);
            }
        }
        if (due == null)
        {
            return;
        }
        for (int i = due.Count - 1; i >= 0; i--)
        {
            due[i](_scriptApi);
        }
    }

    private void DispatchVariableChanged(StringName id)
    {
        _worldState?.ScriptData?.OnVariableChanged(_scriptApi, id);
    }
}
