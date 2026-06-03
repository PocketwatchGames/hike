// Player body type. Selects which base character model renders — each gender
// maps to its own model subtree (instanced FBX + ModelAnimator) under the
// player scene, all sharing the same skeleton and animation library so the
// EAnimation state machine drives every variant identically. Authored on
// PlayerSpawnData; resolved to the live visual in Player.Initialize.
//
// Wire values are stable — gender is serialized and used as a dictionary key
// on Player's per-gender model map, so renames are safe but reorders are not.
public enum EGender
{
    Female = 0,
    Male = 1,
}
