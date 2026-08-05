using Godot;

// Implemented by an entity whose collider deliberately oversails the structure it
// belongs to — a roof's eave and rake overhangs hang out over open ground.
// GameCamera's upward cutaway probe asks before treating a hit as a ceiling, so
// walking under the eaves of a house doesn't cut its roof away while the player
// is still outside the building.
//
// This is a CUTAWAY-only distinction. The collider is untouched: an overhang
// still blocks movement, sight and projectiles like any other building surface.
public interface IClipCover
{
    // Does this cover read as a ceiling at worldPos — is the point under
    // enclosed space, rather than under an overhang?
    bool IsCeilingAt(Vector3 worldPos);
}
