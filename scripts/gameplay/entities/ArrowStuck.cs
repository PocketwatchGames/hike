using Godot;

// A round stuck in a living mob: visual only, parented to the mob so it follows
// it, with no collision. When the mob dies it drops as a loose pickup of the
// round (DropAsLoot); a mob that unloads alive takes it with it.
[GlobalClass]
public partial class ArrowStuck : Node3D
{
    private AmmoData _data;

    public static ArrowStuck Create(Mob mob, AmmoData data, Vector3 worldHitPos, Vector3 hitDirection)
    {
        if (mob == null || data == null)
        {
            return null;
        }
        var stuck = new ArrowStuck();
        stuck._data = data;
        mob.AddChild(stuck);
        stuck.GlobalPosition = worldHitPos;

        // Embedded arrows use the same 3D model the in-flight Projectile fired
        // (arrow data's stuckModel → scenes/projectiles/arrow_model.tscn), so
        // the arrow stuck in the mob reads as the object that was shot rather
        // than the flat worldSprite billboard. Loose ground arrows keep the
        // sprite (Loot), so only this embedded case carries the model. Orient
        // it along the shot's travel direction the same way Projectile.Launch
        // aims the in-flight visual (LookAt points local -Z down the flight).
        if (data.stuckModel != null)
        {
            Node3D model = data.stuckModel.Instantiate<Node3D>();
            stuck.AddChild(model);
            if (hitDirection.LengthSquared() > 1e-6f)
            {
                Vector3 fwd = hitDirection.Normalized();
                Vector3 up = Mathf.Abs(fwd.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
                stuck.LookAt(stuck.GlobalPosition + fwd, up);
            }
            return stuck;
        }

        // Fallback when no model is authored: programmatic Sprite3D from the
        // arrow data's worldSprite. Unshaded billboard, no orientation — the
        // stuck arrow is a small detail and reads fine flat.
        Texture2D texture = data.worldSprite ?? data.inventorySprite;
        if (texture != null)
        {
            var sprite = new Sprite3D();
            sprite.Texture = texture;
            sprite.PixelSize = 0.0738f;
            sprite.Centered = true;
            sprite.AlphaCut = SpriteBase3D.AlphaCutMode.OpaquePrepass;
            sprite.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
            stuck.AddChild(sprite);
        }
        return stuck;
    }

    // The mob died: the round drops loose where it was stuck.
    public void DropAsLoot()
    {
        if (_data != null && Sim.Current != null)
        {
            Sim.Current.EjectFiredRound(_data, GlobalPosition);
        }
        _data = null;
        QueueFree();
    }
}
