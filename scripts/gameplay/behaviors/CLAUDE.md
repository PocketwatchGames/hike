# Mob AI System

Covers `scripts/gameplay/MobAI.cs`, `scripts/data/behaviors/` (authored tuning), and `scripts/gameplay/behaviors/` (runtime).

Per-mob hierarchical state machine driven by polymorphic Resource data.

**Mob combat lives elsewhere.** `BehaviorAttack` fires real `WeaponData` weapons off `Mob.Weapons` (the loadout from `SpeciesData.weapons`, not the brain or the base `MobData`), running the player's damage + weapon-mod path; `AttackBehaviorData` carries only behavior-level positioning. For weapon selection/priority gating, `primaryItem`, per-weapon cooldowns, held-model, and elite mob-mods, see [scripts/data/items/CLAUDE.md](../../data/items/CLAUDE.md).

## Data model (authored in `.tres`)

- `BrainData` (`scripts/data/BrainData.cs`) — `idleBehavior` (StringName) + `Array<BehaviorNode> behaviors`. One brain per mob type, referenced from `MobData.brain`.
- `BehaviorNode` — `name` (StringName, per-brain instance id), `data` (`BehaviorData` subclass), `Array<BehaviorNodeTransition> transitions`.
- `BehaviorData` (base, `scripts/data/BehaviorData.cs`) — abstract per-behavior tuning. Subclasses live in `scripts/data/behaviors/` (e.g. `IdleBehaviorData`, `AttackBehaviorData`). Override `CreateRuntime()` to return a fresh `BehaviorBase` instance bound to this data.
- `BehaviorNodeTransition` — `condition` (`BehaviorTransitionData` subclass) + `destination` (StringName naming a sibling node).
- `BehaviorTransitionData` (base, `scripts/data/BehaviorTransitionData.cs`) — abstract transition predicate. Subclasses live in `scripts/data/behaviors/conditions/` (e.g. `AggroAcquiredCondition`). Override `Evaluate(Mob, ref PerceptionState)`.

## Runtime

- `BehaviorBase` (base, `scripts/gameplay/BehaviorBase.cs`) — runtime instance per mob. Subclasses live in `scripts/gameplay/behaviors/` (e.g. `BehaviorIdle`, `BehaviorAttack`). Override `Run(Mob, time, ref PerceptionState, ref AIOutput)`. Use `TryTransitions(...)` to evaluate the node's transitions; on a hit return `new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination)`. Otherwise write to `AIOutput` and return `Running`. Per-instance state (timers, sub-state) lives on the runtime instance — never on the shared data Resource.
- **Behaviors are server-side only and must never reference client content (FX scenes, audio, animations); to trigger a presentational cue, emit an intent flag on `AIOutput` (`yell`, `oneShotAnim`, `vocalization`, …) and let `Mob` map it to the authored `PackedScene`/animation wired in the mob `.tscn` (e.g. `_vocalizationEffects`).**
- `Mob.InitBehaviors()` walks `mobData.brain`, instantiates each `BehaviorData.CreateRuntime()`, calls `Init(node)`, populates `_behaviors` (Dictionary<StringName, BehaviorBase>), validates transition destinations, sets `_curBehavior = brain.idleBehavior`.
- `Mob.TickAI(deltaTime, out AIOutput)` runs in `_PhysicsProcess` at 60Hz. Picks the highest-perception triggered slot from `_simState.PerceptionTargets`, then runs the current behavior; behavior output drives actuation (`Mob._PhysicsProcess` reads `AIOutput.pathTarget` and applies impulses, with damping toggling for braking).

## Perception

- `MobSimState.PerceptionTargets[]` — one `PerceptionState` slot per potential target (currently sized 1 for the player; preserved as an array for future multiplayer). Each slot has `perception` (slow-accumulating awareness), `triggered` (latched binary; sets when perception hits `MobData.PerceptionThresholdAlert`, clears at 0), `canSee`, `lastKnownPosition`, and the target reference. Some mobs additionally track the nearest mob on the **opposite side of the player divide** (`Teams.IsPlayerSide`) in `MobSimState.ThreatPerception` (same struct, fed by `AccumulateThreatPerception`/`ThreatScan`). This second channel is **derived, not authored**: it runs only for a `dangerous` mob (tracks the player's companions to attack them) or a tamed companion (a guard dog, aware of enemies *and* harmless wildlife). A companion perceives an opposite-side creature on sight; a hostile only latches onto a player-side target once that target is itself `triggered` (so it ignores an idling pet and keeps focus on the player). There is no `threatTeam` to set.
- `Mob.UpdatePerception()` is throttled via `MobSimState.PerceptionTickAccumulator` / `PerceptionTickInterval` (~10Hz, jittered per-mob at construction so raycasts don't clump on the same frame). Behaviors stay at 60Hz so combat reactions are responsive.

## Aggro (target priority, separate from perception)

Perception answers *who is this mob aware of*; **aggro** answers *which engaged enemy to hit*. They're independent mechanics keyed on the same enemies.

- `MobSimState.Aggro` (`AggroTracker`) — a small per-mob table of decaying aggro values, one per tracked enemy. `Mob.Damage` credits the attacker `healthDamage * DamageData.aggroMultiplier`; `Player.OnHurtBoxHit` relays the same onto `World.Companion` so a pet prioritizes whoever is mauling its master. The table decays each perception tick by `MobData.aggroReductionSpeed` and prunes dead/freed targets. Transient — not serialized.
- Selection: a hostile mob weighs the player (its perception slot) against the companion it tracks via `ThreatPerception` in `BehaviorAttack.ResolveTarget`, committing to the higher-aggro one (ties default to the player). A companion ranks opposite-side mobs by aggro in `ThreatScan.FindNearest` (nearest breaks ties). A hostile tracks the companion automatically because it's `dangerous` (no per-mob faction needed); give its brain a `ThreatPerceivedCondition (Alert)` edge into its attack state to act on it (see `goblin.tres` / `goblin_brain.tres`).
- `MobData.canTriggerMobs` is read off the *perceived* mob and gates whether *seeing* it is enough to start a fight. `true` (the default — most hostiles) = a scanner on the opposite side that fully perceives it engages on sight. `false` (the tamed pet) = scanners build awareness only; they enter combat with it solely by being attacked by it (`Mob.Hit` latches the threat slot directly, bypassing this gate). So "harmlessness" travels with the creature being looked at, not the looker.

## Adding a new behavior

1. Create `FooBehaviorData : BehaviorData` in `scripts/data/behaviors/` with `[Export]` tuning fields and `CreateRuntime() => new BehaviorFoo(this)`.
2. Create `BehaviorFoo : BehaviorBase` in `scripts/gameplay/behaviors/`. Constructor takes the data; `Run` calls `TryTransitions` first, then writes to `AIOutput`.
3. Add a `BehaviorNode` to the brain `.tres` with a unique `name`, the new data subclass, and any transitions.

## Adding a new transition condition

1. Create `FooCondition : BehaviorTransitionData` in `scripts/data/behaviors/conditions/` overriding `Evaluate`.
2. Wire it as the `condition` of a `BehaviorNodeTransition` sub-resource in the brain `.tres`.

Both base classes are non-abstract (`virtual` with `GD.PushError` fallback) so `[GlobalClass]` plays nicely with Godot's editor picker. Subclasses must be tagged `[GlobalClass]` to surface in the inspector.
