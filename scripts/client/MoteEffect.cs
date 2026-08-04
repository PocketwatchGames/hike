using Godot;

// Camera-parented floating dust-mote visuals — a GPU particle system
// (scenes/fx/motes.tscn).
// Modeled on RainEffect: the GPUParticles3D is a child of MainCamera for
// scene-structure convenience, but each frame we override its world position
// so the emission box stays anchored on the player (world-axis-aligned), and
// SkyController.Apply() pushes the per-frame look (dust hue + density wash)
// every frame so motes track time-of-day and weather. The specular glint
// colour comes straight from the global sun_color (blended sun→moon→sunset).
//
// Particles emit everywhere in the box; the draw-pass shader (mote.gdshader)
// gates each speck per-fragment against the voxel sun mask × cloud shadow, so
// motes only appear inside intense sunbeams and vanish under cover / in caves
// — the same "emit everywhere, discard per-fragment" approach rain uses for
// its falling streaks. Density (AmountRatio) is scaled by SkyController's
// dust × cloudCover² wash so clear skies stay clean.
[GlobalClass]
public partial class MoteEffect : Node3D
{
    // Mirror of SkyController.Current — SkyController.Apply() fetches this to
    // push per-frame mote params. Same static-ref rationale as RainEffect:
    // Godot 4's C# binding strips a NodePath wired from another scene because
    // the instanced root arrives typed as plain Node3D at property-set time.
    public static MoteEffect Current { get; private set; }

    [Export] public GpuParticles3D moteParticles;

    // --- Tunables ---------------------------------------------------------
    // Total particle budget (the CEILING, hit only at full dust density). Most
    // are discarded per-fragment outside the beams, so this is far above the
    // count ever visible at once — kept modest because the dust wash now scales
    // the live buffer down from here (see amountQuantizationStep below).
    [Export] public int particleCount = 1500;
    // Buffer-size quantization for the dust-driven Amount. The live particle
    // buffer is rounded to multiples of this and only rewritten when it crosses
    // a step — each rewrite is a GPU realloc that restarts emission (a brief
    // whole-field repop), so coarser = fewer pops as dust/weather drift, finer =
    // the simulated count tracks dust more tightly.
    [Export] public int amountQuantizationStep = 256;
    // Speck size in SubViewport pixels (pushed to the draw shader). 1 px.
    [Export(PropertyHint.Range, "0.1,8,0.1")] public float moteSizePx = 1.0f;
    // Float speed (m/s). A slow gentle drift — motes hang in the air and
    // wander rather than streak. Drives the process material's initial
    // velocity. Keep low; this is the baseline drift before turbulence.
    [Export(PropertyHint.Range, "0,8,0.01")] public float speed = 0.3f;
    // Turbulence noise strength on the process material — the independent,
    // wandering, non-uniform motion (vs a uniform scroll). 0 disables. Keep
    // SMALL: high turbulence is what produces occasional fast outlier specks
    // (curl-noise hot spots); the .tscn also caps influence + adds damping so
    // any kick decays back to a gentle drift.
    [Export(PropertyHint.Range, "0,4,0.01")] public float turbulence = 0.15f;
    // How much the local baked wind nudges motes downwind. Applied as a
    // horizontal drift FORCE (the process material's Gravity vector) because
    // that's the only knob that imparts a consistent net drift through the
    // 180-degree emission spread — Direction/InitialVelocity get washed out by
    // the full-sphere spread. Since Gravity is a constant acceleration the
    // drift accumulates over a mote's (long) lifetime, so keep this SMALL: at
    // 0.02 a gentle ~5 m/s breeze leans motes a fraction of a m/s downwind
    // while a ~25 m/s gale visibly blows them. The sampled wind is damped by
    // the wind factor first, so sealed cells (caves / interiors) stay calm. 0
    // disables (pure drift + turbulence, as before).
    [Export(PropertyHint.Range, "0,1,0.001")] public float windInfluence = 0.02f;
    // Near-ground concentration: brightness e-folds over this many metres of
    // height above the player's ground. Smaller = motes hug the ground.
    [Export(PropertyHint.Range, "0.5,32,0.5")] public float nearGroundHeight = 6.0f;
    // Tumble rate of each fleck's normal (radians/sec). The specular glint
    // flashes as the spinning normal sweeps through the light/eye half-vector,
    // so this is effectively the sparkle rate. Each speck has an independent
    // per-particle phase.
    [Export(PropertyHint.Range, "0,12,0.1")] public float spinRate = 2.0f;
    // Specular sharpness — higher = tighter, briefer, rarer glints.
    [Export(PropertyHint.Range, "1,64,1")] public float specPower = 8.0f;
    // Specular flash brightness (in the light colour); >1 lets a catching fleck
    // bloom briefly.
    [Export(PropertyHint.Range, "0,4,0.01")] public float specIntensity = 1.0f;
    // Beam gate: lit = light_map.r * (1 - cloud) must clear this for a speck
    // to show. Higher = motes confined to the most intense beams only; lower =
    // motes also drift through softly-lit air, not just the sharpest beams.
    [Export(PropertyHint.Range, "0,1,0.01")] public float beamThreshold = 0.3f;
    // Block-light (torch / campfire / lantern) APPEARANCE: how strongly local
    // light tints/brightens a speck's glint. Block-light values are small, so
    // this wants a big multiplier — 8-24 (past ~1 the glint blooms). 0 =
    // sun/moon shafts only.
    [Export(PropertyHint.Range, "0,32,0.1")] public float blockLightStrength = 8.0f;
    // Block-light REVEAL: how far from a light motes appear at all — separate
    // from the appearance term above so a long-reaching light can still have a
    // tight mote pool. Raise the curve to tighten the pool; the strength then
    // sets where its edge lands. See the block_light_* block in mote.gdshader.
    [Export(PropertyHint.Range, "0.5,4,0.05")] public float blockLightRevealCurve = 1.5f;
    [Export(PropertyHint.Range, "0,64,0.5")] public float blockLightRevealStrength = 13.0f;
    // Shaft occlusion gate — makes motes track the actual god-rays instead of
    // blanketing open lit sky (clear desert noon). A mote needs cloud cover OR
    // local terrain/foliage shadow contrast to show. shaftSampleRadius = how far
    // out (m) to probe the sun mask for that contrast; occlusionMin/Soft remap
    // how much occlusion is required → fully shown. Block-lit motes bypass it.
    [Export(PropertyHint.Range, "0.25,8,0.25")] public float shaftSampleRadius = 2.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float shaftOcclusionMin = 0.15f;
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float shaftOcclusionSoft = 0.3f;

    // Metres the emission column is lifted above the player's ground position
    // each frame, so the box sits in the visible air column instead of
    // straddling the player (its bottom half would otherwise be underground,
    // where every particle is discarded by the sun-mask gate — wasted budget).
    // Pair with the box's vertical extent in motes.tscn (~5 half-height): with
    // a +4 lift the box spans roughly ground−1 .. ground+9, so we stop spawning
    // the underground slab and the too-high band that nearGroundHeight fades out.
    [Export(PropertyHint.Range, "0,16,0.5")] public float anchorHeightAbovePlayer = 4f;

    // --- Local air density ------------------------------------------------
    // The regional weather scalar (ZoneData.dustAmount, via SkyController) sets
    // the baseline particle budget, but it knows nothing about authored fog
    // volumes or roof interior dust. Those are per-voxel, so the count also
    // rides the air the player is actually standing in — otherwise a dusty room
    // in a clean zone has no particles to light and the whole roof mechanic is
    // inert. Taken as a MAX with the weather wash, never a replacement.
    // >1 so a MODEST authored dust value still fills the field. The same channel
    // feeds the fog raymarch's extinction, so needing dust near 1.0 for a good
    // mote field would force a washed-out interior to get one; this buys the
    // motes without the haze.
    [Export(PropertyHint.Range, "0,8,0.05")] public float localDustDensityScale = 3f;
    // Metres above the player's feet to sample. Wants to sit in the room's air
    // rather than in the floor voxel.
    [Export(PropertyHint.Range, "0,4,0.1")] public float localDustSampleHeight = 1f;
    // Seconds for the sampled value to catch up. Smoothed because Amount
    // reallocates the GPU buffer and restarts emission — stepping through a
    // doorway would otherwise flush the whole field, repeatedly.
    [Export(PropertyHint.Range, "0.05,10,0.05")] public float localDustLagSeconds = 1.5f;

    private float _localDust;
    private float _intensity;
    // The density the live buffer was sized for. Handed to the shader so it can
    // cull each speck back to what its own position earns — without it, a dusty
    // room raises the count across the whole emission box and motes appear in
    // the open air outside. See mote_budget_density in mote.gdshader.
    private float _budgetDensity = 1f;
    // Pre-integrated sparkle time (accumulates dt), pushed as mote_time so the
    // twinkle phase doesn't ride raw shader TIME (precision over long sessions).
    private float _moteTime;

    // Runtime copies of the process + draw-pass materials. Duplicated in
    // _Ready so the per-frame tuning writes (and SkyController's weather-driven
    // glint/dust colour writes) never persist back to the shared .tres on an
    // editor save (same pattern as RainEffect's runtime materials).
    public ShaderMaterial MoteMatRuntime { get; private set; }
    private ParticleProcessMaterial _procRuntime;
    // Last Amount we pushed — Amount reallocates GPU buffers (and restarts
    // emission), so we only rewrite it when the dust-driven, quantized target
    // crosses a step. -1 forces the first sync.
    private int _appliedCount = -1;

    public override void _Ready()
    {
        Current = this;

        if (moteParticles == null)
        {
            return;
        }

        moteParticles.Emitting = true;

        // Duplicate both materials. The actual tunable values are pushed every
        // frame in _Process (NOT here) so editing the node's [Export]s on the
        // live/running effect takes effect immediately instead of only at load.
        if (moteParticles.ProcessMaterial is ParticleProcessMaterial proc)
        {
            _procRuntime = (ParticleProcessMaterial)proc.Duplicate();
            moteParticles.ProcessMaterial = _procRuntime;
            _procRuntime.Gravity = Vector3.Zero;
        }

        if (moteParticles.DrawPass1 is PrimitiveMesh mesh && mesh.Material is ShaderMaterial mat)
        {
            MoteMatRuntime = (ShaderMaterial)mat.Duplicate();
            mesh.Material = MoteMatRuntime;
        }
    }

    public override void _ExitTree()
    {
        if (Current == this)
        {
            Current = null;
        }
    }

    // Called by SkyController.ApplyMotes() every frame. `intensity` is the
    // already-computed density wash (dust baseline faded by shaft presence), so
    // this node just consumes it as the density scalar (AmountRatio).
    public void SetIntensity(float intensity)
    {
        _intensity = Mathf.Clamp(intensity, 0f, 1f);
    }

    public override void _Process(double delta)
    {
        using var _prof = Profiler.Sample("MoteEffect.Process");

        // Bisection / cost toggle: when disabled, hide the particles so the
        // renderer skips their simulation + draw-pass shader entirely, and skip
        // the per-frame param pushes below. Hidden (not just non-emitting) so
        // the cost drops immediately rather than after the 10s lifetime drains.
        if (moteParticles != null && !CVars.motes.Value)
        {
            if (moteParticles.Visible)
            {
                moteParticles.Visible = false;
                moteParticles.Emitting = false;
            }
            return;
        }
        if (moteParticles != null && !moteParticles.Visible)
        {
            moteParticles.Visible = true;
            moteParticles.Emitting = true;
        }

        float dt = (float)delta;
        _moteTime += dt;

        Sim sim = Sim.Current;
        bool worldReady = sim != null && sim.player != null;

        // Anchor the emission column on the PLAYER (not the camera, which sits
        // ~65 m above) so motes populate the visible near-ground air. The node
        // is a child of MainCamera for scene structure; we override world
        // position here and kill any inherited camera pitch. While here, sample
        // the local baked wind so we can lean the motes downwind below.
        Vector3 windDrift = Vector3.Zero;
        if (worldReady)
        {
            Vector3 pp = sim.player.GlobalPosition;
            GlobalPosition = new Vector3(pp.X, pp.Y + anchorHeightAbovePlayer, pp.Z);

            WorldState ws = sim.WorldState;
            if (ws != null)
            {
                float sampled = ws.GetAirDensityWorld(
                    Mathf.FloorToInt(pp.X),
                    Mathf.FloorToInt(pp.Y + localDustSampleHeight),
                    Mathf.FloorToInt(pp.Z));
                // Exponential catch-up, frame-rate independent.
                float k = 1f - Mathf.Exp(-dt / Mathf.Max(localDustLagSeconds, 0.01f));
                _localDust = Mathf.Lerp(_localDust, sampled, k);
            }
            if (ws != null && windInfluence > 0f)
            {
                Vector3 windVel = ws.GetWindVelocityWorld(
                    Mathf.FloorToInt(pp.X), Mathf.FloorToInt(pp.Y), Mathf.FloorToInt(pp.Z));
                // Damp by the wind factor so motes in sealed cells (caves /
                // building interiors) aren't pushed around by ambient wind.
                windDrift = windVel * ws.SampleWindFactor(pp) * windInfluence;
            }
        }
        GlobalRotation = Vector3.Zero;

        // Motes emit everywhere; the draw shader gates per-fragment to the
        // sunbeams. Density rides the SkyController wash.
        // Density (the dust × shaft-presence wash, pushed via SetIntensity)
        // drives the ACTUAL live particle count, not just AmountRatio — so a
        // low-dust scene simulates far fewer motes instead of thinning a full
        // buffer. Amount reallocates/restarts the system, so quantize it to
        // coarse steps and only rewrite on a step change; AmountRatio then
        // carries the smooth residual within the step so density still tracks
        // the wash frame-to-frame without thrashing the buffer.
        if (moteParticles != null)
        {
            int ceiling = Mathf.Max(1, particleCount);
            int step = Mathf.Clamp(amountQuantizationStep, 1, ceiling);
            // Whichever is thicker: the regional weather air, or the air right
            // here (fog volume / dusty roof interior).
            float density = Mathf.Clamp(
                Mathf.Max(_intensity, _localDust * localDustDensityScale), 0f, 1f);
            _budgetDensity = density;
            int target = Mathf.RoundToInt(ceiling * density);
            // Round the buffer UP to the next step (min one step) so AmountRatio
            // always has headroom to fine-tune the density downward.
            int quantized = Mathf.Clamp(((target + step - 1) / step) * step, step, ceiling);
            if (quantized != _appliedCount)
            {
                moteParticles.Amount = quantized;
                _appliedCount = quantized;
            }
            moteParticles.AmountRatio = Mathf.Clamp(target / (float)quantized, 0f, 1f);
        }

        // Push the motion tunables every frame so they're live-tunable on the
        // running node. These only affect newly-spawned particles, which is
        // fine for a continuously-emitting drift field.
        if (_procRuntime != null)
        {
            _procRuntime.InitialVelocityMin = speed * 0.4f;
            _procRuntime.InitialVelocityMax = speed;
            _procRuntime.TurbulenceEnabled = turbulence > 0f;
            _procRuntime.TurbulenceNoiseStrength = turbulence;
            // Gentle downwind lean (zero when windInfluence is 0 or in calm /
            // sealed air), applied as the constant gravity-force vector.
            _procRuntime.Gravity = windDrift;
        }

        // Push the draw-pass tunables every frame too (cheap), so size / spin /
        // specular / beam-gate / near-ground edits apply live. The glint/dust
        // COLOURS are weather-driven and pushed by SkyController.
        if (MoteMatRuntime != null)
        {
            MoteMatRuntime.SetShaderParameter("mote_size_px", moteSizePx);
            MoteMatRuntime.SetShaderParameter("spin_rate", spinRate);
            MoteMatRuntime.SetShaderParameter("spec_power", specPower);
            MoteMatRuntime.SetShaderParameter("spec_intensity", specIntensity);
            MoteMatRuntime.SetShaderParameter("beam_threshold", beamThreshold);
            MoteMatRuntime.SetShaderParameter("block_light_strength", blockLightStrength);
            MoteMatRuntime.SetShaderParameter("block_light_reveal_curve", blockLightRevealCurve);
            MoteMatRuntime.SetShaderParameter("block_light_reveal_strength", blockLightRevealStrength);
            MoteMatRuntime.SetShaderParameter("shaft_sample_radius", shaftSampleRadius);
            MoteMatRuntime.SetShaderParameter("shaft_occlusion_min", shaftOcclusionMin);
            MoteMatRuntime.SetShaderParameter("shaft_occlusion_soft", shaftOcclusionSoft);
            MoteMatRuntime.SetShaderParameter("near_ground_height", nearGroundHeight);
            MoteMatRuntime.SetShaderParameter("mote_time", _moteTime);
            // Bound per-frame rather than once, because the volume recenters as
            // the player crosses chunks — the texture handle is stable but its
            // origin is not. Unbound (no ChunkManager yet) leaves inv_size at
            // zero, which the shader reads as "no local dust".
            // Both halves of the density reconciliation, pushed together so the
            // shader's max() matches the one that sized the buffer this frame.
            MoteMatRuntime.SetShaderParameter("mote_budget_density", _budgetDensity);
            MoteMatRuntime.SetShaderParameter("mote_weather_density", _intensity);
            MoteMatRuntime.SetShaderParameter("local_dust_density_scale", localDustDensityScale);

            FogMap fog = sim?.ChunkManager?.FogVolume;
            if (fog != null)
            {
                MoteMatRuntime.SetShaderParameter("fog_map", fog.Texture);
                MoteMatRuntime.SetShaderParameter("fog_map_origin", fog.Origin);
                MoteMatRuntime.SetShaderParameter("fog_map_inv_size", fog.InvSize);
            }
            float groundY = worldReady ? sim.player.GlobalPosition.Y : 0f;
            MoteMatRuntime.SetShaderParameter("ground_reference_y", groundY);
        }
    }
}
