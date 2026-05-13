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
    // Optional crit override. Receivers that implement crit-eligible states
    // (e.g., stunned mobs) swap this in for the entire damage payload before
    // applying. Ignored by receivers that don't.
    public DamageData critDamage;

    public HitInfo(DamageData template, Node source, Vector3 hitDirection = default, DamageData critDamage = null)
    {
        this.source = source;
        this.hitDirection = hitDirection;
        this.critDamage = critDamage;
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
