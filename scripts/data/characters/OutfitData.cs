using Godot;

// One named outfit on the shared polysplit player rig: the MeshInstance3D parts
// to show for each body type. Registered under a key in PlayerData.outfits;
// ArmorData.outfit (worn armor) refers to entries by key, so rig mesh names are
// authored once here rather than on every item.
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

	// Head parts (hood / helm / hat). On the player they replace the styled
	// hair; an NPC wears them over its hair (pick a short style, like the "b"
	// cuts). Empty = the outfit leaves the head bare.
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
