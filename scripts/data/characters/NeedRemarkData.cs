using Godot;

// A line the controlled member says aloud once `need` rises past `atStages`
// (Player.Needs' unbounded deprivation, 1 = the stamina phase, 2 = the
// max-health phase). Shown by NeedRemarks in a bubble that dismisses itself.
[GlobalClass]
public partial class NeedRemarkData : Resource
{
	[Export] public ENeed need;
	[Export(PropertyHint.Range, "0,4,0.05,or_greater")] public float atStages = 1f;
	// hike_strings.tsv key of the line.
	[Export] public StringName textLocKey;
}
