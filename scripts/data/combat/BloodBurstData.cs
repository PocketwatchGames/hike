using Godot;

// One severity of a BloodData: what a body throws off when struck that hard.
// Scenes are SHAPES — they author white-ish albedo and are tinted by the
// owning BloodData.color at spawn, so one splatter serves every substance.
//
// [Tool]: reachable from MobData (a [Tool] class) through BloodData — see the
// [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class BloodBurstData : Resource
{
	// Directional spray. Spawned at the hit point with its local -Z turned
	// along the hit direction, so the scene authors its throw toward -Z.
	[Export] public PackedScene splatter;
	// Lingering cloud at the hit point, unrotated.
	[Export] public PackedScene mist;

	[ExportGroup("Stains")]
	// Stains laid on the ground under the hit, scattered along the hit
	// direction as if the splatter landed there.
	[Export(PropertyHint.Range, "0,16,1")] public int stainCount = 1;
	// Square stain size range, meters.
	[Export(PropertyHint.Range, "0.05,4,0.05")] public float stainSizeMin = 0.4f;
	[Export(PropertyHint.Range, "0.05,4,0.05")] public float stainSizeMax = 0.8f;
	// Farthest a stain lands from below the hit point, along the hit direction.
	[Export(PropertyHint.Range, "0,6,0.1")] public float stainThrow = 0.8f;
	// Full strength for this long, then a linear fade to nothing.
	[Export(PropertyHint.Range, "0,600,1")] public float stainHoldSeconds = 20f;
	[Export(PropertyHint.Range, "0.1,600,0.1")] public float stainFadeSeconds = 15f;
}
