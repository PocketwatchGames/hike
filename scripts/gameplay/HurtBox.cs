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

    // Apply damage to the receiver. One-way: receiver doesn't report back.
    public Action<HitInfo> OnHit;

    public EHitResult QueryHitType(HitInfo hit)
    {
        return GetHitType != null ? GetHitType(hit) : EHitResult.None;
    }

    public void Hit(HitInfo hit)
    {
        OnHit?.Invoke(hit);
    }
}
