using System.Collections.Generic;
using Godot;

// Who tripped a trigger, as a world script sees it — a record, not a node, so a
// script still reaches the game only through WorldScriptApi.
public readonly struct TriggerActor
{
    public readonly ETriggerActors Kind;
    // The placement name of a named mob; null for the party and anonymous mobs.
    public readonly string Name;
    public readonly ETeam Team;

    public TriggerActor(ETriggerActors kind, string name, ETeam team)
    {
        Kind = kind;
        Name = name;
        Team = team;
    }
}

// Tests the resident trigger boxes (TriggerZone) against the party, the
// companion and — on a slower cadence, and only while some resident trigger
// admits mobs — every resident mob. A world holds a handful of triggers, so
// this is a short list of AABB tests rather than physics areas.
public partial class Sim
{
    private readonly List<TriggerZone> _triggers = new();
    private readonly List<(Node3D Node, ETriggerActors Kind)> _triggerActors = new();
    private float _triggerMobScanAccumulator;

    public void RegisterTrigger(TriggerZone zone)
    {
        if (zone != null && !_triggers.Contains(zone))
        {
            _triggers.Add(zone);
        }
    }

    public void UnregisterTrigger(TriggerZone zone)
    {
        _triggers.Remove(zone);
    }

    private void TickTriggers(double delta)
    {
        if (_triggers.Count == 0)
        {
            return;
        }
        _triggerActors.Clear();
        if (_player != null && GodotObject.IsInstanceValid(_player) && !_player.IsDead)
        {
            _triggerActors.Add((_player, ETriggerActors.Controlled));
        }
        if (_partyNodes != null)
        {
            for (int i = 0; i < _partyNodes.Count; i++)
            {
                Player member = _partyNodes[i];
                if (member != null && member != _player && GodotObject.IsInstanceValid(member) && !member.IsDead)
                {
                    _triggerActors.Add((member, ETriggerActors.PartyMember));
                }
            }
        }
        Mob companion = Companion;
        bool companionLive = companion != null && GodotObject.IsInstanceValid(companion) && companion.alive;
        if (companionLive)
        {
            _triggerActors.Add((companion, ETriggerActors.Companion));
        }

        bool mobsScanned = false;
        float interval = _worldState?.SimData?.triggerMobScanSeconds ?? 0.25f;
        _triggerMobScanAccumulator += (float)delta;
        if (_triggerMobScanAccumulator >= interval && AnyTriggerWantsMobs())
        {
            _triggerMobScanAccumulator = 0f;
            mobsScanned = true;
            foreach (Mob mob in GetEntities<Mob>())
            {
                if (mob == companion || !GodotObject.IsInstanceValid(mob) || !mob.alive || mob.IsHidden)
                {
                    continue;
                }
                ETriggerActors kind = MobKind(mob.ActorTeam);
                if (kind != ETriggerActors.None)
                {
                    _triggerActors.Add((mob, kind));
                }
            }
        }

        // Every zone is evaluated before any fires, and exits fire before enters:
        // an actor stepping from one box straight into an adjacent one has left
        // the first before it arrives in the second, whatever order the zones
        // are listed in. (A script that sends its fairy away on leaving one box
        // and calls her to the next would otherwise lose her to the late exit.)
        // Firing can also stream entities in or out, which is why the zones are
        // all evaluated first.
        _triggerFires.Clear();
        foreach (TriggerZone zone in _triggers)
        {
            if (GodotObject.IsInstanceValid(zone) && zone.IsInsideTree())
            {
                zone.Evaluate(_triggerActors, mobsScanned, _triggerFires);
            }
        }
        foreach (TriggerZone.TriggerFire fire in _triggerFires)
        {
            if (!fire.Enter)
            {
                FireTrigger(fire.Zone, false, fire.Who, fire.Kind);
            }
        }
        foreach (TriggerZone.TriggerFire fire in _triggerFires)
        {
            if (fire.Enter)
            {
                FireTrigger(fire.Zone, true, fire.Who, fire.Kind);
            }
        }
    }

    private readonly List<TriggerZone.TriggerFire> _triggerFires = new();

    // Is a resident trigger with this name occupied right now? False for one
    // that is not streamed in.
    public bool IsTriggerOccupied(string name)
    {
        foreach (TriggerZone zone in _triggers)
        {
            if (zone.State.Name == name && zone.IsOccupied)
            {
                return true;
            }
        }
        return false;
    }

    private bool AnyTriggerWantsMobs()
    {
        foreach (TriggerZone zone in _triggers)
        {
            if (zone.State.WantsMobs)
            {
                return true;
            }
        }
        return false;
    }

    private static ETriggerActors MobKind(ETeam team) => team switch
    {
        ETeam.Hostile => ETriggerActors.HostileMob,
        ETeam.Friendly => ETriggerActors.FriendlyMob,
        ETeam.Neutral => ETriggerActors.NeutralMob,
        ETeam.Prey => ETriggerActors.PreyMob,
        _ => ETriggerActors.None,
    };

    // The box became occupied (enter) or empty (exit). A disabled trigger keeps
    // tracking who is inside but fires nothing; a spent Once trigger likewise.
    public void FireTrigger(TriggerZone zone, bool enter, Node3D who, ETriggerActors kind)
    {
        TriggerSimState state = zone.State;
        ScriptVariableBank vars = _worldState?.SimState?.ScriptVars;
        if (vars != null && state.IsDisabled(vars))
        {
            return;
        }
        if (state.Repeat == ETriggerRepeat.Once)
        {
            if (enter ? state.EnterFired : (state.ExitFired || !state.EnterFired))
            {
                return;
            }
        }
        if (enter)
        {
            state.EnterFired = true;
        }
        else
        {
            state.ExitFired = true;
        }

        StringName variable = enter ? state.EnterSetsVariable : state.ExitSetsVariable;
        if (variable != null && !variable.IsEmpty)
        {
            vars?.SetBool(variable, true);
        }

        ConversationAction[] actions = enter ? state.EnterActions : state.ExitActions;
        if (actions.Length > 0)
        {
            var ctx = new ConversationContext
            {
                sim = this,
                player = who as Player ?? _player,
            };
            foreach (ConversationAction action in actions)
            {
                action?.Execute(ctx);
            }
        }

        if (_scriptApi != null && state.Name != null)
        {
            Mob mob = who as Mob;
            var actor = new TriggerActor(kind, mob?.SimState?.Name,
                mob != null ? mob.ActorTeam : ETeam.Player);
            if (enter)
            {
                _worldState?.ScriptData?.OnTriggerEntered(_scriptApi, state.Name, actor);
            }
            else
            {
                _worldState?.ScriptData?.OnTriggerExited(_scriptApi, state.Name, actor);
            }
        }
    }
}
