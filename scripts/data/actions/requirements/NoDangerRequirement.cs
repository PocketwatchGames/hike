using System;
using Godot;

// Refuses a "safe-only" interactive action (cooking / sleeping at a campfire)
// while a dangerous hostile makes the spot unsafe. Anchored at the interactive
// being used (falling back to the actor) and scoped by `dangerRadius`: a mob
// blocks only if it's within that radius AND either in an engaged posture or has
// a clear line of sight to the object (see Sim.IsDangerNear). Fog/lighting-
// independent and symmetric about the object.
//
// If the interactive implements IMobWard (a lit campfire), mobs it wards off
// (fire-fearing slimes) are ignored — so lighting/camping isn't blocked by the
// very mobs the fire scares away.
[GlobalClass]
public partial class NoDangerRequirement : ActionRequirement
{
    // Horizontal meters from the interactive within which a merely-present
    // hostile (one that can see the spot but isn't engaged) is considered —
    // roughly on-screen range so a clearly-visible mob blocks. Does NOT bound an
    // actively hunting mob (Mob.IsEngaging counts at any range). Per-interactive
    // so different stations can differ by feel.
    [Export(PropertyHint.Range, "0,60,0.5")] public float dangerRadius = 25f;

    public override bool Evaluate(IActionActor actor, in ActionContext context)
    {
        Sim sim = Sim.Current;
        if (sim == null)
        {
            // Sim not loaded — fail closed so the action can't fire from a
            // half-initialised state, matching the other requirement subclasses.
            return false;
        }
        IInteractive interactive = context.primaryInteractive;
        Vector3 anchor = (interactive as Node3D)?.GlobalPosition ?? actor.ActorWorldPosition;
        Func<Mob, bool> warded = interactive is IMobWard ward ? ward.WardsOff : null;
        // Exclude the interactive's own colliders from the line-of-sight ray so a
        // mob's clear view of the object isn't blocked by the object itself (it's a
        // physics body sitting right at the anchor).
        Godot.Collections.Array<Rid> losExclude = null;
        if (interactive is Node3D node)
        {
            losExclude = new Godot.Collections.Array<Rid>();
            CollectBodyRids(node, losExclude);
        }
        return !sim.IsDangerNear(anchor, dangerRadius, warded, losExclude);
    }

    // Depth-first collect the RIDs of every CollisionObject3D at or under `node`
    // (the interactive's StaticBody, interact/zone areas, etc.).
    private static void CollectBodyRids(Node node, Godot.Collections.Array<Rid> into)
    {
        if (node is CollisionObject3D collider)
        {
            into.Add(collider.GetRid());
        }
        foreach (Node child in node.GetChildren())
        {
            CollectBodyRids(child, into);
        }
    }
}
