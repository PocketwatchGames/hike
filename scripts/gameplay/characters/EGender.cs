// Player body type. Selects which base character model renders; authored on
// PlayerSpawnData, resolved to the live visual in Player.Initialize.
//
// Wire values are stable — gender is serialized and used as a dictionary key
// on Player's per-gender model map, so renames are safe but reorders are not.
public enum EGender
{
    Female = 0,
    Male = 1,
}
