using System;
using Godot;

[GlobalClass]
public partial class HurtBox : Area3D
{
    public Action<DamageData, Node> OnHit;

    public void Hit(DamageData damageData, Node source)
    {
        OnHit?.Invoke(damageData, source);
    }
}
