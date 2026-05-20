# Action System: Weapons, Consumables, Interactives

Covers `scripts/data/actions/` (authored data) and `scripts/gameplay/actions/` (runtime).

A single `ActionRunner` (one per actor) drives all timeline-based player and mob actions. Two distinct authored data shapes feed it.

## Slot-driven actions (weapons, consumables, mob attacks)

Pressed via input or AI request.

- `ItemActionProfile` (`scripts/data/actions/ItemActionProfile.cs`): one profile per slot. Holds an `Array<ItemAction> chargedActions` (the tiers), profile-level `chargeEvents` / `chargeEndEvents` / `abortEvents`, and behavioral flags (`autoActivateAtMax`, `locksMovement`, `interruptOnDamage`, `queueable`/`queueWindowSeconds`).
- `ItemAction` (`scripts/data/actions/ItemAction.cs`): one charge tier within a profile. Carries `chargeTime`, `activeDurationSeconds`, `cooldownSeconds`, `events` (Active timeline), `readyEvents` (announce reaching this tier), combo position (`comboIndex` / `comboWindowMs`), `requirements`, abort/interrupt policy (`canAbort` / `canInterrupt`), per-tier charge curves, and per-tier charge fx (`chargeStartEffect`, `chargeLoopEffect`, `chargeCancelEffect`, `releaseEffect`).
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
