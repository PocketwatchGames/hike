# Mob AI System

Covers `scripts/gameplay/MobAI.cs`, `scripts/data/behaviors/` (authored tuning), and `scripts/gameplay/behaviors/` (runtime).

Per-mob hierarchical state machine driven by polymorphic Resource data.

**Mob combat lives elsewhere.** `BehaviorAttack` fires real `WeaponData` weapons off `Mob.Weapons` (the loadout from `SpeciesData.weapons`, not the brain or the base `MobData`), running the player's damage + weapon-mod path; `AttackBehaviorData` carries only behavior-level positioning. For weapon selection/priority gating, `primaryItem`, per-weapon cooldowns, held-model, and elite mob-mods, see [scripts/data/items/CLAUDE.md](../../data/items/CLAUDE.md).

## Data model (authored in `.tres`)

- `BrainData` (`scripts/data/BrainData.cs`) — `idleBehavior` (StringName) + `Array<BehaviorNode> behaviors`. One brain per mob type, referenced from `MobData.brain`.
- `BehaviorNode` — `name` (StringName, per-brain instance id), `data` (`BehaviorData` subclass), `Array<BehaviorNodeTransition> transitions`.
- `BehaviorData` (base, `scripts/data/BehaviorData.cs`) — abstract per-behavior tuning. Subclasses live in `scripts/data/behaviors/` (e.g. `IdleBehaviorData`, `AttackBehaviorData`). Override `CreateRuntime()` to return a fresh `BehaviorBase` instance bound to this data. Also carries `behaviorFlags` (`EBehaviorFlags`, a `[Flags]` bitmask) — the behavior's resting *stance*: `Engaging` (Attack/Investigate/Wary/Dodge/aerial-attack), `Disengaging` (Flee/Retreat/escape), or `None` (idle/wander/look/follow). It's authored `[Export]` but each subclass sets its own correct default in its constructor, so authors never touch it and existing brains pick it up on next save. Consumed by the interactive danger gate (`Sim.IsDangerNear` reads `Mob.IsEngaging`) — a fleeing mob is not danger, a hunting one is even behind cover.
- `BehaviorNodeTransition` — `condition` (`BehaviorTransitionData` subclass) + `destination` (StringName naming a sibling node).
- `BehaviorTransitionData` (base, `scripts/data/BehaviorTransitionData.cs`) — abstract transition predicate. Subclasses live in `scripts/data/behaviors/conditions/` (e.g. `AggroAcquiredCondition`). Override `Evaluate(Mob, ref PerceptionState)`.

## Runtime

- `BehaviorBase` (base, `scripts/gameplay/BehaviorBase.cs`) — runtime instance per mob. Subclasses live in `scripts/gameplay/behaviors/` (e.g. `BehaviorIdle`, `BehaviorAttack`). Override `Run(Mob, time, ref PerceptionState, ref AIOutput)`. Use `TryTransitions(...)` to evaluate the node's transitions; on a hit return `new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination)`. Otherwise write to `AIOutput` and return `Running`. Per-instance state (timers, sub-state) lives on the runtime instance — never on the shared data Resource.
- **Behaviors are server-side only and must never reference client content (FX scenes, audio, animations); to trigger a presentational cue, emit an intent flag on `AIOutput` (`oneShotAnim`, `vocalization` — including `EVocalization.Yell`, the alarm that also broadcasts a mob investigation, …) and let `Mob` map it to the authored `PackedScene`/animation wired in the mob `.tscn` (e.g. `_vocalizationEffects`).**
- `Mob.InitBehaviors()` walks `mobData.brain`, instantiates each `BehaviorData.CreateRuntime()`, calls `Init(node)`, populates `_behaviors` (Dictionary<StringName, BehaviorBase>), validates transition destinations, sets `_curBehavior = brain.idleBehavior`.
- `Mob.TickAI(deltaTime, out AIOutput)` runs in `_PhysicsProcess` at 60Hz. Picks the highest-perception triggered slot from `_simState.PerceptionTargets`, then runs the current behavior; behavior output drives actuation (`Mob._PhysicsProcess` reads `AIOutput.pathTarget` and applies impulses, with damping toggling for braking).

## Perception

- `MobSimState.PerceptionTargets[]` — one `PerceptionState` slot per potential target (currently sized 1 for the player; preserved as an array for future multiplayer). Each slot has `perception` (slow-accumulating awareness), `triggered` (latched binary; sets when perception hits `MobData.perceptionThresholdAlert`, clears at 0), `canSee`, `lastKnownPosition`, and the target reference. Some mobs additionally track the nearest mob on the **opposite side of the player divide** (`Teams.IsPlayerSide`) in `MobSimState.ThreatPerception` (same struct, fed by `AccumulateThreatPerception`/`ThreatScan`). This second channel is **derived, not authored**: it runs only for a `dangerous` mob (tracks the player's companions to attack them) or a tamed companion (a guard dog, aware of enemies *and* harmless wildlife). A companion perceives an opposite-side creature on sight; a hostile only latches onto a player-side target once that target is itself `triggered` (so it ignores an idling pet and keeps focus on the player). There is no `threatTeam` to set.
- `Mob.UpdatePerception()` is throttled via `MobSimState.PerceptionTickAccumulator` / `PerceptionTickInterval` (~10Hz, jittered per-mob at construction so raycasts don't clump on the same frame). Behaviors stay at 60Hz so combat reactions are responsive.
- **Suspicion is the "something rattled me" channel.** `Mob.RaiseSuspicion(level)`
  multiplies `perceptionIncreaseSpeed` by `level`, bleeding off at a constant
  `MobData.suspicionDecayPerSecond` (0.1/s, so a 2x spike is gone in ten seconds),
  on BOTH the player slot and the threat channel — a jumpy mob is quicker about
  everything. It scales growth only INSIDE the `perceptionDelta >
  minPerceptionDelta` branch, so it sharpens a detection the mob was already
  making and can never conjure one out of nothing. Stored as (peak, set-time) on
  the sim clock rather than integrated per tick, so the throttled perception tick
  can read it at any cadence; a fresh spike takes the higher of the two levels, so
  a lesser scare can't calm a mob. **Re-raising each tick is how a behavior HOLDS
  it** — the stamp resets, so the bleed starts when the calls stop, which is what
  keeps a mob fully on edge for the whole walk to a body. Raised today only by the
  corpse stimuli below; any future stimulus (a heard scream, a sprung trap) raises
  it the same way.

## Aggro (target priority, separate from perception)

Perception answers *who is this mob aware of*; **aggro** answers *which engaged enemy to hit*. They're independent mechanics keyed on the same enemies.

- `MobSimState.Aggro` (`AggroTracker`) — a small per-mob table of decaying aggro values, one per tracked enemy. `Mob.Damage` credits the attacker `healthDamage * DamageData.aggroMultiplier`; `Player.OnHurtBoxHit` relays the same onto `World.Companion` so a pet prioritizes whoever is mauling its master. The table decays each perception tick by `MobData.aggroReductionSpeed` and prunes dead/freed targets. Transient — not serialized.
- Selection: a hostile mob weighs the player (its perception slot) against the companion it tracks via `ThreatPerception` in `BehaviorAttack.ResolveTarget`, committing to the higher-aggro one (ties default to the player). A companion ranks opposite-side mobs by aggro in `ThreatScan.FindNearest` (nearest breaks ties). A hostile tracks the companion automatically because it's `dangerous` (no per-mob faction needed); give its brain a `ThreatPerceivedCondition (Alert)` edge into its attack state to act on it (see `goblin.tres` / `goblin_brain.tres`).
- `MobData.canTriggerMobs` is read off the *perceived* mob and gates whether *seeing* it is enough to start a fight. `true` (the default — most hostiles) = a scanner on the opposite side that fully perceives it engages on sight. `false` (the tamed pet) = scanners build awareness only; they enter combat with it solely by being attacked by it (`Mob.Hit` latches the threat slot directly, bypassing this gate). So "harmlessness" travels with the creature being looked at, not the looker.

## Reacting to a dead body

**One behavior, two entry points, one stimulus slot.** `MobSimState.CorpseSighting`
(`Mob.Corpses.cs`) holds the body a mob has noticed and not finished reacting to,
and `BehaviorInspectCorpse` runs every case off it — they are the same phase
machine with legs dropped:

| Case | Phases |
|---|---|
| Watched an attacker make the kill | glance → run over → study → face where the damage came from |
| Watched a trap / hazard make it | stare (a trap is a place to avoid, not a scene to inspect) |
| Came across the body later | glance → run over → study |
| A species that never approaches, or any flier | stare |

- **The stimulus carries FACTS, the node carries DURATIONS.** `damageOrigin` (null
  when nothing was there to look for) and `approach` are what the sighting knows;
  every timing is an `[Export]` on `CorpseInspectBehaviorData` — a `Vector2`
  (min, max) second range rolled per phase, the same idiom as
  `WanderBehaviorData.pauseTimeRange`, so a group reacting to one body does not
  move in lockstep. A species that
  only ever stares sets `approach = false` — the sparrow / kun-kun / drake /
  creature-hostile brains do. A FLIER never approaches whatever the data says:
  the walk over is ground pathing.
- **Both entry points go through `Mob.NoticeCorpse`, which remembers the body as
  it posts** — not when the reaction finishes — so an aggro interrupt can never
  replay it. `MobSimState.SeenCorpses` is a capped ring of instance ids, transient.
- **Noticing a body is SEEING it**: both entry points gate on `Mob.IsInVisionCone`
  (the boolean half of the facing term `UpdatePerception` grades, flattened to XZ
  because a body lies on the ground) plus a `Sightline` ray, against the looker's
  own `visionRange`. A death behind a mob's back does not register.
- **Witnesses are found at the death** (`Mob.Die` → `BroadcastCorpseSighting`,
  shaped like the yell's `BroadcastInvestigation`): spatial query, then the gate
  above per candidate. The killer is remembered rather than notified, so it never
  inspects its own kill.
- **Noticing a body makes a mob jumpy**, via the suspicion channel above:
  `witnessedDeathSuspicion` (2) for seeing it happen, `foundCorpseSuspicion` (1.5)
  for walking up on what is left, both authored on `CorpseInspectBehaviorData`.
  The level is HELD for the whole reaction (re-raised every tick off
  `CorpseSighting.witnessed`) and only starts bleeding off once the mob is done
  with the body, so a goblin that watched you kill its packmate stays sharp
  through the walk over and for ten seconds after — on top of the last leg
  turning it to face where the blow came from.
- **The brain wires the reaction; the species can decline it.**
  `MobData.noticesCorpses` (false on the slimes) is how a creature too mindless to
  register a body opts out of a brain it shares with species that keep it — the
  same shape as `canTriggerMobs`, and it skips that mob's scan entirely.
- **A body nobody saw die is found by polling**, so it rides the throttled
  perception tick and walks `Sim.Corpses` (registered in `Die`, unregistered on
  tree exit) rather than a spatial query per mob per tick — the no-corpses case
  has to cost a count check.
- **Aggro outranks it both ways**: the brain's aggro/threat edges are wired out of
  the node, and engaging the player clears a pending sighting in `TickAI` exactly
  as it clears an investigation. The body stays remembered, so nothing resumes
  after the fight.
- Wire it per brain: one `InspectCorpse` node, a `HasCorpseSightingCondition` edge
  in from the idle/wander nodes ordered BELOW the aggro and alarm edges, and the
  brain's existing attack/flee transitions reused as the way out.

## Adding a new behavior

1. Create `FooBehaviorData : BehaviorData` in `scripts/data/behaviors/` with `[Export]` tuning fields and `CreateRuntime() => new BehaviorFoo(this)`. **If the behavior is an engaged/pursuing or a fleeing stance, set `behaviorFlags` in its constructor** (`Engaging` / `Disengaging`); leave it unset (`None`) for neutral idle/wander behaviors. This is what the interactive danger gate keys off — a new attack-like behavior that forgets it won't register as danger.
2. Create `BehaviorFoo : BehaviorBase` in `scripts/gameplay/behaviors/`. Constructor takes the data; `Run` calls `TryTransitions` first, then writes to `AIOutput`. Mob seeds `AIOutput.behaviorFlags` from the running node's `behaviorFlags` each tick; a behavior may `|=` extra bits it alone knows at runtime (see `BehaviorAttack` adding `Attacking` mid-swing, which feeds the CombatTracker). Mob caches the composed value on `MobSimState.CurrentBehaviorFlags` for out-of-tick readers (danger, despawn).
3. Add a `BehaviorNode` to the brain `.tres` with a unique `name`, the new data subclass, and any transitions.

## Adding a new transition condition

1. Create `FooCondition : BehaviorTransitionData` in `scripts/data/behaviors/conditions/` overriding `Evaluate`.
2. Wire it as the `condition` of a `BehaviorNodeTransition` sub-resource in the brain `.tres`.

Both base classes are non-abstract (`virtual` with `GD.PushError` fallback) so `[GlobalClass]` plays nicely with Godot's editor picker. Subclasses must be tagged `[GlobalClass]` to surface in the inspector.
