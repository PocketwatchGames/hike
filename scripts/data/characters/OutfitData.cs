using Godot;

// One named outfit on the shared polysplit player rig: the MeshInstance3D parts
// to show for each body type. Registered under a key in PlayerData.outfits;
// PlayerState.outfit (the class look) and ArmorData.outfit (worn armor) refer
// to entries by key, so rig mesh names are authored once here rather than on
// every character and item.
//
// Split by gender because the two rigs prefix their parts differently (Female
// F_, Male M_) and the outfits don't map by a simple prefix swap (the Male Mage
// has no cape, the Male Knight no skirt). An empty set for a gender leaves that
// rig on its bare-body / bare-head fallback.
[GlobalClass]
public partial class OutfitData : Resource
{
	// Torso/legs parts, shown in place of the bare-body default.
	[Export] public string[] bodyMeshNamesFemale = System.Array.Empty<string>();
	[Export] public string[] bodyMeshNamesMale = System.Array.Empty<string>();

	// Head parts (hood / helm), shown in place of the hair-style fallback.
	// Empty = the outfit leaves the head bare and the styled hair shows.
	[Export] public string[] headMeshNamesFemale = System.Array.Empty<string>();
	[Export] public string[] headMeshNamesMale = System.Array.Empty<string>();

	public string[] GetBodyMeshNames(EGender gender)
	{
		return gender == EGender.Male ? bodyMeshNamesMale : bodyMeshNamesFemale;
	}

	public string[] GetHeadMeshNames(EGender gender)
	{
		return gender == EGender.Male ? headMeshNamesMale : headMeshNamesFemale;
	}
}
