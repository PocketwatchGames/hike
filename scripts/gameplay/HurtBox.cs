using System;
using Godot;

[GlobalClass]
public partial class HurtBox : Area3D
{
    // Predict the result of a hit without applying it — the resolved EHitResult
    // plus the crit/backstab flags that would fold, in one pass. Called by
    // attackers before Hit() so the weapon can pick its base impact effect and
    // its per-tier crit/backstab overlays (ItemAction.impactCritEffect /
    // impactBackstabEffect) locally. Keeps damage application a one-way
    // notification, which leaves room for future networked play (the prediction
    // runs on the client, the real damage application stays authoritative on the
    // server). Unset on receivers that don't resolve hits — QueryHit returns
    // HitPrediction.None in that case.
    public Func<HitInfo, HitPrediction> PredictHit;

    // Apply damage to the receiver. One-way: receiver doesn't report back.
    public Action<HitInfo> OnHit;

    // Receiver-supplied hit filter. Given the incoming HitInfo, returns whether
    // this receiver accepts the hit. Lets each receiver own its own rule
    // against the hit's carried context — team allegiance today (a Mob answers
    // from its effective team, the player from the Player team), but also
    // stealth, damage tags, or anything else the HitInfo grows — instead of the
    // attacker walking the tree to guess who owns the box. Unset on ownerless
    // damageables (props, environmental hurtboxes) — see CanBeHit.
    public Func<HitInfo, bool> CanHit;

    // The shape giving this hurtbox its volume, resolved from the first
    // CollisionShape3D child. Owners place the Area3D at their own root and
    // offset the shape upward, so the node itself carries no useful position.
    public CollisionShape3D Shape { get; private set; }

    // World point that stands for this hurtbox in spatial queries. NOT the node
    // origin: that sits at the owner's feet, exactly on the terrain surface it
    // stands on, so a ray aimed there terminates on the ground itself.
    public Vector3 Center => Shape != null ? Shape.GlobalPosition : GlobalPosition;

    public override void _Ready()
    {
        foreach (Node child in GetChildren())
        {
            if (child is CollisionShape3D shape)
            {
                Shape = shape;
                break;
            }
        }
    }

    // Safe wrapper for senders. A receiver that wires no CanHit filter accepts
    // every hit (props, environmental damageables have no faction to defend).
    public bool CanBeHit(HitInfo hit)
    {
        return CanHit == null || CanHit(hit);
    }

    public HitPrediction QueryHit(HitInfo hit)
    {
        return PredictHit != null ? PredictHit(hit) : HitPrediction.None;
    }

    public void Hit(HitInfo hit)
    {
        OnHit?.Invoke(hit);
    }
}
