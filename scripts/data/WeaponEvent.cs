using Godot;

[GlobalClass]
public partial class WeaponEvent : Resource
{
	[Export] public ushort time;
	[Export] public float meleeRange = 1f;
	[Export] public float meleeRadius = 2f;
}
