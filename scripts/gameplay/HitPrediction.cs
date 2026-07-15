// Result of a non-mutating hit prediction (HurtBox.QueryHit): the resolved
// hit type plus the crit/backstab trigger flags that would fold, bundled so an
// attacker predicts a swing's base impact scene AND its crit/backstab overlays
// in a single pass — the receiver runs the crit/backstab test once per query
// instead of once per field. Prediction only; the authoritative apply
// (HurtBox.Hit) re-derives both on the receiver (see HurtBox for the
// networked-play split).
public readonly struct HitPrediction
{
	public readonly EHitResult Result;
	public readonly EDamageTriggerFlags Triggers;

	public HitPrediction(EHitResult result, EDamageTriggerFlags triggers)
	{
		Result = result;
		Triggers = triggers;
	}

	public static readonly HitPrediction None = new HitPrediction(EHitResult.None, EDamageTriggerFlags.None);
}
