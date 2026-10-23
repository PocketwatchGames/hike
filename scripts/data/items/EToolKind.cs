// What a carried item can serve as besides its main use (ItemData.toolUses):
// a blade cuts, a digging tool unearths a buried spot. Each kind
// has code behind it, so a new one is an enum member plus its use site.
// Written into .tres as an int — APPEND-ONLY.
public enum EToolKind
{
	None,
	Blade,
	Digging,
}
