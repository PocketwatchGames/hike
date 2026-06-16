using Godot;

// Chain-lightning payload: an arc that hops between nearby enemies, dealing
// Electrical-tagged damage to each link. Fired off an attack impact — by the
// Shocking weapon mod (player weapons) and the elite lightning aura (goblins)
// alike. Wet targets take the bonus automatically through the receiver's
// tag-resistance fold (status_wet's Electrical modifier). See
// ItemEventHandlers.ApplyChainLightning.
// [Tool] so the editor can bind it under its [Tool] parent StatusEffectData.
[Tool]
[GlobalClass]
public partial class ChainLightningData : Resource
{
	// Damage applied to each chained target. Tag it Electrical so wet targets
	// take the wetness bonus. Null = no chain.
	[Export] public DamageData damage;

	// Maximum number of targets the arc strikes (links/hops). Each step picks a
	// random in-range enemy not yet struck by this chain.
	[Export(PropertyHint.Range, "1,10,1,or_greater")] public int maxChains = 3;

	// Search radius (meters) for the next link, measured from the current link's
	// position (the impact point for the first hop).
	[Export(PropertyHint.Range, "1,15,0.5,or_greater")] public float chainRange = 4f;

	// One-shot visual + sound spawned at each struck target. Null = no fx.
	[Export] public PackedScene fx;
}
