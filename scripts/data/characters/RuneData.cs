using Godot;

// One of the seven secret runes (SimData.runes). A species' sequence of them
// (SpeciesData.runes) is what an inscription on a weapon or armor is matched
// against — see RuneInscription.
// [Tool]: reachable from SpeciesData, which is in the item closure — see the
// [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class RuneData : Resource
{
    [Export] public Texture2D icon;
}
