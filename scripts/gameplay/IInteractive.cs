using Godot;
using Godot.Collections;

public interface IInteractive
{
    Vector3 hudPosition { get; }
    bool CanInteract();
    bool CanActorInteract(Player player);
    void Complete();
    ulong GetInteractTime(Player player);

    // Action-runner-driven interactivity. When non-null, the Player's
    // interact press starts the action keyed by DefaultVerb in this
    // dictionary, routed through ActionRunner. The interactive's timeline
    // events fire as authored, including any OpenInteractive event that
    // calls back into Complete().
    //
    // Returning null falls back to the legacy GetInteractTime / Complete
    // path so existing interactives (chest, door, torch) keep working
    // unchanged until they opt in.
    Dictionary<EActionVerb, ItemActionProfile> GetActions(Player player) => null;
    EActionVerb DefaultVerb => EActionVerb.Open;
}
