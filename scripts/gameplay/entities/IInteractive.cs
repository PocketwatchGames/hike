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

    // The lock barring every action not marked InteractiveAction.bypassesLock,
    // or null. ActionRunner gates those actions on a key and, when one
    // completes, spends the key and calls Unlock — before the action's
    // completion events, so Complete already sees the lock gone.
    LockData Lock => null;
    void Unlock() { }

    // The one "can this player use it right now" question every interact site
    // asks: the placement's disabled gate, the interactive's own answer, then
    // whether the actor can see it. Ask this, never CanActorInteract directly,
    // or a disabled or unseen entity is usable from that site.
    static bool CanUse(IInteractive interactive, Player actor)
    {
        return interactive != null && !IsDisabled(interactive) && interactive.CanActorInteract(actor)
            && CanSee(interactive, actor);
    }

    // Whether there is light enough at the interactive for `actor` to see it.
    // Last in CanUse because it costs a sun-shadow raycast. Only world entities
    // are gated — the player's own traversal and self-actions aren't Node3Ds.
    static bool CanSee(IInteractive interactive, Player actor)
    {
        Sim sim = Sim.Current;
        if (interactive is not Node3D node || actor == null || sim?.SimData == null)
        {
            return true;
        }
        Vector3 samplePos = node.GlobalPosition + Vector3.Up * sim.SimData.interactLightSampleHeight;
        // An interactive can occupy a light-blocking voxel (a closed door stamps
        // Barrier into its column; a lever sits in a wall), which always reads
        // dark. Read the face the actor looks at instead: one voxel toward them.
        if (!LightEngine.IsOpenVoxel(sim.WorldState, Mathf.FloorToInt(samplePos.X), Mathf.FloorToInt(samplePos.Y), Mathf.FloorToInt(samplePos.Z)))
        {
            Vector3 toActor = actor.GlobalPosition - samplePos;
            samplePos += Mathf.Abs(toActor.X) >= Mathf.Abs(toActor.Z)
                ? new Vector3(Mathf.Sign(toActor.X), 0f, 0f)
                : new Vector3(0f, 0f, Mathf.Sign(toActor.Z));
        }
        return PlayerPerception.ComposeLightFactor(sim, actor, samplePos) >= sim.SimData.interactMinLightFactor;
    }

    static bool IsDisabled(IInteractive interactive)
    {
        return interactive is Node3D node && (Sim.Current?.IsEntityDisabled(node) ?? false);
    }
}
