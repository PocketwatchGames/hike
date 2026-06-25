using Godot;

// A character's vocalization bank — the voice-over set for one actor (player
// body type or mob species). Each field is a one-shot Fx scene whose audio is
// the actor's own voice; they ride on top of the shared, gender-/species-
// agnostic impact / death-splat / breath-puff / yell-particle effects so only
// the vocal layer varies per character.
//
// Used by both Player (a per-gender map, resolved from the spawned gender) and
// Mob (a single per-species slot). Adding a voice = author a VoiceData .tres
// and wire it in the actor scene; no new fields or code branches. Any slot may
// be null — that vocalization is simply silent for this voice (the spawn
// helpers no-op on null), so a mob that never yells or a voice with no hurt
// clip still works.
//
// pitchShift makes cheap voice variants: point several VoiceData at the same
// clips with different pitch to get distinct-sounding characters (e.g. a crowd
// of villagers) without recording new audio. Applied to every voice clip this
// bank spawns.
[GlobalClass]
public partial class VoiceData : Resource
{
	// Pain cry layered over the blood-impact effect on each damaging hit.
	[Export] public PackedScene hurt;
	// Death cry layered over the death splat on the killing blow.
	[Export] public PackedScene death;
	// Vocalization on being revived (a companion brought back from death) —
	// layered over the action's shared revive cue. Null for voices that never
	// revive.
	[Export] public PackedScene revive;
	// Alert shout spawned when the actor yells (mob aggro acquisition / first
	// hit). Players have none; left null for voices that never yell.
	[Export] public PackedScene yell;
	// Gasp/pant spawned the moment stamina is exhausted (player only). Self-
	// anchored and carries its own breath-puff particles.
	[Export] public PackedScene outOfBreath;

	// Multiplier applied to the pitch_scale of every voice clip this bank
	// spawns (1 = as recorded). Drives cheap per-voice variation — e.g. a
	// lighter/younger villager at 1.15, a heavier one at 0.85 — from shared
	// source clips. 0.01 step so authors can nudge it finely.
	[Export(PropertyHint.Range, "0.5,2,0.01")] public float pitchShift = 1.0f;
}
