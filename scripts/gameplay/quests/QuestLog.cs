using System;
using System.Collections.Generic;
using System.IO;
using Godot;

// The run's active quests, owned by WorldSimState so they persist with the rest
// of player progression (SaveGame v4). GameClient ticks the log each frame and
// feeds it triggers (death -> Rescue, nightfall -> Return to Camp); the HUD
// subscribes to onQuestAdded / onQuestRemoved to surface widgets. Quests
// self-report terminal status in Tick and the log drops them the moment they do.
public class QuestLog
{
    readonly List<QuestState> _quests = new();
    public IReadOnlyList<QuestState> Quests => _quests;
    public int Count => _quests.Count;

    public event Action<QuestState> onQuestAdded;
    public event Action<QuestState> onQuestRemoved;

    public void Add(QuestState quest)
    {
        if (quest == null || _quests.Contains(quest))
        {
            return;
        }
        _quests.Add(quest);
        quest.OnStart();
        onQuestAdded?.Invoke(quest);
    }

    public void Remove(QuestState quest)
    {
        if (quest == null || !_quests.Remove(quest))
        {
            return;
        }
        quest.OnEnd();
        onQuestRemoved?.Invoke(quest);
    }

    // True if the log already holds a rescue quest for this member — avoids
    // stacking a duplicate "Rescue X!" if the death path fires twice.
    public bool HasRescueFor(PlayerState member)
    {
        if (member == null)
        {
            return false;
        }
        for (int i = 0; i < _quests.Count; i++)
        {
            if (_quests[i] is RescueQuest rq && rq.TargetMember == member)
            {
                return true;
            }
        }
        return false;
    }

    public bool HasQuestOfType<T>() where T : QuestState
    {
        for (int i = 0; i < _quests.Count; i++)
        {
            if (_quests[i] is T)
            {
                return true;
            }
        }
        return false;
    }

    // Poll every quest, then drop any that went terminal this tick. Iterating a
    // copy-free forward pass for Tick, then a reverse pass for removal so a
    // completion mid-list doesn't disturb the tick loop.
    public void Tick(ulong nowMs)
    {
        for (int i = 0; i < _quests.Count; i++)
        {
            _quests[i].Tick(nowMs);
        }
        for (int i = _quests.Count - 1; i >= 0; i--)
        {
            if (_quests[i].Status != EQuestStatus.Active)
            {
                Remove(_quests[i]);
            }
        }
    }

    // --- Persistence (SaveGame v4+) ---------------------------------------
    // (count, [pathString, statusByte, payloadLen, payloadBytes]*). The
    // QuestData resource path drives reconstruction via CreateRuntime; the
    // subclass payload is length-prefixed so a quest whose .tres went missing
    // between sessions is skipped cleanly without desyncing the stream.
    public void Serialize(BinaryWriter w)
    {
        w.Write(_quests.Count);
        for (int i = 0; i < _quests.Count; i++)
        {
            QuestState q = _quests[i];
            w.Write(q.Data?.ResourcePath ?? "");
            w.Write((byte)q.Status);

            using var payload = new MemoryStream();
            using (var pw = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                q.Serialize(pw);
            }
            byte[] bytes = payload.ToArray();
            w.Write(bytes.Length);
            w.Write(bytes);
        }
    }

    // Replaces the current set (dropping whatever new-game seeding added), so
    // load order relative to seeding doesn't matter.
    public void Deserialize(BinaryReader r)
    {
        for (int i = _quests.Count - 1; i >= 0; i--)
        {
            Remove(_quests[i]);
        }
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string path = r.ReadString();
            var status = (EQuestStatus)r.ReadByte();
            int payloadLen = r.ReadInt32();
            byte[] payload = r.ReadBytes(payloadLen);

            QuestData data = string.IsNullOrEmpty(path) ? null : GD.Load<QuestData>(path);
            QuestState quest = data?.CreateRuntime();
            if (quest == null)
            {
                GD.PushWarning($"QuestLog: could not restore quest '{path}', skipping.");
                continue;
            }
            using (var pms = new MemoryStream(payload))
            using (var pr = new BinaryReader(pms))
            {
                quest.Deserialize(pr);
            }
            quest.SetStatus(status);
            Add(quest);
        }
    }
}
