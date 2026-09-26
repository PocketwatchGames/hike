using Godot;

[GlobalClass]
public partial class PropInstance : Node3D, IWorldEntity
{
    // Porousness is owned per-collider by node type: a prop's movement collider
    // is a PorousBody (blocks movement / grounded sight, lets smell, sound,
    // perched vision, and flight pass through), while a genuinely solid prop
    // would use a plain StaticBody3D on Environment. No per-prop toggle.

    // How many voxel cells of wall this prop carves out, upward from the cell it
    // stands in. 0 (every prop but window frames) touches no voxels. A window
    // frame IS a hole in a wall, so the hole belongs to the SCENE and follows it
    // wherever the frame is placed — editor, subscene stamp, worldgen — instead
    // of being painted to match by hand each time. See PropSimState.ResolveStamp.
    [Export(PropertyHint.Range, "0,8,1")] public int apertureHeight = 0;

    // Must match the [Export] above in both name and default: a scene sitting on
    // the default stores no value for the load pass to read.
    private const int DEFAULT_APERTURE_HEIGHT = 0;
    private static readonly ScenePropertyCache _apertureHeights =
        new ScenePropertyCache("apertureHeight", DEFAULT_APERTURE_HEIGHT);

    public static int GetApertureHeight(PackedScene scene)
    {
        return _apertureHeights.Get(scene);
    }

    public void OnSpawned(Sim sim) { }

    public static PropInstance Create(Sim sim, PropSimState data)
    {
        var instance = data.Scene.Instantiate<PropInstance>();
        data.SeatTransform(instance);
        sim.AddChild(instance);
        return instance;
    }

    // Bisection toggle: every PropInstance hides itself when CVars.propsVisible
    // goes false. Combined with mob_visible / mob_hud / mob_shadows this lets
    // you attribute the render_draw_calls table to mobs vs props vs everything
    // else (terrain, hud, decals). Subscription lifetime tracks the node.
    public override void _Ready()
    {
        Visible = CVars.propsVisible.Value;
        CVars.propsVisible.OnChanged += OnPropsVisibleChanged;
        TreeExiting += () => CVars.propsVisible.OnChanged -= OnPropsVisibleChanged;
    }

    private void OnPropsVisibleChanged(CVar cvar)
    {
        Visible = ((CVarBool)cvar).Value;
    }

    // prop_seat_probe: how far each prop near the player stands above the
    // terrain it looks to be sitting on. Terrain collision IS the drawn mesh
    // (ChunkMesh builds it with CreateTrimeshCollision), so a ray down onto
    // Environment reads the visible ground. "rise" is the spread of that ground
    // across the prop's own column — ~0 on flat ground, larger on a grade —
    // because the painter seats props differently on each.
    private const float SeatProbeRadius = 20f;
    private const float SeatProbeUp = 2f;
    private const float SeatProbeDown = 4f;
    private const float SeatProbeSpread = 0.45f;

    public static void ProbeSeats()
    {
        Sim sim = Sim.Current;
        Player player = sim?.player;
        PhysicsDirectSpaceState3D space = sim?.GetWorld3D()?.DirectSpaceState;
        if (player == null || space == null)
        {
            GD.Print("[prop_seat] no player / world");
            return;
        }
        Vector3 center = player.GlobalPosition;
        var buckets = new System.Collections.Generic.SortedDictionary<float, int>();
        int count = 0;
        foreach (PropInstance prop in sim.GetEntities<PropInstance>())
        {
            Vector3 p = prop.GlobalPosition;
            if (p.DistanceSquaredTo(center) > SeatProbeRadius * SeatProbeRadius)
            {
                continue;
            }
            if (!TryGroundY(space, p, out float ground))
            {
                continue;
            }
            float lo = ground;
            float hi = ground;
            for (int k = 0; k < 4; k++)
            {
                Vector3 q = p + new Vector3(k == 0 ? SeatProbeSpread : k == 1 ? -SeatProbeSpread : 0f, 0f,
                    k == 2 ? SeatProbeSpread : k == 3 ? -SeatProbeSpread : 0f);
                if (TryGroundY(space, q, out float g))
                {
                    lo = Mathf.Min(lo, g);
                    hi = Mathf.Max(hi, g);
                }
            }
            float above = p.Y - ground;
            string scene = string.IsNullOrEmpty(prop.SceneFilePath) ? prop.Name : System.IO.Path.GetFileNameWithoutExtension(prop.SceneFilePath);
            GD.Print($"[prop_seat] {scene} at ({p.X:F1},{p.Y:F2},{p.Z:F1}) ground {ground:F2} above {above:+0.00;-0.00} rise {hi - lo:F2}");
            float bucket = Mathf.Snapped(above, 0.1f);
            buckets[bucket] = buckets.TryGetValue(bucket, out int n) ? n + 1 : 1;
            count++;
        }
        var summary = new System.Text.StringBuilder();
        foreach (var kv in buckets)
        {
            summary.Append($" {kv.Key:+0.0;-0.0}m x{kv.Value}");
        }
        GD.Print($"[prop_seat] {count} props within {SeatProbeRadius}m, seat above drawn ground:{summary}");
    }

    private static bool TryGroundY(PhysicsDirectSpaceState3D space, Vector3 at, out float groundY)
    {
        groundY = 0f;
        using var query = PhysicsRayQueryParameters3D.Create(
            at + Vector3.Up * SeatProbeUp, at + Vector3.Down * SeatProbeDown, (uint)ECollisionLayer.Environment);
        Godot.Collections.Dictionary hit = space.IntersectRay(query);
        if (hit.Count == 0)
        {
            return false;
        }
        groundY = hit["position"].AsVector3().Y;
        return true;
    }
}
