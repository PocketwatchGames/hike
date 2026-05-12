// Wire bytes are stable; entries are append-only so existing world files keep
// loading. Bytes 2 (legacy "AutoLoot") and 3 (legacy "Loot") were retired when
// loot moved to its own LootSimState — EntitySerializer's legacy Tag.Prop
// reader still recognises those bytes and converts them to LootSimState, so
// 2 and 3 must not be reused by any future PropType entry.
public enum PropType : byte
{
	Tree = 0,
	TallGrass = 1,
}
