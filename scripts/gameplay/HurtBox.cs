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
    public Func<DamageData, EHitResult> GetHitType;

    // Apply damage to the receiver. One-way: receiver doesn't report back.
    public Action<DamageData, Node> OnHit;

    public EHitResult QueryHitType(DamageData damageData)
    {
        return GetHitType != null ? GetHitType(damageData) : EHitResult.None;
    }

    public void Hit(DamageData damageData, Node source)
    {
        OnHit?.Invoke(damageData, source);
    }
}
