using Godot;

// A kind of lock. The resource itself is the identity: a KeyData opens the
// locks it lists, and a locked interactive (IInteractive.Lock) holds one. While
// it is held, the interactive's lock-gated actions refuse to start unless the
// actor carries a key that opens it, and one such key is spent when the action
// completes — which removes the lock for good.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class LockData : Resource
{
    // Event-log line (loc key) shown when a press is refused for want of a key.
    [Export] public StringName lockedMessage = "";
}
