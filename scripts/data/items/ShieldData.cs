using Godot;

// An off-hand shield. The sneak crouch raises it: while the player sneaks with a
// shield equipped, its guard pool soaks hits before central armor and a
// well-timed crouch parries (Player.OnHurtBoxHit). The model in `heldModel` is
// shown on the off hand only while the guard stance is held.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class ShieldData : ItemData
{
	protected override EItemCategory ComputeCategory() => EItemCategory.Shield;

	// Weight while equipped (see ItemData.EquipWeight).
	[Export] public float weight = 0f;
	public override float EquipWeight => weight;

	[ExportGroup("Block")]
	// Recharging guard pool, live only while sneaking. 0 = the shield never soaks
	// (it can still parry). The guard refills fully over guardRechargeTime seconds
	// once guardRechargeDelay seconds have passed since the last hit; any damage
	// taken while guarding re-arms that delay even when the pool is already empty,
	// so a player under fire can't regenerate their guard.
	[Export] public float guardArmor = 0f;
	[Export] public float guardRechargeDelay = 1f;
	// Seconds from empty to full. 0 = never recharges.
	[Export] public float guardRechargeTime = 0.5f;
	// Noise a block makes at the blocker (Sim.CreateNoiseEvent; see
	// ItemEvent.impactDecibels for the scale). 0 = silent.
	[Export] public float blockDecibels = 0f;
	// Thorns: dealt back to the attacker whenever the guard soaks some of a blow,
	// scaled like parryCounter. Only a Mob landing a discrete hit is struck. Null =
	// blocking hurts nobody.
	[Export] public DamageData blockCounter;

	[ExportGroup("Parry")]
	// Milliseconds after the crouch begins during which a block PARRIES — fully
	// negating the blow and counter-striking the attacker. 0 = no parry. Sim clock.
	[Export] public int parryTimeMs = 0;
	// The largest single (post-resistance) hit a parry negates, at level 0. A
	// bigger blow falls through to the passive block. Scales with the shield's
	// level and the Melee forge upgrade (Player.EffectiveMaxParryDamage). A parry
	// needs the guard off its recharge delay and re-arms it, so it can't be spammed.
	// 0 = no parry.
	[Export] public float maxParryDamage = 0f;
	// Dealt back to the attacker on a parry, scaled like maxParryDamage. Only a
	// Mob landing a discrete hit is countered. Null = the blow is negated with no
	// counter-strike.
	[Export] public DamageData parryCounter;
	// One-shot Fx at the player on a parry (the clang + shake). Null = none.
	[Export] public PackedScene parryEffect;
	[Export] public float parryDecibels = 0f;

	public bool CanParry => parryTimeMs > 0 && maxParryDamage > 0f;
	public bool CanGuard => guardArmor > 0f || CanParry;

	public override ItemState CreateState()
	{
		return new ShieldState(this);
	}
}
