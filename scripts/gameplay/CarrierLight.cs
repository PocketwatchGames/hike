using System;
using System.Collections.Generic;
using Godot;

// A moving light source that pre-computes 8 diffusion kernels (one per corner
// of the source voxel) and blends them by trilinear weights each frame based
// on the carrier's sub-voxel position. This gives smooth sub-voxel light
// motion without running diffusion per frame.
//
// On voxel crossing: all 8 kernels are recomputed (~8 diffusions, ~20ms).
// Between crossings: only trilinear blend weights change — O(kernel volume)
// float reads + array writes, no diffusion. Typically < 1ms per frame.
//
// Future optimization: adjacent voxels share 4 of 8 corners, so only 4
// need recomputing per crossing.
[GlobalClass]
public partial class CarrierLight : Node3D
{
    [Export] public int Emission = 32;
    [Export] public Color LightColor = new(1f, 0.75f, 0.4f);
    [Export] public bool Active { get; set; } = true;
    [Export] public PackedScene LightOnEffectScene;
    [Export] public PackedScene LightOffEffectScene;
    [Export] public PackedScene LightLoopEffectScene;

    private CornerKernels _kernels;
    private List<(Vector3I pos, ushort r, ushort g, ushort b)> _currentDeposit = new();
    private bool _registered;
    private Vector3I _lastVoxel;
    private Vector3 _lastSubVoxel;
    private Fx _loopEffect;

    public override void _Ready()
    {
        // Deferred so Activate's Fx.Create calls run after the parent
        // (Mob / Player) finishes its own add_child cycle. Synchronous
        // invocation here triggers Godot's "Parent node is busy setting
        // up children" rejection — the Fx still ends up parented via
        // Fx.Create's deferred fallback, but the spurious error spams
        // the console at every spawn. Deferring the whole activation
        // keeps the log clean and registers the light one frame later,
        // which is invisible.
        if (Active) { CallDeferred(MethodName.Activate); }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Active && !_registered)
        {
            Activate();
            return;
        }
        if (!_registered) { return; }

        World world = World.Current;
        if (world == null) { return; }

        Vector3 pos = GlobalPosition;
        Vector3I voxel = new Vector3I(
            Mathf.FloorToInt(pos.X),
            Mathf.FloorToInt(pos.Y),
            Mathf.FloorToInt(pos.Z));
        Vector3 sub = new Vector3(pos.X - voxel.X, pos.Y - voxel.Y, pos.Z - voxel.Z);

        if (voxel != _lastVoxel)
        {
            _kernels = LightEngine.ComputeCornerKernels(
                world.WorldState, voxel, Emission, LightColor);
            _lastVoxel = voxel;
            _lastSubVoxel = new Vector3(-1, -1, -1);
        }

        if (sub == _lastSubVoxel) { return; }
        _lastSubVoxel = sub;

        BlendAndDeposit(world.WorldState, sub);
    }

    public void SetActive(bool active)
    {
        Active = active;
        if (active) { Activate(); } else { Deactivate(); }
    }

    public void Activate()
    {
        if (_registered) { return; }
        World world = World.Current;
        if (world == null) { return; }

        Vector3 pos = GlobalPosition;
        var voxel = new Vector3I(
            Mathf.FloorToInt(pos.X),
            Mathf.FloorToInt(pos.Y),
            Mathf.FloorToInt(pos.Z));

        _kernels = LightEngine.ComputeCornerKernels(
            world.WorldState, voxel, Emission, LightColor);
        _lastVoxel = voxel;
        _registered = true;
        Active = true;

        Vector3 sub = new Vector3(pos.X - voxel.X, pos.Y - voxel.Y, pos.Z - voxel.Z);
        _lastSubVoxel = sub;
        BlendAndDeposit(world.WorldState, sub);

        if (LightOnEffectScene != null)
        {
            Fx.Create(LightOnEffectScene, GetParent() ?? this, GlobalPosition);
        }
        if (_loopEffect == null && LightLoopEffectScene != null)
        {
            _loopEffect = Fx.Create(LightLoopEffectScene, this, Vector3.Zero);
        }
    }

    public void Deactivate()
    {
        if (!_registered) { return; }
        World world = World.Current;
        if (world == null) { return; }
        RemoveCurrentDeposit(world.WorldState);
        _kernels = null;
        _currentDeposit.Clear();
        _registered = false;
        Active = false;

        if (LightOffEffectScene != null)
        {
            Fx.Create(LightOffEffectScene, GetParent() ?? this, GlobalPosition);
        }
        if (_loopEffect != null)
        {
            _loopEffect.Stop();
            _loopEffect = null;
        }
    }

    public override void _ExitTree()
    {
        Deactivate();
    }

    private void BlendAndDeposit(WorldState worldState, Vector3 sub)
    {
        RemoveCurrentDeposit(worldState);

        float sx = Mathf.Clamp(sub.X, 0f, 0.99999f);
        float sy = Mathf.Clamp(sub.Y, 0f, 0.99999f);
        float sz = Mathf.Clamp(sub.Z, 0f, 0.99999f);

        // Compute trilinear weights for 8 corners.
        Span<float> weights = stackalloc float[8];
        for (int cx = 0; cx <= 1; cx++)
        {
            float wx = cx == 0 ? (1f - sx) : sx;
            for (int cy = 0; cy <= 1; cy++)
            {
                float wy = cy == 0 ? (1f - sy) : sy;
                for (int cz = 0; cz <= 1; cz++)
                {
                    float wz = cz == 0 ? (1f - sz) : sz;
                    int c = cx | (cy << 1) | (cz << 2);
                    weights[c] = _kernels.SeedOpen[c] ? wx * wy * wz : 0f;
                }
            }
        }

        int dim = _kernels.Dim;
        int dimSq = dim * dim;
        Vector3I origin = _kernels.Origin;
        int[] nonZero = _kernels.NonZeroIndices;
        int nonZeroCount = _kernels.NonZeroCount;

        _currentDeposit.Clear();
        _currentDeposit.Capacity = nonZeroCount;

        for (int ni = 0; ni < nonZeroCount; ni++)
        {
            int idx = nonZero[ni];

            float blendR = 0f, blendG = 0f, blendB = 0f;
            for (int c = 0; c < 8; c++)
            {
                float w = weights[c];
                if (w <= 0f) { continue; }
                blendR += w * _kernels.R[c][idx];
                blendG += w * _kernels.G[c][idx];
                blendB += w * _kernels.B[c][idx];
            }

            if (blendR < 0.5f && blendG < 0.5f && blendB < 0.5f) { continue; }

            ushort qr = (ushort)Math.Min(ushort.MaxValue, (int)(blendR + 0.5f));
            ushort qg = (ushort)Math.Min(ushort.MaxValue, (int)(blendG + 0.5f));
            ushort qb = (ushort)Math.Min(ushort.MaxValue, (int)(blendB + 0.5f));
            if (qr == 0 && qg == 0 && qb == 0) { continue; }

            int lz = idx / dimSq;
            int ly = (idx / dim) % dim;
            int lx = idx % dim;
            Vector3I wpos = new Vector3I(origin.X + lx, origin.Y + ly, origin.Z + lz);

            worldState.AddBlockLightWorld(wpos.X, wpos.Y, wpos.Z, qr, qg, qb);
            _currentDeposit.Add((wpos, qr, qg, qb));
        }
    }

    private void RemoveCurrentDeposit(WorldState worldState)
    {
        for (int i = 0; i < _currentDeposit.Count; i++)
        {
            var (pos, r, g, b) = _currentDeposit[i];
            worldState.SubtractBlockLightWorld(pos.X, pos.Y, pos.Z, r, g, b);
        }
    }
}
