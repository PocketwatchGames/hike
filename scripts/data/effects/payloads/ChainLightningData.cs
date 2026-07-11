using Godot;

// Chain-lightning payload: an arc that hops between nearby enemies, feeding an
// Electrical BUILDUP meter on each link rather than dealing damage directly.
// A link that crosses its shock threshold DISCHARGES (dischargeDamage — a jolt
// of instant damage plus a Dizzy contribution) and the arc leaps onward with a
// reduced buildup; a link that fails to cross stops the chain. Wet targets are
// twice as vulnerable to Electrical buildup (status_wet's Electrical modifier
// folds through the receiver's tag resistance), so arcs propagate freely
// through a soaked crowd and fizzle on dry ones. Fired off an attack impact —
// by the Shocking weapon mod (player weapons) and the elite lightning aura
// (goblins) alike. See ItemEventHandlers.ApplyChainLightning.
// [Tool] so the editor can bind it under its [Tool] parent StatusEffectData.
[Tool]
[GlobalClass]
public partial class ChainLightningData : Resource
{
	// The Electrical buildup meter each link accrues (status_shocked). A hop
	// only continues once a link's meter crosses the threshold. Null = no chain.
	[Export] public StatusEffectData shockEffect;

	// Buildup fed to the FIRST link (before the receiver's Electrical resistance /
	// vulnerability folds in). Author >= 1 so a dry target still crosses on the
	// opening zap; a wet target (2x) crosses with headroom to spare.
	[Export(PropertyHint.Range, "0,4,0.05,or_greater")] public float buildupPerHit = 1.2f;

	// Buildup multiplier applied on each successive hop (the arc weakens as it
	// travels). At 0.6 the second link gets 60% of the first, the third 36%, etc.
	// — so how far the chain reaches is set by how many links can still cross,
	// which wetness (2x) directly extends.
	[Export(PropertyHint.Range, "0,1,0.01")] public float chainBuildupFalloff = 0.6f;

	// The discharge landed on a link the instant its meter crosses: instant
	// Electrical-tagged damage plus (via its own buildups) a Dizzy contribution,
	// so repeated shocks escalate toward a stun. Applied through the normal hit
	// pipeline, so wet targets take the extra Electrical damage too. Null = the
	// crossing only gates the chain and deals no damage of its own.
	[Export] public DamageData dischargeDamage;

	// Maximum number of targets the arc strikes (links/hops). Each step picks a
	// random in-range enemy not yet struck by this chain.
	[Export(PropertyHint.Range, "1,10,1,or_greater")] public int maxChains = 3;

	// Search radius (meters) for the next link, measured from the current link's
	// position (the impact point for the first hop).
	[Export(PropertyHint.Range, "1,15,0.5,or_greater")] public float chainRange = 4f;

	// One-shot visual + sound spawned at each struck target. Null = no fx.
	[Export] public PackedScene fx;

	// Bolt arc drawn between consecutive links — and from the attacker to the
	// impact point — when this fires. Its scene root is a LightningBolt (see
	// scenes/fx/lightning_arc.tscn). Null = no bolt visual.
	[Export] public PackedScene boltFx;
}
