using System.IO;

// "Kunkun Hunt (X/12)" — counts player-credited kills of a target species.
// Subscribes to GameClient.onMobKilled while active; the running count is the
// only state it serializes (the goal comes from the QuestData).
public class KillCountQuest : QuestState
{
    int _count;

    KillCountQuestData Config => Data as KillCountQuestData;

    public KillCountQuest(KillCountQuestData data) : base(data) { }

    public override void OnStart()
    {
        if (Sim.Current != null)
        {
            Sim.Current.onMobKilled += OnMobKilled;
        }
    }

    public override void OnEnd()
    {
        if (Sim.Current != null)
        {
            Sim.Current.onMobKilled -= OnMobKilled;
        }
    }

    void OnMobKilled(SpeciesData species, bool damagedByPlayer)
    {
        KillCountQuestData cfg = Config;
        if (!damagedByPlayer || cfg?.targetSpecies == null || species != cfg.targetSpecies)
        {
            return;
        }
        if (_count < cfg.targetCount)
        {
            _count++;
        }
    }

    public override void Tick(ulong nowMs)
    {
        if (_count >= (Config?.targetCount ?? 0))
        {
            Complete();
        }
    }

    protected override int Current => _count;
    protected override int Target => Config?.targetCount ?? 0;

    public override void Serialize(BinaryWriter w) => w.Write(_count);
    public override void Deserialize(BinaryReader r) => _count = r.ReadInt32();
}
