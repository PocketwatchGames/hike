// Names a player action. Selection layer (input mapping, charge tier, hold-menu)
// resolves the verb; the runner then runs the profile authored for that verb.
// Mostly diagnostic at runtime — by the time an action is in flight, the
// profile fully describes the behavior. Names appear in saved data and combat
// logs, so be specific (Light/Heavy/Use/Open/Break/Lockpick) rather than
// generic (Primary/Secondary).
public enum EActionVerb
{
	None,
	Light,
	Heavy,
	Use,
	Open,
	Break,
	Lockpick,
	Block,
}
