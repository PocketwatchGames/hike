# Action System: Weapons, Consumables, Interactives

Covers `scripts/data/actions/` (authored data) and `scripts/gameplay/actions/` (runtime).

A single `ActionRunner` (one per actor) drives all timeline-based player and mob actions. Two distinct authored data shapes feed it.

## Slot-driven actions (weapons, consumables, mob attacks)

Pressed via input or AI request.

- `ItemActionProfile` (`scripts/data/actions/ItemActionProfile.cs`): one profile per slot. Holds an `Array<ItemAction> chargedActions` (the tiers), profile-level `chargeEvents` / `chargeEndEvents` / `abortEvents`, and behavioral flags (`autoActivateAtMax`, `locksMovement`, `interruptOnDamage`, `queueable`/`queueWindowSeconds`).
- `ItemAction` (`scripts/data/actions/ItemAction.cs`): one charge tier within a profile. Carries `chargeTime`, `activeDurationSeconds`, `cooldownSeconds`, `events` (Active timeline), `readyEvents` (announce reaching this tier), combo position (`comboIndex` / `comboWindowMs`), `requirements`, abort/interrupt policy (`canAbort` / `canInterrupt`), per-tier charge curves, and per-tier charge fx (`chargeStartEffect`, `chargeLoopEffect`, `chargeCancelEffect`, `releaseEffect`).

### Combos: two independent mechanisms

Both ride a per-weapon "press again before the window lapses" chain (`WeaponState`), reset when the wielder is hit (`Player.ResetWeaponCombos`), and coexist on one profile:

- **Tier-chain** (`ItemAction.comboIndex` / `comboWindowMs`, `WeaponState.comboIndex` / `comboExpireMs`): author *one full `ItemAction` tier per combo step*. `ActionRunner.ResolveTargetComboIndex` targets `previousComboIndex + 1` while the window is open, and tier selection filters to that index. Maximal flexibility (each step is a wholly different attack); heaviest authoring.
- **Repeat-swing** (`ItemAction.repeatActionOverrides` : `Array<ActionRepeatOverride>`, `WeaponState.repeatIndex` / `repeatExpireMs`): author *one* tier; its `repeatActionOverrides` array length IS the combo length and index IS the swing number. `EnterActive` walks `repeatIndex` through the list per press (wrapping to 0 after the last), and each `ActionRepeatOverride` layers per-swing `damageMultiplier` (applied in `ResolveHit`) + `cooldownSeconds` over the base tier. Activating a tier with an empty list (a charged finisher) closes the repeat window so the next basic press restarts at swing 0. Light authoring for the common "same swing N times, finisher is beefier" case (club = 3 swings, knife = 5). Per-swing anim/fx overrides land on `ActionRepeatOverride` in a later pass.
- Profiles are referenced from `WeaponData.actionProfile`, `ConsumableData.actionProfile`, and `AttackBehaviorData.actionProfile`.

## Interactive actions (chest, door, torch, loot)

Pressed via Interact.

- `InteractiveAction` (`scripts/data/actions/InteractiveAction.cs`): one verb's behavior on an interactive. Self-describes its `verb` (EActionVerb) + `displayName` (StringName, used by future radial UI). Carries `interactEvents` (timeline during the wait), `completionEvents` (fired as a batch at `durationSeconds` — this is where `OpenInteractive` lives, so authors don't have to align an event time to the duration), `requirements`, `locksMovement`, `interruptOnDamage`. No charging, queueing, combo, or auto-activate — interactives have one phase that runs to completion.
- `IInteractive` (`scripts/gameplay/IInteractive.cs`): exposes `Array<InteractiveAction> GetActions(Player)` and `Complete(int actionIndex)`. The first entry is the default action; future radial UI iterates the array reading `displayName` for each entry. The `ActionRunner` calls `interactive.Complete(context.interactiveActionIndex)` from the `OpenInteractive` event handler.

## ActionRunner

`scripts/gameplay/actions/ActionRunner.cs` — single-action runner with one in-flight `PlayerAction` plus an optional queued action. Two `TryStart` overloads (one per data shape) — `ItemActionProfile` enters Charging, walks tier selection, fires `readyEvents` on tier promotion, transitions to Active on release / `autoActivateAtMax`; `InteractiveAction` enters Active immediately, walks `interactEvents` over `durationSeconds`, fires `completionEvents` from `EndActive`. Aborts (player-initiated `TryAbort`, damage-driven `TryInterrupt`) skip `completionEvents` for interactives; weapons consult per-tier `canAbort` / `canInterrupt`.

## ItemEvent

`scripts/data/actions/ItemEvent.cs` — shared timeline event used by both shapes. `type` is a bitmask of `EItemEventType` flags (Melee, Hitscan, UseAmmo, ApplyEffect, DecrementStack, ToggleMovingLight, PlayAnim, PlaySound, OpenInteractive, ConsumeFromInventory) — a single event can fire several handlers at once (e.g. a healing potion's release tick is `ApplyEffect | DecrementStack`). Per-flag fields are unioned on the resource; the inspector hides fields whose owning flag isn't selected (`_ValidateProperty`), but storage is preserved so toggling a flag off and back on doesn't lose values. **Wire values are stable: append new bits, never reassign existing ones**, so existing `.tres` files keep loading. The `fx` field is the per-event audiovisual cue (e.g. the chest-creak puff lives on the `OpenInteractive` event in chest.tscn, not on the chest's C# class).

## Player flow

- Press Interact: `Player.TryStartInteractiveAction(highlight)` calls `runner.TryStart(actions[0], context)` and stashes `(_curInteractive, _curInteractiveActionIndex)` so the existing movement-lock and Interacting-anim checks (`_curInteractive != null`) keep working unchanged.
- After `_runner.Tick()`: if `_curInteractive != null` and the runner is no longer busy, the interactive completed naturally — clear `_curInteractive` and `_highlightInteractive`.
- Cancel (Jump / Sneak / repeat-Interact press): `CancelInteract` calls `_runner.TryAbort()` if mid-interactive, then clears `_curInteractive`.

## Adding a new interactive

Implement `IInteractive`, expose `[Export] Array<InteractiveAction> _actions`, return it from `GetActions`, branch on `actionIndex` (or `_actions[actionIndex].verb`) inside `Complete`. Author the `.tscn` with one or more inline `InteractiveAction` sub-resources, each carrying `verb`, `durationSeconds`, `interactEvents`, and `completionEvents` (typically `[OpenInteractive]`).

## Adding a new weapon / consumable verb

Extend `EActionVerb`, author a new `ItemAction` tier on the profile with the new verb tag, wire its `events` and per-tier fx. The runner's tier-selection loop picks it up via `comboIndex` / `chargeTime`.

## Gating tiers by actor physical state

`ActorStateRequirement` (`scripts/data/actions/requirements/ActorStateRequirement.cs`) reads `IActionActor.IsGrounded` / `IsSwimming` and exposes `forbidSwimming`, `requireSwimming`, `requireGrounded`, `requireAirborne`. Drop it on `ItemAction.requirements` to lock a tier to a physical state — e.g. all tiers carry `forbidSwimming = true` on the club, or a single `requireAirborne = true` tier on a club gives it a ground-slam variant that auto-selects when the player presses mid-jump. The runner's tier selection picks the highest qualifying tier, so an airborne-gated tier added above the normal swing (same `comboIndex`, same `chargeTime`) takes over when airborne and falls through to the swing on the ground.

When ALL tiers in the current combo step fail their requirements at press, `ActionRunner.StartImmediate` refuses the press outright and spawns `ItemActionProfile.rejectEffect` on the actor — author a one-shot `Fx` scene there for the "can't do that" cue (e.g. a splash + thud for the club while swimming). Without `rejectEffect`, the refusal is silent. Mobs keep `IsGrounded = true` / `IsSwimming = false` defaults — mob attacks evaluate cleanly against the same requirement type.
