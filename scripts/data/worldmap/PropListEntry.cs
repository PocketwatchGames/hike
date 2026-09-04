using Godot;

// Which of a painted region's two storeys a prop fills.
//
// The fill lays CANOPY down first and everywhere, then fills UNDERSTORY beneath
// and between it, and the two reserve room from their own kind only — a bush
// does not push a tree away and a tree does not push a bush away. So this is
// the single most consequential thing a prop list says about a scene.
//
// Appended to, never reordered: Godot writes an enum by its number and strips a
// value equal to the default, so inserting a member silently re-labels every
// .tres already authored.
public enum EPropTier
{
    // Decide from the measured drawn height, against PropListData's
    // canopyHeightMeters. Right for almost everything: nothing low is a tree
    // and nothing tall is undergrowth.
    Auto = 0,
    Canopy = 1,
    Understory = 2,
    // Both storeys, as separate passes. For a tree whose branches reach the
    // ground and so reads as undergrowth as well as canopy — a pine.
    Both = 3,
}

// A prop list's row: a scene, its weight, and which storey it fills.
//
// A row rather than the scene itself, because filling both storeys is a
// statement about THIS list. The same pine is canopy-and-understory in a pine
// stand and plain canopy in a mixed wood, and neither is a property of the
// .tscn.
[GlobalClass]
public partial class PropListEntry : WeightedScene
{
    [Export] public EPropTier tier = EPropTier.Auto;
}
