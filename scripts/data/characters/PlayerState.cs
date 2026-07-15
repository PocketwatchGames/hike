using Godot;
using Godot.Collections;

// One party member — the complete "soul" of a playable character: identity +
// appearance picks, the character-sheet stat block, the starting loadout, and
// passive traits. Replaces the old CharacterCreationState (which only carried
// name + appearance); the starting loadout that used to live on WorldGenData is
// now per-character here, since each member owns their own gear.
//
// Authored as a template .tres and listed on WorldGenData.startingParty; the
// runtime Party is built by cloning each template (Resource.Duplicate) at game
// start. Named `State` (not `Data`) because a member is a mutable per-run record
// — its vitals and, later, its live inventory evolve as the run plays and are
// what SaveGame will persist. A Player node is hydrated from one of these.
[GlobalClass]
public partial class PlayerState : Resource
{
	[ExportGroup("Identity")]
	// Display name, shown on the stats panel. A proper name, authored as-is
	// rather than localized (names aren't translated). Blank falls back to the
	// Player's default name.
	[Export] public string characterName = "Wyatt Anderson";

	// Body type this member spawns as. Selects which base character model
	// renders (Player.Initialize looks it up in the player scene's per-gender
	// model-package map) and the voice. Defaults to Female (0).
	[Export] public EGender gender = EGender.Female;

	// Modular-appearance picks: each is an INDEX. skinTone / hairColor index the
	// gender-agnostic color palettes on PlayerData (skinTones / hairColors);
	// hairStyle indexes the rig's gender-specific hair menu (ModelAnimator
	// .hairStyleMeshNames). Out-of-range indices clamp to a sane default (or
	// bald), so a partially-authored pick never breaks. Defaults (0) pick the
	// first entry.
	[Export] public int skinTone;
	[Export] public int hairColor;
	[Export] public int hairStyle;

	[ExportGroup("Stats")]
	// The character sheet — per-member multipliers around 1.0 (1 = the
	// PlayerData baseline), folded into the shared stat-compose pipeline when
	// this member is the hosted Player (see Player.MemberStat / MaxHealth /
	// MaxStamina and ItemEventHandlers.ResolveHit):
	//   health / stamina — scale the member's max HP / stamina pools.
	//   strength         — scales melee-swing damage only (ranged/thrown unaffected).
	//   perception       — sharpens the player's own senses (Vision + Hearing).
	//   stealth          — quiets the player's emissions (Noise + Scent); higher = stealthier.
	//   fortitude        — resists incoming combat status buildup (via EStat.FortitudeResistance); higher = more resistant.
	//   charisma         — reserved for a future dialogue system; currently unused.
	[Export] public float health = 1f;
	[Export] public float stamina = 1f;
	[Export] public float fortitude = 1f;
	[Export] public float strength = 1f;
	[Export] public float perception = 1f;
	[Export] public float stealth = 1f;
	[Export] public float charisma = 1f;

	[ExportGroup("Starting Equipment")]
	// This member's starting loadout. Moved off WorldGenData (it was a shared
	// single-character loadout) so each party member carries their own gear.
	// Player.Initialize seeds these into the member's inventory at spawn.

	// Items spawned already equipped. Each is added to the inventory and then
	// auto-moved into its matching slot (armor → armorSlot, weapons →
	// CanonicalSlot); if the slot is taken or the item isn't equippable it stays
	// in the backpack.
	[Export] public ItemCount[] equippedInventory = System.Array.Empty<ItemCount>();

	// Items pushed into consumable hotbar slots in order. Each is created at
	// maxStack.
	[Export] public ConsumableData[] startingConsumables = System.Array.Empty<ConsumableData>();

	// Items added to the backpack at spawn. Each entry's count is split into
	// maxStack-sized stacks. No equip / hotbar placement.
	[Export] public ItemCount[] startingInventory = System.Array.Empty<ItemCount>();

	[ExportGroup("Traits")]
	// Passive status effects intrinsic to this character (perks / afflictions),
	// applied to the Player when this member is spawned / becomes controlled.
	[Export] public Array<StatusEffectData> traits = new();

	// Runtime (not authored): this member's PROVISIONAL individual knowledge —
	// items/recipes/species/regions/languages learned in the field while this
	// character was active, not yet banked into the shared party pool. Combined
	// with Party.Knowledge on every "do we know X?" read (see WorldSimState /
	// Player) and folded into the party pool when the player camps
	// (Party.BankActive). Survives death/revive because it lives here on the
	// PlayerState; lost only when the member is permanently destroyed. Field
	// initializer gives each cloned member its own store (same as the runtime
	// fields below).
	public readonly Knowledge Knowledge = new();

	// Runtime (not authored): true once this member has died in the field. A dead
	// member is not selectable and can't be controlled; their Player body remains
	// where it fell as a revivable corpse until another member revives them, at
	// which point this clears and they respawn at the campfire. Reset per run
	// because the runtime Party is a fresh clone of the templates.
	public bool IsDead;

	// Runtime (not authored): the World.DayNumber by which a fallen member must be
	// revived. Set to the day AFTER the one they wake at (one full day of grace);
	// once DayNumber reaches it un-revived (i.e. the party sleeps to that sunrise),
	// the member is destroyed permanently. 0 = no pending deadline (alive, or not
	// yet assigned).
	public int ReviveByDay;

	// Runtime (not authored): true once this member has eaten a cooked dish today.
	// Each character may cook and eat once per day; the camp Cook tab is withheld
	// from a member who already has. Cleared for the whole party at each sunrise
	// (GameClient handles World.OnNewDay).
	public bool HasEatenToday;

	// Runtime (not authored): days since this member was last the active
	// (controlled) character — the "rest" counter the well-rested lottery weights
	// by. 0 = currently or most-recently controlled (set the instant they become
	// active in Party.SetActive). Each sunrise every living member increments and
	// the still-controlled member is forced back to 0 (see
	// Party.AdvanceRestAndPickWellRested), so an idle member's odds of being drawn
	// climb the longer they sit out. Winning resets this to 1 — still eligible the
	// next day, just with the lowest odds.
	public int RestDays;

	// Runtime (not authored): set when this member joins mid-run (Party.Add) so
	// they win the FOLLOWING morning's well-rested lottery unconditionally — a
	// fresh face shows up rested. Cleared the moment they win.
	public bool ForceWellRestedNextDay;

	// Runtime (not authored): true while this member holds today's "well rested"
	// daily buff — one member is crowned each sunrise by the lottery and cleared
	// the next. Drives both the WellRested stat buff (applied to their Player
	// node) and the campfire glow particle (Player gates that on IsWellRested plus
	// actually sitting at the fire).
	public bool IsWellRested;
}
