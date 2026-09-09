// Which traversal a press would perform from where the player stands right now —
// the ClimbHUD's whole state. One direction or nothing: a wall attach and a
// top-out read as Up, backing over a lip and a step-off read as Down, and the two
// are never offered together. Mantling is NOT here — a ledge is an interact
// target and carries the ordinary interact prompt.
//
// Runtime-only (Player.TraversalPreview), never serialized.
public enum ETraversalPreview
{
	None,
	Up,
	Down,
}
