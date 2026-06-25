using System;

// Bitmask on ItemEvent — a single event can fire several behaviors at once
// (e.g. ApplyEffect | DecrementStack on a healing potion's release tick).
// Wire values are stable — append new bits, never reassign existing ones,
// so existing weapon/consumable .tres files keep loading.
[Flags]
public enum EItemEventType
{
	Melee = 1 << 0,
	Hitscan = 1 << 1,
	UseAmmo = 1 << 2,
	ApplyStatusEffect = 1 << 3,
	DecrementStack = 1 << 4,
	ToggleMovingLight = 1 << 5,
	PlayAnim = 1 << 6,
//	PlaySound = 1 << 7, REMOVED
	// Calls Complete() on context.primaryInteractive — the universal way for
	// an interactive action's timeline to trigger the interactive's effect
	// (chest opens, door swings, lockpick succeeds).
	OpenInteractive = 1 << 8,
	// Decrements one unit from a matching item in context.supportingItems.
	// The matching item is identified by ev.reagent (an ItemData). On stack
	// reaching zero, the supporting item is removed from the player's
	// inventory.
	ConsumeFromInventory = 1 << 9,
	// Dispatches to IActionActor.ApplyMotion — the actor's physics layer
	// reads the event's speed/duration/freeze-gravity fields and drives the
	// resulting motion itself (dash for Player, lunge for Mob). Action events
	// emit motion *intent*; actor implementations resolve direction, friction,
	// terrain interactions, and end-of-motion behavior. Keeps the runner
	// physics-agnostic.
	ApplyMotion = 1 << 10,
	// Teaches Player.LearnLanguage(ev.language). Reusable across sources —
	// knowledge stones author it in their interactive's completionEvents, mob
	// dialogue can fire it from a chatter timeline, and a language-teaching
	// consumable authors it on the profile's release tick. The first-time
	// flash plays on the actor (typically the player) only when Add returns
	// true so re-reading a stone whose language is already known is silent.
	LearnLanguage = 1 << 11,
	// Teaches ev.concept (a TeachableConcept) to the learner. Superset of
	// LearnLanguage — handles language pieces, recipes, region locations,
	// and any future TeachableConcept subclass through one event type.
	// Scrolls author this on the consumable's release tick alongside
	// DecrementStack; NPCs (once IInteractive-backed) author it on a Talk
	// action's completion events.
	LearnConcept = 1 << 12,
	// Spawns a Projectile from ev.projectileData at the actor's position,
	// flying along the actor's forward (with the tier's accuracy spread).
	// On collision the projectile builds a HitInfo from the damage profile
	// the event resolves against the firing weapon (damageProfileKey) and
	// calls HurtBox.Hit — same payload shape as Hitscan, just delayed by
	// flight time.
	Projectile = 1 << 13,
	// Spawns ev.areaEffectScene at the actor's aim point. The scene is a
	// Node3D parented to the world (e.g. a GasCloud carrying a DamageZone
	// + particle loop). Pairs with EAimType.Positional — the player's aim
	// cursor (Player.AimWorldPosition) is the drop target. Falls back to
	// ActorWorldPosition when no aim cursor is active.
	SpawnAreaEffect = 1 << 14,
	// One-shot pulse that applies ev.effects to every same-team Mob (including
	// the actor itself) inside a sphere of ev.areaRadius around the actor.
	// Pairs naturally with a battle-cry style attack: the goblin yells, every
	// nearby goblin gets the speed / damage buff. Status-effect lifecycle is
	// owned by the receiver (each StatusEffectData carries its own duration
	// + fx), so this event just selects who and applies the list. ev.fx (if
	// set) spawns once at the actor as the source-side audiovisual cue.
	ApplyAreaStatusEffect = 1 << 15,
	// One-shot camera shake — magnitude + duration decaying linearly to 0.
	// Optional distance falloff against the player when range > 0; range == 0
	// fires the full magnitude regardless of where the event lives. Pairs
	// with the per-frame continuous shake source (ContinuousCameraShake)
	// attached to environmental hazards.
	CameraShake = 1 << 16,
	// Digs at the actor's aim point (or a short reach in front when no aim
	// cursor is active). Routes to World.TryDig, which excavates the nearest
	// buried-item spot in range — or, failing that, forces the nearest
	// burrowed mob to surface. The shovel consumable authors this on its Use
	// timeline alongside DecrementStack. See ev.digRadius.
	Dig = 1 << 17,
	// One-shot full-screen flash toward ev.screenFlashColor, decayed by
	// ScreenEffectsController. Parallels CameraShake — an action-timeline way to
	// punch a screenspace flash (a spell, a flashbang). For effect SCENES
	// (particle bursts, sounds) drop a ScreenFlashEmitter node in instead.
	ScreenFlash = 1 << 18,
	// Summons a minion mob (ev.minionData) on the player's team at the actor's
	// aim point (or position when no aim cursor is active). The minion follows
	// the player and self-drains via its MobData's authored drain status. The
	// summoning weapon (WeaponState) tracks its minions and recycles the oldest
	// once its cap is reached; unequip/remove destroys them. Authored at time=0
	// on the summoner's (zero-duration) Active timeline so the full-charge
	// auto-activate fires it. See ItemEventHandlers.DoSummonMinion.
	SummonMinion = 1 << 19,
	// One-shot controller rumble — weak/strong motor magnitudes decaying
	// linearly to 0 over a duration. Optional distance falloff against the
	// player when range > 0; range == 0 fires full magnitude regardless of
	// where the event lives. Haptic parallel to CameraShake — author both on
	// the same impact event for a hit that shakes the screen and the pad.
	ControllerRumble = 1 << 20,
}
