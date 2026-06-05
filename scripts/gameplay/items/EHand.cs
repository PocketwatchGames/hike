// Which hand a wielded weapon's 3D model attaches to. Read by WeaponData and
// applied by HeldItemVisual, which binds a hand socket per side against the
// rig's L_/R_wrist_joint bones. Default is Right — most weapons (swords, the
// bow's draw hand) live there; a bow held in the off-hand uses Left.
public enum EHand
{
	Right,
	Left,
}
