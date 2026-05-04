using Godot;

// Runtime payload passed to HurtBox.Hit. Senders (weapons, traps, damage
// zones, anything else that deals damage) build this from a DamageData
// template plus runtime context (attacker, hit direction). Receivers read
// the fields they care about — health damage routes through armor, status
// effects are appended to the receiver's _statusEffects, etc.
public struct HitInfo
{
    public Node source;
    public float healthDamage;
    public float stun;
    public Godot.Collections.Array<StatusEffectData> statusEffects;
    public Vector3 hitDirection;

    public HitInfo(DamageData template, Node source, Vector3 hitDirection = default)
    {
        this.source = source;
        this.hitDirection = hitDirection;
        if (template != null)
        {
            healthDamage = template.healthDamage;
            stun = template.stun;
            statusEffects = template.statusEffects;
        }
        else
        {
            healthDamage = 0f;
            stun = 0f;
            statusEffects = null;
        }
    }
}
