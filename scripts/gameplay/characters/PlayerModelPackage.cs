using Godot;

// Root of a per-gender player model package scene (player_model_female.tscn /
// player_model_male.tscn). Each package bundles one base character rig with its
// animator, held-item hand socket, and pixel-snap — all wired inside the scene
// — so Player can instance exactly the gender that spawned and read its drivers
// off this root, instead of carrying every gender's subtree in player.tscn and
// hiding all but one. Adding a body type = author a new package scene + map it
// on Player's gender->package dictionary; no new fields or code branches.
[GlobalClass]
public partial class PlayerModelPackage : Node3D
{
    // The rig's animation / faceting driver. Player binds this as the live
    // _animator when this package is the spawned gender.
    [Export] public ModelAnimator animator;
    // The hand-socket held-item renderer for this rig. Null on a package without
    // the socket wired, in which case Player treats held items as disabled.
    [Export] public HeldItemVisual heldVisual;
}
