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
- `Mob.InitBehaviors()` walks `mobData.brain`, instantiates each `BehaviorData.CreateRuntime()`, calls `Init(node)`, populates `_behaviors` (Dictionary<StringName, BehaviorBase>), validates transition destinations, sets `_curBehavior = brain.idleBehavior`.
- `Mob.TickAI(deltaTime, out AIOutput)` runs in `_PhysicsProcess` at 60Hz. Picks the highest-perception triggered slot from `_simState.PerceptionTargets`, then runs the current behavior; behavior output drives actuation (`Mob._PhysicsProcess` reads `AIOutput.pathTarget` and applies impulses, with damping toggling for braking).

## Perception

- `MobSimState.PerceptionTargets[]` — one `PerceptionState` slot per potential target (currently sized 1 for the player; preserved as an array for future multiplayer). Each slot has `perception` (slow-accumulating awareness), `triggered` (latched binary; sets when perception hits `MobData.PerceptionThresholdAlert`, clears at 0), `canSee`, `lastKnownPosition`, and the target reference. A companion additionally tracks the nearest enemy mob in `MobSimState.ThreatPerception` (same struct, fed by `AccumulateThreatPerception`/`ThreatScan`, enabled by setting `MobData.threatTeam` to a real team — `None` means don't scan).
- `Mob.UpdatePerception()` is throttled via `MobSimState.PerceptionTickAccumulator` / `PerceptionTickInterval` (~10Hz, jittered per-mob at construction so raycasts don't clump on the same frame). Behaviors stay at 60Hz so combat reactions are responsive.

## Aggro (target priority, separate from perception)

Perception answers *who is this mob aware of*; **aggro** answers *which engaged enemy to hit*. They're independent mechanics keyed on the same enemies.

- `MobSimState.Aggro` (`AggroTracker`) — a small per-mob table of decaying aggro values, one per tracked enemy. `Mob.Damage` credits the attacker `healthDamage * DamageData.aggroMultiplier`; `Player.OnHurtBoxHit` relays the same onto `World.Companion` so a pet prioritizes whoever is mauling its master. The table decays each perception tick by `MobData.aggroReductionSpeed` and prunes dead/freed targets. Transient — not serialized.
- Selection: a hostile mob weighs the player (its perception slot) against the companion it tracks via `ThreatPerception` in `BehaviorAttack.ResolveTarget`, committing to the higher-aggro one (ties default to the player). A companion ranks hostiles by aggro in `ThreatScan.FindNearest` (nearest breaks ties). To make a hostile species track the companion, set `threatTeam = Friendly` on its `MobData` and give its brain a `ThreatPerceivedCondition (Alert)` edge into its attack state (see `goblin.tres` / `goblin_brain.tres`).
- `MobData.canTriggerMobs` is read off the *perceived* mob and gates whether *seeing* it is enough to start a fight. `true` (the default — most hostiles) = a scanner that fully perceives it engages on sight. `false` (the tamed pet) = scanners build awareness only; they enter combat with it solely by being attacked by it (`Mob.Hit` latches the threat slot directly, bypassing this gate). The hit-latch is gated to `threatTeam` so the awareness edge agrees with what `AccumulateThreatPerception` tracks. So "harmlessness" travels with the creature being looked at, not the looker.

## Adding a new behavior

1. Create `FooBehaviorData : BehaviorData` in `scripts/data/behaviors/` with `[Export]` tuning fields and `CreateRuntime() => new BehaviorFoo(this)`.
2. Create `BehaviorFoo : BehaviorBase` in `scripts/gameplay/behaviors/`. Constructor takes the data; `Run` calls `TryTransitions` first, then writes to `AIOutput`.
3. Add a `BehaviorNode` to the brain `.tres` with a unique `name`, the new data subclass, and any transitions.

## Adding a new transition condition

1. Create `FooCondition : BehaviorTransitionData` in `scripts/data/behaviors/conditions/` overriding `Evaluate`.
2. Wire it as the `condition` of a `BehaviorNodeTransition` sub-resource in the brain `.tres`.

Both base classes are non-abstract (`virtual` with `GD.PushError` fallback) so `[GlobalClass]` plays nicely with Godot's editor picker. Subclasses must be tagged `[GlobalClass]` to surface in the inspector.
