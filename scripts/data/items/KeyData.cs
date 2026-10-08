using Godot;

// An item that opens locks (LockData). Stacks like any material; one is spent
// per lock opened.
[GlobalClass]
public partial class KeyData : ItemData
{
    [Export] public Godot.Collections.Array<LockData> opens = new();
    // A skeleton key: opens every lock, listed or not. A key made for the lock
    // is always spent before a skeleton key.
    [Export] public bool opensAnyLock;

    public bool Opens(LockData lockData)
    {
        return lockData != null && (opensAnyLock || opens.Contains(lockData));
    }
}
