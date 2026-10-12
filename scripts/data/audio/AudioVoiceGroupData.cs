using Godot;

// A shared voice budget for Fx audio. Every Fx scene whose root names the same
// group draws from one budget, so a burst across DIFFERENT scenes (a blast
// breaking a crate, a pot and a barrel in one frame) is limited as one problem.
// Counted per Fx event, not per player: an Fx with two layered players is one
// voice. Only the audio is refused — the event's particles still play.
[GlobalClass]
public partial class AudioVoiceGroupData : Resource
{
    // Events of this group allowed to sound at once.
    [Export(PropertyHint.Range, "1,32,1")] public int maxVoices = 4;

    // A new event starting this soon after the group's last accepted one is
    // silent, whatever the budget — collapses a same-frame burst into one sound.
    [Export(PropertyHint.Range, "0,500,1,suffix:ms")] public int retriggerWindowMs = 40;

    // What happens when the budget is full: silence the oldest voice so the new
    // one plays, or keep the playing voices and silence the new one.
    [Export] public EVoiceSteal steal = EVoiceSteal.StealOldest;
}

public enum EVoiceSteal
{
    StealOldest,
    RejectNew,
}
