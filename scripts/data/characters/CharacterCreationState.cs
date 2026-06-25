using Godot;

// The player's character-creation choices for a run — name plus the modular
// appearance picks. Authored as a default in a .tres and handed to the
// new-game pipeline (the slot the old PlayerSpawnData filled), where
// Player.Initialize resolves it to the live visual. The starting loadout that
// used to ride alongside these picks now lives on WorldGenData (it's a
// property of the world/scenario, not the character).
[GlobalClass]
public partial class CharacterCreationState : Resource
{
	// The player character's display name, shown on the stats panel. A proper
	// name, so it's authored as-is rather than localized (names aren't
	// translated). Blank falls back to the Player's default name.
	[Export] public string playerName = "Wyatt Anderson";

	// Body type the player spawns as. Selects which base character model
	// renders — Player.Initialize looks this up in the player scene's
	// per-gender model-package map and instances the matching package (each
	// gender shares the skeleton + animation library, so only the body model
	// differs). Defaults to Female (0).
	[Export] public EGender gender = EGender.Female;

	// Modular-appearance picks: each is an INDEX. skinTone / hairColor index the
	// gender-agnostic color palettes on PlayerData (skinTones / hairColors);
	// hairStyle indexes the rig's gender-specific hair menu (ModelAnimator
	// .hairStyleMeshNames). Player.Initialize resolves them to a flat recolor on
	// the skin / hair meshes and the hair mesh to show. Out-of-range indices
	// clamp to a sane default (or bald), so a partially-authored pick never
	// breaks. Defaults (0) pick the first entry.
	[Export] public int skinTone;
	[Export] public int hairColor;
	[Export] public int hairStyle;
}
