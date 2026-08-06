// Which rule drives the ceiling cutaway, selected by the camera_clip_mode cvar.
//
// Exactly one runs: every mode writes the same camera_clip globals, so a second
// one would only mean "whichever pushed last this frame".
public enum EClipMode
{
    // No automatic cutaway. The manual R3 toggle still forces one — that is a
    // player-facing control, not part of the rule being switched off.
    Off = 0,
    // Upward raycast from the player; one clip height, world-wide.
    Scalar = 1,
    // Per-column band rule (ClipColumnMask).
    Column = 2,
    // Cell-region decomposition (ClipCellMask).
    Cell = 3,
    // Scalar base plane plus a probe-driven disc revealing a lower one
    // (ClipIris). Iris IS the scalar mode with a disc on top, so until that
    // lands this behaves exactly as Scalar rather than as a broken mode.
    Iris = 4,
}
