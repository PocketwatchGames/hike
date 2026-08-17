using System.Collections.Generic;
using Godot;

// A cascade: the falling ribbon of water, the spray at either end of it, and the
// loop of noise it makes at the bottom. Purely audio-visual — the drop is air,
// so there is no collision, no path blocking and nothing to interact with, and
// the player falls through it.
[GlobalClass]
public partial class Waterfall : Node3D, IWorldEntity
{
    // How often the earshot check runs. The ambience is a single looping stream
    // per site and a site can sit at the far edge of the loaded radius, so it is
    // worth pausing rather than mixing at -inf; a fifth of a second is invisible
    // through the distance attenuation. Matches ChunkAmbienceSpawner.
    private const float EARSHOT_SWEEP_SEC = 0.2f;
    // Hysteresis past MaxDistance before pausing, so a player loitering exactly
    // at the attenuation edge doesn't thrash the stream.
    private const float EARSHOT_SLACK = 4f;

    private AudioStreamPlayer3D _audio;
    private float _earshot;
    private double _sweepAccumSec;

    public void OnSpawned(Sim sim) { }

    public static Waterfall Create(Sim sim, WaterfallSimState data)
    {
        WaterfallData style = sim.SimData?.waterfalls;
        WaterfallTierData tier = style?.TierFor(data.FallHeight);
        // ShouldSpawn already refuses these, but CreateEntity is reachable from
        // paths that don't consult it (editor placement, respawn), and a fall
        // with no authored tier has nothing to be.
        if (style == null || tier == null) { return null; }
        var instance = new Waterfall();
        // Before AddChild, like every other entity — _Ready reads the transform.
        data.SeatTransform(instance);
        instance.Build(data, style, tier);
        sim.AddChild(instance);
        return instance;
    }

    private void Build(WaterfallSimState data, WaterfallData style, WaterfallTierData tier)
    {
        ArrayMesh mesh = WaterfallMeshBuilder.Build(data, style);
        if (mesh != null)
        {
            var visual = new MeshInstance3D();
            visual.Mesh = mesh;
            visual.MaterialOverride = style.sheetMaterial;
            visual.Layers = GameCamera.MainSceneLayer;
            // Water casts no shadow, and a curtain of it casting one would put a
            // black slab across the cliff behind the fall.
            visual.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            AddChild(visual);
            // Per-instance rather than per-tier materials, so every fall in the
            // world shares one material and still carries its own weight.
            visual.SetInstanceShaderParameter("sheet_thickness", tier.sheetThickness);
            visual.SetInstanceShaderParameter("sheet_foam", tier.foam);
            visual.SetInstanceShaderParameter("fall_top_y", data.TopY);
            visual.SetInstanceShaderParameter("fall_bottom_y", data.BottomY);
        }

        SpawnEdgeFx(data, style, tier.lipFx, top: true);
        SpawnEdgeFx(data, style, tier.baseFx, top: false);
        SpawnAudio(data, style, tier);
        // The only thing _Process does is the earshot sweep, so a silent fall
        // stays out of the process list entirely.
        SetProcess(_audio != null);
    }

    // Spray along one horizontal edge of the sheet. Emitters are spaced along the
    // edge rather than counted, so a one-column trickle gets a single puff and a
    // wide curtain mists along its whole width from the same authored rule.
    private void SpawnEdgeFx(WaterfallSimState data, WaterfallData style, PackedScene scene, bool top)
    {
        if (scene == null) { return; }
        List<Vector3> edge = EdgePositions(data, style, top);
        if (edge.Count == 0) { return; }

        int count = Mathf.Clamp(
            Mathf.RoundToInt(edge.Count / Mathf.Max(style.metersPerEmitter, 1f)),
            1, Mathf.Max(style.maxEmittersPerEdge, 1));
        for (int i = 0; i < count; i++)
        {
            // Centres of `count` equal slices of the edge, so the emitters sit
            // inside the sheet instead of one hanging off each end.
            int index = Mathf.Min((int)((i + 0.5f) / count * edge.Count), edge.Count - 1);
            Fx.Create(scene, this, edge[index] - data.WorldPosition);
        }
    }

    // The two ends of the sheet as world positions: the lip the water leaves,
    // and where the jet meets the pool. The landing is offset out from the lip
    // by the same reach the sheet is swept with, so the plume sits under the
    // falling water rather than against the wall behind it.
    private static List<Vector3> EdgePositions(WaterfallSimState data, WaterfallData style, bool top)
    {
        var positions = new List<Vector3>();
        foreach (WaterfallLip lip in data.Lips)
        {
            var pour = new Vector3(lip.DirX, 0f, lip.DirZ);
            Vector3 edge = new Vector3(lip.X + 0.5f, data.TopY, lip.Z + 0.5f) - pour * 0.5f;
            positions.Add(top
                ? edge
                : new Vector3(edge.X, data.BottomY, edge.Z) + pour * style.pourReach);
        }
        // Stable left-to-right order, so the spacing below picks a spread along
        // the edge rather than whatever order the lips were measured in.
        positions.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z));
        return positions;
    }

    // The plunge pool is where a waterfall is loud, so the one ambience source
    // sits at the middle of the landing line rather than at the entity origin up
    // on the lip.
    private void SpawnAudio(WaterfallSimState data, WaterfallData style, WaterfallTierData tier)
    {
        if (tier.sound == null) { return; }
        List<Vector3> landing = EdgePositions(data, style, top: false);
        if (landing.Count == 0) { return; }
        Vector3 centre = Vector3.Zero;
        foreach (Vector3 p in landing) { centre += p; }
        centre /= landing.Count;

        _audio = new AudioStreamPlayer3D();
        _audio.Stream = tier.sound;
        _audio.Bus = "World3D";
        _audio.VolumeDb = tier.volumeDb;
        _audio.UnitSize = tier.unitSize;
        _audio.MaxDistance = tier.maxDistance;
        _audio.Position = centre - data.WorldPosition;
        _earshot = tier.maxDistance + EARSHOT_SLACK;
        AddChild(_audio);
    }

    // The entity is built before it is added to the world (its transform has to
    // be seated first), and a player cannot start playing until it is in the
    // tree — so the loop starts here rather than where it is created. Play then
    // pause, so the decoder is warm and the first audible frame when the player
    // walks into earshot doesn't pop.
    public override void _Ready()
    {
        if (_audio == null) { return; }
        _audio.Play();
        _audio.StreamPaused = true;
    }

    public override void _Process(double delta)
    {
        if (_audio == null) { return; }
        _sweepAccumSec += delta;
        if (_sweepAccumSec < EARSHOT_SWEEP_SEC) { return; }
        _sweepAccumSec = 0.0;

        Player player = Sim.Current?.player;
        if (player == null) { return; }
        bool inEarshot = _audio.GlobalPosition.DistanceSquaredTo(player.GlobalPosition) <= _earshot * _earshot;
        if (_audio.StreamPaused == inEarshot)
        {
            _audio.StreamPaused = !inEarshot;
        }
    }
}
