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

    // Runs `then` once, this many seconds of sim time from now (it pauses with
    // the game). Lost if the run ends first — a save is a sunrise wake — so a
    // delay must not carry anything that has to survive one; that is a script
    // variable.
    public void After(double seconds, System.Action<WorldScriptApi> then) => _sim.ScheduleScriptDelay(seconds, then);

    // Every door an author gave this name (a painter placement's name), opened
    // or closed whether or not it is streamed in. False, with an error, when no
    // door has the name — a script aimed at a typo'd or renamed door.
    public bool OpenDoor(string name) => SetDoorsOpen(name, true);

    public bool CloseDoor(string name) => SetDoorsOpen(name, false);

    // Opens the conversation of the mob an author gave this name, as if the
    // player had talked to it. It must be streamed in — place it near where the
    // party will be. False, with an error, when no resident mob has the name or
    // it has no conversation.
    public bool StartConversation(string name)
    {
        foreach (EntitySimState entity in _sim.WorldState.FindNamed(name))
        {
            if (entity is MobSimState && entity.RuntimeNode is Mob mob && mob.SpeakDialogue())
            {
                return true;
            }
        }
        GD.PushError($"World script: no streamed-in mob named '{name}' has a conversation to start.");
        return false;
    }

    // Speaks branch `key` of the named mob's conversation as a STANDING line:
    // typed over it in its language, paragraphs advancing on their own, nothing
    // for the player to press. The last paragraph stays until the next Say,
    // Hush, a conversation with that mob, or the mob leaving the world. The
    // branch must have no choices and no line actions (that is a conversation —
    // StartConversation), and the mob must be streamed in. False, with an
    // error, otherwise.
    public bool Say(string mob, string key)
    {
        Mob speaker = ResidentMob(mob, $"say '{key}'");
        return speaker != null && speaker.Say(key);
    }

    // Lowers the named mob's standing line. With `key`, only while it is still
    // saying that branch — so leaving one zone does not silence the line the
    // next zone has already raised.
    public void Hush(string mob, string key = null)
    {
        ResidentMob(mob, "hush")?.Hush(key);
    }

    // Sends the named mob to `target` — walking, or running — then runs `then`
    // and hands it back to its brain. `target` is a streamed-in entity's name
    // (another mob: the goal follows it as it moves) or else a named place.
    // Overrides the brain for the trip and stops a follow. Runtime only: lost if
    // the mob streams out on the way.
    public bool SendTo(string mob, string target, bool run = false, System.Action<WorldScriptApi> then = null)
    {
        Mob mover = ResidentMob(mob, $"go to '{target}'");
        if (mover == null)
        {
            return false;
        }
        System.Action done = then == null ? null : () => then(this);
        Node3D node = ResidentNode(target, mover);
        if (node != null)
        {
            mover.ScriptMoveTo(node, run, done);
            return true;
        }
        if (!TryPlace(_sim, target, out Vector3 at))
        {
            return false;
        }
        mover.ScriptMoveTo(at, run, done);
        return true;
    }

    // The live node of a streamed-in entity with this name, other than `self`,
    // or null.
    private Node3D ResidentNode(string name, Node3D self)
    {
        foreach (EntitySimState entity in _sim.WorldState.FindNamed(name))
        {
            if (entity.RuntimeNode is Node3D node && node != self && GodotObject.IsInstanceValid(node))
            {
                return node;
            }
        }
        return null;
    }

    // The named mob trails the controlled player until StopFollowing. Saved,
    // and the mob is never streamed out while it follows.
    public bool Follow(string mob)
    {
        Mob follower = ResidentMob(mob, "follow");
        follower?.ScriptFollow(true);
        return follower != null;
    }

    public bool StopFollowing(string mob)
    {
        Mob follower = ResidentMob(mob, "stop following");
        follower?.ScriptFollow(false);
        return follower != null;
    }

    // The named mob rises, fades out and stays HIDDEN — still in the world,
    // saved as hidden — until Reappear brings it back.
    public bool Vanish(string mob)
    {
        Mob mover = ResidentMob(mob, "vanish");
        mover?.Vanish();
        return mover != null;
    }

    // The named mob vanishes `seconds` of sim time from now — unless any later
    // command to it (Reappear, SendTo, Follow...) comes first, which cancels it.
    // A grace period: a player stepping out of a box and back in keeps her.
    public bool VanishAfter(string mob, float seconds)
    {
        Mob mover = ResidentMob(mob, "vanish");
        mover?.ScriptVanishAfter(seconds);
        return mover != null;
    }

    // Is the named mob hidden (vanished and not yet back)? Off its saved state.
    public bool IsHidden(string mob)
    {
        foreach (EntitySimState entity in _sim.WorldState.FindNamed(mob))
        {
            if (entity is MobSimState state && state.Hidden)
            {
                return true;
            }
        }
        return false;
    }

    // Is the trigger box with this name occupied by the actors it admits right
    // now? For a callback that lands after a delay (a Reappear's `then`) to
    // check the player has not already left. False when it is not streamed in.
    public bool IsTriggerOccupied(string trigger) => _sim.IsTriggerOccupied(trigger);

    // Is the named mob following the player? Read off its saved state, so it
    // answers whether or not the mob is streamed in.
    public bool IsFollowing(string mob)
    {
        foreach (EntitySimState entity in _sim.WorldState.FindNamed(mob))
        {
            if (entity is MobSimState state && state.ScriptFollowing)
            {
                return true;
            }
        }
        return false;
    }

    // The named mob vanishes (if it is not hidden already) and fades back in at
    // `target` — a streamed-in entity's name (over another mob, wherever that
    // mob is when it comes back in) or else a named place — and stays there.
    // Then `then` runs. The quick way to put a mob somewhere far off.
    public bool Reappear(string mob, string target, System.Action<WorldScriptApi> then = null)
    {
        Mob mover = ResidentMob(mob, $"reappear at '{target}'");
        if (mover == null)
        {
            return false;
        }
        System.Action done = then == null ? null : () => then(this);
        Node3D node = ResidentNode(target, mover);
        if (node != null)
        {
            mover.Reappear(node, done);
            return true;
        }
        if (!TryPlace(_sim, target, out Vector3 at))
        {
            return false;
        }
        mover.Reappear(at, done);
        return true;
    }

    // Reappear over the controlled player, wherever they are when the mob comes
    // back in.
    public bool ReappearAtPlayer(string mob, System.Action<WorldScriptApi> then = null)
    {
        Mob mover = ResidentMob(mob, "reappear at the player");
        if (mover == null || _sim.player == null)
        {
            return false;
        }
        mover.Reappear(_sim.player, then == null ? null : () => then(this));
        return true;
    }

    // A scripted mob stays where the script last put it; this hands it back to
    // its own brain.
    public bool Release(string mob)
    {
        Mob mover = ResidentMob(mob, "release");
        mover?.ScriptRelease();
        return mover != null;
    }

    // A named PLACE: any named painter placement — a waypoint, an NPC, a door —
    // as the bake registered it among the world's points of interest. False,
    // with an error, for a name nothing carries.
    public static bool TryPlace(Sim sim, StringName place, out Vector3 at)
    {
        at = default;
        string name = place?.ToString();
        if (!string.IsNullOrEmpty(name) && sim?.WorldState != null
            && sim.WorldState.PointsOfInterest.TryGetValue(name, out at))
        {
            return true;
        }
        GD.PushError($"World script: no place is named '{name}' — name a painter placement (a waypoint) that.");
        return false;
    }

    // The live node of the mob an author gave this name, or null with an error
    // naming what the script wanted it for.
    private Mob ResidentMob(string name, string purpose)
    {
        foreach (EntitySimState entity in _sim.WorldState.FindNamed(name))
        {
            if (entity is MobSimState && entity.RuntimeNode is Mob mob && GodotObject.IsInstanceValid(mob))
            {
                return mob;
            }
        }
        GD.PushError($"World script: no streamed-in mob named '{name}' to {purpose}.");
        return null;
    }

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
