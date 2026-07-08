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
	// The character sheet. DISPLAY-ONLY for now — surfaced in the camp stats
	// panel but not yet folded into combat / perception / status gameplay (that
	// wiring is a follow-up). health / stamina are max values (default matches
	// PlayerData); strength / perception / stealth are multipliers around 1;
	// fortitude resists negative status buildup; charisma is currently unused.
	[Export] public float health = 100f;
	[Export] public float stamina = 100f;
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

	// Runtime (not authored): the World.TimeOfDayAbsolute by which a fallen member
	// must be revived. Set to the sunrise AFTER the one they wake at (one full day
	// out); if the clock reaches it un-revived, the member is destroyed
	// permanently. 0 = no pending deadline (alive, or not yet assigned).
	public double ReviveDeadlineAbsolute;
}
