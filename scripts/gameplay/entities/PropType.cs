// Wire bytes are stable; entries are append-only so existing world files keep
// loading. AutoLoot replaced the original "Loot" enum value at byte 2 — same
// auto-pickup semantics, renamed for clarity now that an interactive Loot
// variant exists in the Loot.cs class.
public enum PropType : byte
{
	Tree = 0,
	TallGrass = 1,
	AutoLoot = 2,
	Loot = 3,
}
