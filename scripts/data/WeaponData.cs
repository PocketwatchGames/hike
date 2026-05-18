using Godot;

[GlobalClass]
public partial class WeaponData : ItemData
{
	[Export] public bool useAmmo = false;
	[Export] public int maxAmmo = 0;
	// Optional drop spawned at every Hitscan impact point. When wired, each
	// shot leaves a recoverable arrow in the world that returns 1 ammo when
	// removed (player pickup or LootData.removeTimeMs timeout). Null = no
	// drop, ammo decrements permanently.
	[Export] public ArrowLootData arrowLootData;
	[Export] public DamageData damageData;

	// Authored timeline + tier list. Replaces the old cooldownTime / activeTime
	// / activateOnRelease / events fields. A tap-fire weapon has a single tier
	// with chargeTime=0 and autoActivateAtMax=true; a charge-and-release bow
	// has a single tier with chargeTime=0 and autoActivateAtMax=false; phase 3
	// adds multi-tier (Light/Heavy) authoring.
	[Export] public ItemActionProfile actionProfile;

	[Export] public override int maxLevel { get; set; } = 5;

	public override ItemState CreateState()
	{
		return new WeaponState(this);
	}
}
