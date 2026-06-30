using Godot;

public struct AIOutput
{
    public Vector3? pathTarget;
    public float? yaw;
    public float speed;
    // When non-null, Mob's tick will TryStart this action through its
    // ActionRunner (subject to the runner's busy / cooldown checks).
    // attackContext supplies target / supportingItems / etc.; primaryItem is
    // the WeaponState for the firing weapon (Mob.GetWeapon), which carries the
    // damage profile and any weapon-mods (elite lightning).
    public ItemActionProfile attackProfile;
    public ActionContext attackContext;
    public Vector3 targetPos;
//    public Actor target;
    public float pathSuccessDistance;
    public bool inCombat;
    // Set true by combat behaviors (BehaviorAttack) when actively engaging a
    // target. Distinct from inCombat above (mob-awareness, used for AI-tick
    // LOD): this drives the player-facing CombatTracker via
    // Mob.ReportPlayerCombat.
    public bool combatBehavior;
    public bool burrow;
    // Flying mobs (MobData.CanFly) only: when true, the mob is airborne this
    // tick — physics disables gravity and runs ApplyFlightPhysics (hover +
    // wind + steering) instead of ground locomotion. Behaviors set this while
    // moving between points and clear it to land; a flying combatant
    // (BehaviorFlyAttack) holds it true for the whole engagement. flyAltitude
    // (when set) overrides MobData.hoverHeight as the target height above
    // terrain (low/medium/high cruise tiers just vary this value).
    public bool airborne;
    public float? flyAltitude;
    // Absolute target world-Y for a flier, overriding the terrain-relative
    // flyAltitude when set. Used by aerial combat to anchor hover height to the
    // target (e.g. the player's own elevation) rather than to the ground; the
    // physics layer still floors it just above the local surface and caps it
    // below any ceiling, so "player height or min 1m up" falls out for free.
    // flyAltitude is ignored on ticks where this is set.
    public float? flyTargetY;
    // True when TickAI early-returned because the mob is AI-suspended
    // (BehaviorIdle latches a 100ms suspend window once it's standing at
    // spawn so idle mobs can be physics-frozen). Downstream consumers of
    // AIOutput must treat all other fields as undefined and skip any
    // edge-style processing.
    public bool suspended;
    public InvestigateState? investigation;
    public bool resetInvestigation;
    public ulong? suspendTimeMs;
    // When set, Mob latches this as a one-shot animation through PlayOneShot
    // for the next tick. Looping animations are state-driven (alive/moving)
    // and chosen each tick by Mob.UpdateAnimation; behaviors only need to
    // emit one-shots (attack swing, yell, burrow stab) here. Nullable so
    // a behavior can leave it unset most ticks.
    public EAnimation? oneShotAnim;
    // A vocalization the behavior wants the body to perform this tick (growl,
    // snarl, bark, whimper). The behavior layer only names the intent; the Mob
    // scene maps it to an Fx scene / animation via _vocalizationEffects. Nullable
    // so a behavior leaves it unset on ticks with nothing to say.
    public EVocalization? vocalization;
    // One-shot: the mob started a dodge dash this tick. Mob spawns the authored
    // _dashFx at its feet. The dash motion itself is driven directly through
    // Mob.ApplyDodge (MotionVelocity), not this flag — this is purely the
    // presentational cue, kept off the behavior layer like vocalization.
    public bool dash;
}
public struct BehaviorOutput
{
    public BehaviorOutput(EBehaviorResult r, StringName newB = null)
    {
        result = r;
        newBehavior = newB;
    }
    public EBehaviorResult result;
    public StringName newBehavior;
}
public enum EBehaviorResult
{
    Running,
    RunNewBehavior,
    Complete,
}

public partial class Mob
{
//    private Vector3? _fleePosition;
    private StringName _curBehavior;
    private readonly System.Collections.Generic.Dictionary<StringName, BehaviorBase> _behaviors = new();

    // Called from Mob.Initialize after _simState is set. Walks the mob's BrainData,
    // creates a runtime BehaviorBase per node, validates that every transition
    // target names a real node, and sets the current behavior to idleBehavior.
    private void InitBehaviors()
    {
        _behaviors.Clear();
        _curBehavior = default;

        BrainData brain = mobData?.brain;
        if (brain == null || brain.behaviors == null)
        {
            return;
        }

        foreach (BehaviorNode node in brain.behaviors)
        {
            if (node == null || node.data == null)
            {
                GD.PushError($"Brain '{brain.ResourcePath}' contains a null node or node with null data");
                continue;
            }
            if (_behaviors.ContainsKey(node.name))
            {
                GD.PushError($"Brain '{brain.ResourcePath}' has duplicate behavior name '{node.name}'");
                continue;
            }
            BehaviorBase runtime = node.data.CreateRuntime();
            if (runtime == null)
            {
                continue;
            }
            runtime.Init(node);
            _behaviors[node.name] = runtime;
        }

        // Validate that every transition destination names a known node.
        foreach (BehaviorNode node in brain.behaviors)
        {
            if (node?.transitions == null)
            {
                continue;
            }
            foreach (BehaviorNodeTransition t in node.transitions)
            {
                if (t == null || t.destination == null)
                {
                    continue;
                }
                if (!_behaviors.ContainsKey(t.destination))
                {
                    GD.PushError($"Brain '{brain.ResourcePath}' node '{node.name}' has transition to unknown destination '{t.destination}'");
                }
            }
        }

        StringName initial = _simState.InitialBehavior;
        if (initial != null && _behaviors.ContainsKey(initial))
        {
            _curBehavior = initial;
        }
        else
        {
            _curBehavior = brain.idleBehavior;
        }
        // Fire OnEnter for the starting behavior so its first tick sees the
        // same fresh-state guarantees that every later re-entry will. World
        // time isn't always meaningful at Initialize (the sim clock starts
        // ticking once GameClient runs), so 0 is fine — behaviors that need
        // a real timestamp can read me.World.GameTimeMs themselves.
        if (_curBehavior != null && _behaviors.TryGetValue(_curBehavior, out BehaviorBase startB) && startB != null)
        {
            startB.OnEnter(this, 0);
        }
    }

    private void TickAI(float deltaTime, out AIOutput output)
    {
        using var _profTickAI = Profiler.Sample("Mob.TickAI");
        output = new AIOutput();
        if (!alive)
        {
            output.suspended = true;
            return;
        }

        ulong time = _world.GameTimeMs;

        // Scan perception BEFORE honoring SuspendAITimeMs — a perception
        // trigger has to wake the mob immediately so the distance-LOD
        // suspend extension in Mob.PhysicsProcess doesn't strand a mob
        // through a player approach.
        // Pick the engaged target slot. Among the perception slots (the player
        // in singleplayer), the triggered slot with the strongest awareness
        // wins — this is the "who is this mob aware of" decision. The separate
        // aggro mechanic (who has hurt it most) is applied downstream, in
        // BehaviorAttack.ResolveTarget, where the player slot is weighed against
        // the companion the mob is also tracking via ThreatPerception.
        PerceptionState targetPerception = default;
        PerceptionState[] targets = _simState.PerceptionTargets;
        bool triggered = false;
        float maxPerception = 0f;
        float bestTriggeredPerception = -1f;
        for (int idx = 0; idx < targets.Length; idx++)
        {
            ref PerceptionState s = ref targets[idx];
            triggered |= s.triggered;
            if (s.perception > maxPerception)
            {
                maxPerception = s.perception;
            }
            if (s.triggered && s.perception >= bestTriggeredPerception)
            {
                bestTriggeredPerception = s.perception;
                targetPerception = s;
            }
        }

        if (!triggered && _simState.SuspendAITimeMs > time)
        {
            output.suspended = true;
            return;
        }

        if (!triggered)
        {
            _simState.Yelled = false;
        }
        if (targetPerception.pawnTarget != null)
        {
            investigation = null;
        }

        int maxAttempts = 5;
        for (int i = 0; i < maxAttempts; i++)
        {
            if (_curBehavior != null && _behaviors.TryGetValue(_curBehavior, out BehaviorBase b) && b != null)
            {
                BehaviorOutput behaviorOutput;
                using (Profiler.Sample("Mob.BehaviorRun"))
                {
                    behaviorOutput = b.Run(this, time, ref targetPerception, ref output);
                }
                if (behaviorOutput.newBehavior != null)
                {
                    StartBehavior(behaviorOutput.newBehavior);
                    if (behaviorOutput.result == EBehaviorResult.RunNewBehavior)
                    {
                        continue;
                    }
                }
                else if (behaviorOutput.result == EBehaviorResult.Complete)
                {
                    StartBehavior(defaultBehavior);
                }
            }
            break;
        }

        // Navigator runs after the behavior so behaviors can set high-level
        // intent via Navigator.Goto/Wander and have the navigator translate
        // it into a pathTarget for the impulse layer. Behaviors that already
        // wrote pathTarget directly (legacy) win — the navigator only fills
        // it in when it's still null. See MobNavigator.WriteSteering.
        if (_navigator != null && !output.pathTarget.HasValue)
        {
            using (Profiler.Sample("Mob.NavigatorWriteSteering"))
            {
                _navigator.WriteSteering(deltaTime, ref output);
            }
        }

        output.inCombat = maxPerception > 0 && mobData != null && mobData.dangerous;

        // output.combatBehavior is set by the combat behaviors themselves during
        // the Run loop above (BehaviorAttack), not here — Mob._PhysicsProcess
        // combines it with dangerous + player-perception to feed the
        // CombatTracker (see ReportPlayerCombat).
    }

    // Feed the player-facing CombatTracker each tick. Only a dangerous hostile
    // the player currently perceives reports; `combatBehavior` says it's in an
    // attack behavior right now. Dead mobs don't report (a fresh corpse you can
    // still see mustn't keep combat alive) — Mob.Die routes the kill through
    // CombatTracker.OnMobDied for the instant-end-on-kill rule.
    private void ReportPlayerCombat(in AIOutput output)
    {
        if (!alive || mobData == null || !mobData.dangerous) { return; }
        if (!playerCanSee) { return; }
        if (Teams.AreAllied(ActorTeam, ETeam.Player)) { return; }
        GameClient.Current?.Combat?.Report(this, output.combatBehavior, _world.GameTimeMs);
    }

    private void StartBehavior(StringName behaviorName)
    {
        if (behaviorName == _curBehavior)
        {
            return;
        }
        if (!_behaviors.TryGetValue(behaviorName, out BehaviorBase b) || b == null)
        {
            GD.PushError($"Mob attempted to start unknown behavior '{behaviorName}'");
            return;
        }
        _curBehavior = behaviorName;
        b.OnEnter(this, _world?.GameTimeMs ?? 0);
    }
}
