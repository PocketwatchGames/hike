using Godot;

public enum EWeaponEventType
{
	Melee,
	Hitscan,
	UseAmmo
}

[GlobalClass]
public partial class WeaponEvent : Resource
{
	[Export] public ushort time;
	[Export] public EWeaponEventType type;
	[Export] public float meleeRange = 1f;
	[Export] public float meleeRadius = 2f;
	[Export] public float hitScanRange = 20f;
}
