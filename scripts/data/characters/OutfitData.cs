using Godot;

// What a wearer shows on the shared polysplit human rig: the MeshInstance3D
// parts per body type, and the dye they are worn in. Always INLINE in its owner
// — an armour piece, PlayerData's unarmored look, an NPC appearance — so one
// file holds the whole look; copy the sub-resource in the inspector to reuse one.
//
// Split by gender because the two rigs prefix their parts differently (Female
// F_, Male M_) and the outfits don't map by a simple prefix swap (the Male Mage
// has no cape, the Male Knight no skirt). Armour needs both, since the player
// picks the body; an NPC appearance fills only its own rig's half. An empty set
// for a gender leaves that rig on its bare-body / bare-head fallback.
//
// [Tool]: reachable from ArmorData — see the [Tool]-parent rule in the root
// CLAUDE.md.
[Tool]
[GlobalClass]
public partial class OutfitData : Resource
{
	// Torso/legs parts, shown in place of the bare-body default.
	[Export] public string[] bodyMeshNamesFemale = System.Array.Empty<string>();
	[Export] public string[] bodyMeshNamesMale = System.Array.Empty<string>();

	// Head parts (hood / helm / hat), shown in place of the styled hair. Empty =
	// the outfit leaves the head bare.
	[Export] public string[] headMeshNamesFemale = System.Array.Empty<string>();
	[Export] public string[] headMeshNamesMale = System.Array.Empty<string>();

	// The pack's bodyColor1 precolour atlas, as a palette: what a clothing
	// surface shows when nothing has coloured it.
	public static readonly Color DefaultPrimary = new(0.820f, 0.592f, 0.365f);
	public static readonly Color DefaultSecondary = new(0.302f, 0.106f, 0f);
	public static readonly Color DefaultTertiary = new(0.796f, 0.796f, 0.678f);

	// Clothing palette, weighting the R / G / B regions of the rig's genericRGB
	// mask (model_lit_outfit).
	[Export] public Color primaryColor = DefaultPrimary;
	[Export] public Color secondaryColor = DefaultSecondary;
	[Export] public Color tertiaryColor = DefaultTertiary;

	public string[] GetBodyMeshNames(EGender gender)
	{
		return gender == EGender.Male ? bodyMeshNamesMale : bodyMeshNamesFemale;
	}

	public string[] GetHeadMeshNames(EGender gender)
	{
		return gender == EGender.Male ? headMeshNamesMale : headMeshNamesFemale;
	}
}
