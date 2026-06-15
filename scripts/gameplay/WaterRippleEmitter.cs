using Godot;

// Distance-based water-ripple emitter for moving entities (Player, Mob).
// Each holder ticks this every physics frame; while the entity is in water
// and has moved more than `stride` meters since the last emission, a single
// radial ripple is pushed into SkyController's ripple ring buffer. Distance-
// based (not time-based) so emission rate auto-scales with movement speed
// without tracking velocity.
public class WaterRippleEmitter
{
    private Vector2 _lastEmitXZ;
    private bool _hasLastEmit;

    public void Update(Vector3 worldPos, bool inWater, float strength, float stride)
    {
        if (!inWater)
        {
            _hasLastEmit = false;
            return;
        }
        Vector2 xz = new Vector2(worldPos.X, worldPos.Z);
        if (!_hasLastEmit)
        {
            SkyController.Current?.EmitWaterRipple(xz, strength);
            _lastEmitXZ = xz;
            _hasLastEmit = true;
            return;
        }
        if (xz.DistanceSquaredTo(_lastEmitXZ) >= stride * stride)
        {
            SkyController.Current?.EmitWaterRipple(xz, strength);
            _lastEmitXZ = xz;
        }
    }
}
