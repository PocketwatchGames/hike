using Godot;

// Dispatches sim events to the world's script (WorldScriptData's hooks). Only a
// game binds one — the editor's player-less Sim never does, so a script never
// runs while a world is being authored.
public partial class Sim
{
    private WorldScriptApi _scriptApi;

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
        OnNewDay += day => _worldState.ScriptData?.OnNewDay(_scriptApi, day);
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
        ScriptVariableBank vars = _worldState?.SimState?.ScriptVars;
        if (_scriptApi != null && vars != null)
        {
            vars.OnChanged -= DispatchVariableChanged;
        }
    }

    private void DispatchVariableChanged(StringName id)
    {
        _worldState?.ScriptData?.OnVariableChanged(_scriptApi, id);
    }
}
