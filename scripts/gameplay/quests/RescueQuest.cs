using System.IO;

// "Rescue XXX!" — tracks a fallen party member. Completes when the member is
// revived (PlayerState.IsDead flips false), fails when their body is destroyed
// past its revive deadline (the member drops off the party roster). Keyed on the
// sim-side PlayerState, so it needs nothing from the client: it polls the member
// each tick, which also makes it robust to save/load ordering.
public class RescueQuest : QuestState
{
    // The fallen member's display name — used both for the objective text and to
    // re-resolve the member after a load (members carry no stable numeric id, but
    // names are unique within the small party).
    string _name = "";
    // Cached member, re-resolved by name from the roster when null (after a load)
    // so the party-build vs. save-restore order doesn't matter.
    PlayerState _member;

    public RescueQuest(RescueQuestData data) : base(data) { }

    // Spawn path: bind the freshly-fallen member.
    public void SetTarget(PlayerState member)
    {
        _member = member;
        _name = member?.characterName ?? "";
    }

    public PlayerState TargetMember => Resolve();

    static Party Party => Sim.Current?.WorldState?.SimState?.Party;

    PlayerState Resolve()
    {
        Party party = Party;
        if (party == null)
        {
            return null;
        }
        // Re-scan the roster each resolve: confirms the cached member is still on
        // it (a destroyed member is RemoveAt'd) and rebinds by name after a load.
        PlayerState found = null;
        for (int i = 0; i < party.Members.Count; i++)
        {
            PlayerState m = party.Members[i];
            if (m == null)
            {
                continue;
            }
            if (m == _member || m.characterName == _name)
            {
                found = m;
                break;
            }
        }
        _member = found;
        return _member;
    }

    public override void Tick(ulong nowMs)
    {
        PlayerState member = Resolve();
        // Gone from the roster — the body was destroyed past its revive deadline.
        if (member == null)
        {
            Fail();
            return;
        }
        // Revived.
        if (!member.IsDead)
        {
            Complete();
        }
    }

    protected override string GetTitle() =>
        Data != null ? Loc.Format(Data.textKey, _name) : _name;

    public override void Serialize(BinaryWriter w) => w.Write(_name);

    public override void Deserialize(BinaryReader r)
    {
        _name = r.ReadString();
        _member = null; // resolved lazily on first Tick
    }
}
