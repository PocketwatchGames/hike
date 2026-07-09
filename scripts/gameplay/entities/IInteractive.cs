using Godot;
using Godot.Collections;

public interface IInteractive
{
    Vector3 hudPosition { get; }
    bool CanInteract();
    bool CanActorInteract(Player player);

    // Power tier shown as star pips on the interact HUD, mirroring the mob HUD's
    // level fan. 0 (default) = no pips; the forge overrides it with its level
    // (1-5). Purely presentational.
    int InteractLevel => 0;

    // Whether the discovery X-ray silhouette should currently render (driven by
    // InteractiveXray's LOS probe). Defaults to CanInteract() — an interactive
    // the player can no longer act on isn't worth highlighting through walls, so
    // an opened loot chest / picked-up loot stops X-raying for free. Override
    // when "should highlight" diverges from "can interact": a stash stays
    // interactable forever (reopenable) but should stop broadcasting once the
    // player has found and opened it.
    bool ShouldShowXray() => CanInteract();

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
