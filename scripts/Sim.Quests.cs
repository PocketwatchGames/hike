using Godot.Collections;

// Sim-side driver for the quest system. Quests are simulation state (they live
// on SimState.QuestLog and persist with the save); Sim owns their whole
// lifecycle — seeding, trigger-spawning, and per-tick evaluation — so the client
// only ever reads the log to render widgets. See scripts/gameplay/quests/.
public partial class Sim
{
    // Starting quests are seeded once, the first tick the party exists (it's
    // built after Sim.Initialize). A save-load fills the log from disk instead,
    // so seeding is skipped when the log already has entries.
    bool _questsSeeded;

    void TickQuests()
    {
        SimState sim = _worldState?.SimState;
        if (sim == null)
        {
            return;
        }
        QuestLog log = sim.QuestLog;

        if (!_questsSeeded && sim.Party != null)
        {
            _questsSeeded = true;
            if (log.Count == 0)
            {
                SeedStartingQuests(log);
            }
        }

        SpawnRescueQuests(sim, log);
        log.Tick(_worldState.GameTimeMs);
    }

    void SeedStartingQuests(QuestLog log)
    {
        Array<QuestData> starting = _worldState?.ScriptData?.startingQuests;
        if (starting == null)
        {
            return;
        }
        foreach (QuestData data in starting)
        {
            if (data != null)
            {
                log.Add(data.CreateRuntime());
            }
        }
    }

    // A fallen member with no active rescue quest gets one — polled from the sim
    // roster (PlayerState.IsDead) rather than a client death event.
    void SpawnRescueQuests(SimState sim, QuestLog log)
    {
        QuestData rescueData = _worldState?.ScriptData?.rescueQuest;
        Party party = sim.Party;
        if (rescueData == null || party == null)
        {
            return;
        }
        for (int i = 0; i < party.Members.Count; i++)
        {
            PlayerState m = party.Members[i];
            if (m != null && m.IsDead && !log.HasRescueFor(m) && rescueData.CreateRuntime() is RescueQuest quest)
            {
                quest.SetTarget(m);
                log.Add(quest);
            }
        }
    }

    // Added on the dusk edge (subscribed in Initialize); one per night at most.
    void AddReturnToCampQuest()
    {
        QuestLog log = _worldState?.SimState?.QuestLog;
        QuestData data = _worldState?.ScriptData?.returnToCampQuest;
        if (log == null || data == null || log.HasQuestOfType<ReturnToCampQuest>())
        {
            return;
        }
        log.Add(data.CreateRuntime());
    }
}
