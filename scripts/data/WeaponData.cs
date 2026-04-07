using Godot;

[GlobalClass]
public partial class WeaponData : Resource
{
	[Export] public float meleeRadius = 2f;
	[Export] public float meleeRange = 1f;
	[Export] public float cooldownTime = 0.5f;
	[Export] public float activeTime = 0.5f;
	[Export] public bool activateOnRelease = false;
}
