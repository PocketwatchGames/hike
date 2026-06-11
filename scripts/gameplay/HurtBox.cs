using System;
using Godot;

[GlobalClass]
public partial class HurtBox : Area3D
{
    // Predict the result of a hit without applying it. Called by attackers
    // before Hit() so the weapon can pick its impact effect locally — keeps
    // damage application as a one-way notification, which leaves room for
    // future networked play (the prediction runs on the client, the real
    // damage application stays authoritative on the server).
    public Func<HitInfo, EHitResult> GetHitType;

    // Predict which damage triggers (crit, backstab, ...) would fire on this
    // hit. Parallel to GetHitType — the attacker uses the returned flags to
    // layer per-tier impact overlays (ItemAction.impactCritEffect /
    // impactBackstabEffect) on top of the base impact fx. Unset on receivers
    // that don't surface triggers (props, the player); QueryHitTriggers
    // returns None in that case.
    public Func<HitInfo, EDamageTriggerFlags> GetHitTriggers;

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

    // Safe wrapper for senders. A receiver that wires no CanHit filter accepts
    // every hit (props, environmental damageables have no faction to defend).
    public bool CanBeHit(HitInfo hit)
    {
        return CanHit == null || CanHit(hit);
    }

    public EHitResult QueryHitType(HitInfo hit)
    {
        return GetHitType != null ? GetHitType(hit) : EHitResult.None;
    }

    public EDamageTriggerFlags QueryHitTriggers(HitInfo hit)
    {
        return GetHitTriggers != null ? GetHitTriggers(hit) : EDamageTriggerFlags.None;
    }

    public void Hit(HitInfo hit)
    {
        OnHit?.Invoke(hit);
    }
}
