// Phases of an in-flight player action. Cooldown is bookkeeping on the
// driving item (cooldownExpireMs), not a runner phase — Active ends and the
// runner returns to Ready immediately. The driving weapon/item gates its own
// re-firing via cooldownExpireMs at start time.
public enum EActionPhase
{
	Ready,
	Charging,
	Active,
}
