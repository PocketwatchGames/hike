using Godot;

[GlobalClass]
public partial class PlayerSpawnData : Resource
{
	[Export] public WeaponData meleeWeaponData;
	[Export] public WeaponData rangedWeaponData;
}
