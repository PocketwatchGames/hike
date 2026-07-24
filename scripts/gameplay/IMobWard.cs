// An interactive that repels certain mobs (a lit campfire scaring slimes). The
// interactive danger gate (NoDangerRequirement -> Sim.IsDangerNear) excludes
// warded mobs, so a mob that's afraid of the thing never blocks the player from
// using it — the player can light / camp at a fire surrounded by fire-fearing
// mobs, and lighting it then drives them off via the safety zone.
public interface IMobWard
{
    // True when this interactive currently wards off `mob` (e.g. a lit campfire
    // and a mob whose MobData.fearsCampfire is set). An interactive whose ward is
    // conditional — only while lit — returns false when the condition is off.
    bool WardsOff(Mob mob);
}
