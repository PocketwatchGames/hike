using Godot;
using Godot.Collections;

[GlobalClass]
public partial class WeaponData : Resource
{
	[Export] public float cooldownTime = 0.5f;
	[Export] public float activeTime = 0.5f;
	[Export] public bool activateOnRelease = false;
	[Export] public bool useAmmo = false;
	[Export] public DamageData damageData;
	[Export] public Array<WeaponEvent> events = new();
}
