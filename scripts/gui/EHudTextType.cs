// Categorizes a floating HUD-text request so GameClient can pick the matching
// HudText scene (each scene bakes its own color / fade duration / vertical
// movement). Damage variants render red, heal variants render green with a
// '+' prefix, Info renders white. Append new entries — never reorder — so
// per-type scene wiring on GameClient stays stable.
public enum EHudTextType
{
	Info,
	DamageLight,
	DamageHeavy,
	Crit,
	Backstab,
	HealLight,
	HealHeavy,
	// A hit that landed but did nothing — zero displayed damage and no buildup
	// applied — renders "MISS!". Blocked is a hit the player's charged weapon
	// guard absorbed, rendered "BLOCKED!" in light blue. Parried is a fully
	// deflected + countered hit, rendered "PARRIED!".
	Miss,
	Blocked,
	Parried,
}
