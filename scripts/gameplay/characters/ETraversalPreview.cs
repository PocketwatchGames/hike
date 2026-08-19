// Which traversal a Dash press would perform from where the player stands right
// now — the ClimbHUD's whole state. One direction or nothing: mantling up, a
// wall attach and a top-out all read as Up, a hop down and backing over a lip
// all read as Down, and the two are never offered together.
//
// Runtime-only (Player.TraversalPreview), never serialized.
public enum ETraversalPreview
{
	None,
	Up,
	Down,
}
