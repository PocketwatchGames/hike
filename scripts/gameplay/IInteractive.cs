using Godot;
using Godot.Collections;

public interface IInteractive
{
    Vector3 hudPosition { get; }
    bool CanInteract();
    bool CanActorInteract(Player player);
    // Resolution callback for the action at `actionIndex` in GetActions().
    // Implementers look the action up themselves and branch on `action.verb`
    // when behavior differs by verb (Open swings a door open while Lockpick
    // reveals the lock state, Break smashes the chest, etc.).
    void Complete(int actionIndex);

    // Action-runner-driven interactivity. The Player's interact press starts
    // the action at the player's current index (default 0 — author the
    // primary action first), routed through ActionRunner. Each action
    // self-describes its verb and display label; future radial UI iterates
    // this list to populate the hold-menu.
    Array<InteractiveAction> GetActions(Player player);
}
