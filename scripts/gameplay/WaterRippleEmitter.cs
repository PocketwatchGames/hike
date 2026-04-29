using Godot;

// Distance-based water-ripple emitter for moving entities (Player, Mob).
// Each holder ticks this every physics frame; while the entity is in water
// and has moved more than `stride` meters since the last emission, a single
// radial ripple is pushed into SkyController's ripple ring buffer.
//
// Class (not struct) so mutations to _lastEmitXZ / _hasLastEmit reliably
// persist across calls — a struct field on a Godot Node behaves correctly
// for direct-field calls but the class form rules out any boxing edge case
// from mismatched call sites or future refactors.
//
// Why distance-based rather than time-based: it auto-scales emission rate
// with movement speed without tracking velocity. A slow walking actor emits
// fewer ripples than a sprinting one, naturally; a stationary actor emits
// none. Holders only need a position and an "in water" flag.
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
