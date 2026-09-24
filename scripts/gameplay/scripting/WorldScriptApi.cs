using Godot;

// Everything a world script (WorldScriptData subclass) may do to the running
// game. Kept narrow on purpose: it is the one surface a script is written
// against, so it has to stay stable as the sim underneath it changes — and a
// future data-driven trigger (event -> conditions -> actions, authored as .tres)
// would call the same verbs.
//
// Variables are named by string. The bank warns on an undeclared name, but
// validate_script_vars cannot see names written in C#, so declare every one a
// script uses in a ScriptVariableRegistry.
public class WorldScriptApi
{
    private readonly Sim _sim;

    public WorldScriptApi(Sim sim)
    {
        _sim = sim;
    }

    private ScriptVariableBank Vars => _sim.WorldState?.SimState?.ScriptVars;

    public int DayNumber => _sim.DayNumber;

    public bool GetBool(StringName id) => Vars?.GetBool(id) ?? false;

    public long GetInt(StringName id) => Vars?.GetInt(id) ?? 0;

    public void SetBool(StringName id, bool value) => Vars?.SetBool(id, value);

    public void SetInt(StringName id, long value) => Vars?.SetInt(id, value);

    public void AddInt(StringName id, long delta) => Vars?.AddInt(id, delta);

    // Every door an author gave this name (a painter placement's name), opened
    // or closed whether or not it is streamed in. False, with an error, when no
    // door has the name — a script aimed at a typo'd or renamed door.
    public bool OpenDoor(string name) => SetDoorsOpen(name, true);

    public bool CloseDoor(string name) => SetDoorsOpen(name, false);

    private bool SetDoorsOpen(string name, bool open)
    {
        bool any = false;
        foreach (EntitySimState entity in _sim.WorldState.FindNamed(name))
        {
            if (entity is DoorSimState door)
            {
                _sim.SetDoorOpen(door, open);
                any = true;
            }
        }
        if (!any)
        {
            GD.PushError($"World script: no door is named '{name}'.");
        }
        return any;
    }
}
