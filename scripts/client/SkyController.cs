using Godot;

// Owns the authored atmospheric state: sky dome colors, clouds, sun tint,
// fog, haze, inscatter. Everything here is simulation-driven visual state
// that weather / time-of-day systems write to; none of it belongs in CVars.
//
// Two output channels, handled uniformly:
//   - Global shader uniforms (sky dome + voxel + sprite + water consume
//     these via project.godot [shader_globals]). Registered once in _Ready
//     so CVar overrides would stick — but these values aren't CVars any
//     more, so it's just a seeding pass.
//   - Per-material uniforms on fog_volumetric.tres (the fog raymarch is the
//     only consumer). Pushed to the material in _Ready via Apply().
//
// For runtime updates (weather systems, day/night transitions): mutate the
// [Export] fields, then call Apply() to re-push to GPU. _Process handles
// the one genuinely dynamic value — sun direction from WorldState.
//
// [Tool] makes this script execute in the Godot editor too. _Process calls
// Apply() every editor frame, so any value tweaked in the inspector shows
// its effect immediately in the viewport without needing to play the scene.
[Tool]
[GlobalClass]
public partial class SkyController : Node3D
{
    // Static reference for CVars / weather-preset code that needs to mutate
    // atmospheric exports at runtime. Set in _Ready, cleared in _ExitTree.
    // There's only one SkyController per game scene.
    public static SkyController Current { get; private set; }

    [ExportGroup("Sky Dome")]
    [Export] public Color horizonColor = new Color(0.72f, 0.82f, 0.92f);
    [Export] public Color zenithColor = new Color(0.25f, 0.48f, 0.82f);

    [ExportGroup("Sun")]
    // Wire to the scene's DirectionalLight3D. When set, its transform is
    // the single source of truth for sun direction — both Godot's built-in
    // terrain shadow pass (via the node itself) and the fog shader's beam
    // direction (via sun_world_dir global) track the same rotation.
    // Without this wired, falls back to WorldState.ShadowLightDirection
    // (the day/night sim), which only drives the fog shader and would
    // disagree with terrain shadows if the node transform is edited.
    [Export] public DirectionalLight3D sunLight;
    [Export] public Color sunColor = new Color(1.0f, 0.96f, 0.88f);
    // Fraction of the BFS sun mask treated as sky-bounce ambient that survives
    // the directional shadow. The remaining (1 - sunAmbient) is "direct sun"
    // that the shadow can kill. Read by voxel_clip / sprite_lit shaders AND
    // WorldState.GetPerceivedLight so visual and gameplay stay in sync.
    // 0 = pitch-black hard shadows, 1 = directional shadow ignored.
    // Weather systems raise this for overcast days (diffuse sky, no crisp
    // shadows) and lower it for clear days (punchy direct sun).
    [Export(PropertyHint.Range, "0,1,0.01")] public float sunAmbient = 0.4f;
    // Reverse directional shading: each tint is the color a face is multiplied
    // by when it faces fully away from the corresponding light. White = no
    // effect; darker/saturated colors darken and tint backfacing surfaces.
    [Export] public Color sunTintColor = new Color(0.15f, 0.15f, 0.35f);
    [Export] public Color fillTintColor = new Color(0.5f, 0.5f, 0.5f);
    // Fill light pitch below horizon (degrees) and yaw offset from the sun.
    [Export] public float fillPitchDegrees = 35f;
    [Export] public float fillYawOffsetDegrees = 135f;

    [ExportGroup("Clouds")]
    [Export] public Color cloudColor = new Color(1.0f, 0.98f, 0.95f);
    [Export] public Vector2 cloudScroll = new Vector2(0.004f, 0.002f);
    // Noise-to-cloud remap used by both sky dome and projective ground
    // shadows. Values ABOVE threshold start being cloud; sharpness controls
    // transition width (0 = soft gradient, 1 = hard step).
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudThreshold = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudSharpness = 0.7f;
    [Export] public float cloudScale = 0.15f;
    // World Y of the flat cloud plane used for projective sun-shadow casting
    // (see shaders/cloud_shadow.gdshaderinc). The sky dome renders clouds at
    // "infinity" via sky_common, so this altitude isn't directly visible —
    // it controls how far along the sun direction ground points project to
    // sample the cloud pattern. 40–80 usually reads well.
    [Export] public float cloudAltitude = 60f;
    // How aggressively a full cloud shadow darkens direct sun on terrain /
    // sprites / water. 1.0 = full cloud leaves only block light; 0.6 leaves
    // the ambient sky-bounce portion lit; 0 disables cloud shadows entirely.
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudShadowStrength = 1.0f;

    [ExportGroup("Fog — Authored (voxel fog_map)")]
    // Wire this to res://resources/materials/fog_volumetric.tres — the
    // shader's per-material uniforms are pushed here from the fields below.
    [Export] public ShaderMaterial fogMaterial;
    [Export] public Color fogColor = new Color(0.85f, 0.88f, 0.95f);
    // Multiplier on the per-voxel authored fog density (fog_map). Weather
    // systems drive this — foggy weather cranks it up, clear weather drops
    // it toward 0. The authored fog_map controls WHERE the fog lives (lake
    // mist, valley basins, dungeon interiors); this controls HOW DENSE it
    // reads globally.
    [Export(PropertyHint.Range, "0,1,0.001")] public float fogDensity = 0.05f;
    [Export] public float fogMaxDistance = 100.0f;
    [Export(PropertyHint.Range, "1,64,1")] public int fogSteps = 48;

    [ExportGroup("Fog — Atmosphere (uniform dust)")]
    // Atmospheric dust. Scattering medium god rays need to be visible.
    // Height-gated per pixel to a thin band above the terminating surface
    // (see dustBandHeight) — dust pools near the ground, so beams stay
    // concentrated in the near-ground air column rather than smearing
    // across the whole view ray.
    //
    // Dust contributes to SCATTERING only, not extinction — authored fog
    // is what tints the scene with haze color; dust is purely the medium
    // that reveals beams. Keep this small; if set too high the inscatter
    // from dust dominates even in fully sunlit areas and produces uniform
    // glow.
    [Export(PropertyHint.Range, "0,1,0.0001")] public float dustDensity = 0.003f;
    // How many meters above the reference Y the dust layer extends.
    // Above this height, dust fades to 0 → no beam contribution. 8-12m
    // is a natural range for "mist near the ground" in an outdoor scene.
    [Export(PropertyHint.Range, "1,64,0.1")] public float dustBandHeight = 10.0f;
// Fine-scale animated noise overlaid on the dust density within the
    // band. Creates narrow dense pockets / sparse gaps that read as
    // additional beam structure — parallels the cloud noise but at
    // higher spatial frequency and with non-directional drift. Same
    // threshold/sharpness remap shape as the cloud mask.
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustNoiseStrength = 0.7f;
    [Export(PropertyHint.Range, "0,2,0.001")] public float dustNoiseScale = 0.12f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustNoiseThreshold = 0.4f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustNoiseSharpness = 0.5f;
    [Export] public Vector2 dustNoiseScroll = new Vector2(0.05f, 0.03f);

    [ExportGroup("Fog — Inscatter (Shafts + Halos)")]
    [Export(PropertyHint.Range, "0,32,0.01")] public float sunShaftIntensity = 8.0f;
    [Export(PropertyHint.Range, "0,32,0.01")] public float blockHaloIntensity = 6.0f;
    // Henyey-Greenstein phase. 0 = isotropic (shafts visible from any view
    // direction); positive = forward-peaked (dramatic only when camera faces
    // sun). The isometric camera's locked pitch rarely faces the sun, so 0
    // is the safe default.
    [Export(PropertyHint.Range, "-0.95,0.95,0.01")] public float scatterAnisotropy = 0.0f;
    // Sun-visibility threshold for shaft contribution. The voxel LightMap
    // leaks partial sun values into shallow cave air via lateral BFS
    // propagation — those values (~0.2-0.5) are correct for terrain
    // lighting but show up as unwanted underground shafts when scaled
    // by shaft intensity. Raising this forces shaft contribution to only
    // fully-sunlit voxels. 0 = no filtering (raw lightmap), 1 = only
    // perfectly lit voxels. 0.6-0.8 usually eliminates underground
    // shaft bleed without losing shafts at cave entrances.
    [Export(PropertyHint.Range, "0,1,0.01")] public float shaftSunThreshold = 0.7f;
    // How much cloud occlusion contributes to SHAFT gating. 0 = shafts
    // are purely shaped by scene geometry (screen-space raymarch below);
    // 1 = cloud shadow composes with geometry, so both block shafts.
    // Cloud shadows on terrain / sprite / water are UNAFFECTED by this —
    // those come from cloud_shadow_ground in the other shaders.
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudShaftWeight = 1.0f;
    // Cloud-mask sharpness used SPECIFICALLY for shaft gating. Decoupled
    // from `cloudSharpness` above (which drives sky dome + terrain shadow
    // cloud sharpness) so you can get crisp beams without making cloud
    // shadows on the ground look hard-edged. 0 = soft gradient, 1 = hard
    // step at threshold. High values (0.9+) give Tessellator-style crisp
    // shaft boundaries.
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudShaftSharpness = 0.95f;
    // Half-angle (degrees) at which shafts begin fading toward zero as
    // the view ray aligns with the sun axis. Beams viewed along their
    // length foreshorten into radial dots — physically correct but
    // distracting; fading hides the transition. 90 disables the fade.
    [Export(PropertyHint.Range, "0,90,0.1")] public float shaftCameraFadeDegrees = 60.0f;

    [ExportGroup("Fog — Sun Shadow Raymarch (geometry-shaped beams)")]
    // Screen-space raymarch toward the sun at each fog march sample.
    // Samples depth_tex to check if tree/terrain/sprite geometry is
    // blocking the sun ray at that point. Produces the sharp canopy-
    // shaped beams the cloud-noise path cannot. Expensive — K depth
    // samples per fog step; disable for performance if needed.
    [Export] public bool sunShadowEnabled = true;
    [Export(PropertyHint.Range, "1,16,1")] public int sunShadowSteps = 6;
    [Export(PropertyHint.Range, "1,64,0.1")] public float sunShadowDistance = 16.0f;
    [Export(PropertyHint.Range, "0,0.01,0.0001")] public float sunShadowBias = 0.0005f;

    [ExportGroup("Fog — Shaft Shaping")]
    // Fraction of the view-ray march over which inscatter fades to 0 as
    // it approaches the terminating surface. Dust peaks at the ground
    // (dust_band_height gate is full when right above surface), so this
    // fade prevents shafts from painting color directly onto the ground
    // pixel — beams visually "float" above the terrain rather than
    // blanketing it. 0 = shafts paint onto ground; 0.5 = fade over the
    // last half of the march.
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float shaftGroundFade = 0.2f;

    [ExportGroup("Fog — Mote Shimmer (animated)")]
    // Animated visual noise that makes beams shimmer. Carries no scattering
    // density of its own (that's dustDensity above) — pure cosmetic motion.
    // Keep low by default — the sine-based noise has visible low-frequency
    // periodicity, so high strengths show as obvious striping inside shafts
    // when the sun is near-perpendicular to the view ray.
    [Export(PropertyHint.Range, "0,1,0.01")] public float moteStrength = 0.15f;
    [Export] public float moteScale = 0.18f;
    [Export] public Vector3 moteScroll = new Vector3(0.35f, 0.12f, -0.25f);

    public override void _Ready()
    {
        Current = this;
        // Seed ALL static globals here ONCE. The old code pushed them every
        // frame from _Process, which broke any runtime override. _Process
        // now only pushes genuinely dynamic values (sun direction).
        if (!Engine.IsEditorHint())
        {
            ShaderGlobals.Register("sun_world_dir", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(-0.215f, -0.819f, -0.532f));
            ShaderGlobals.Register("fill_world_dir", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Down);
        }
        Apply();
    }

    public override void _ExitTree()
    {
        if (Current == this) { Current = null; }
    }

    public override void _Process(double delta)
    {
        // Re-apply authored values every frame so inspector tweaks take
        // effect live — whether from the scene tab at edit time (via [Tool])
        // or from the Remote scene tree at runtime while debugging. Apply()
        // is ~15 shader-parameter sets, cheap enough to run unconditionally.
        //
        // This used to be conditional because CVar callbacks wrote the same
        // globals, and a per-frame Apply() would clobber CVar overrides.
        // All those CVars are gone now (values live on this node's exports),
        // so the conflict is gone too.
        Apply();

        // Sun direction: prefer the DirectionalLight3D node's transform
        // when one is wired so terrain shadows (Godot-native, driven by
        // the same node) and the fog shader's beams track the same
        // rotation. Fall back to WorldState.ShadowLightDirection (the
        // day/night sim) only when no node is assigned — and only at
        // runtime, since WorldState doesn't exist in the editor.
        Vector3 sunDir;
        if (sunLight != null)
        {
            // DirectionalLight3D emits light along its -Z (forward) axis.
            sunDir = -sunLight.GlobalTransform.Basis.Z.Normalized();
        }
        else if (!Engine.IsEditorHint())
        {
            sunDir = World.Current?.WorldState?.ShadowLightDirection ?? new Vector3(-0.215f, -0.819f, -0.532f);
        }
        else
        {
            // Editor with no light node wired — keep a stable default so
            // the preview doesn't flicker.
            sunDir = new Vector3(-0.215f, -0.819f, -0.532f);
        }
        Vector3 fillDir = ComputeFillDirection(sunDir, fillPitchDegrees, fillYawOffsetDegrees);
        RenderingServer.GlobalShaderParameterSet("sun_world_dir", sunDir);
        RenderingServer.GlobalShaderParameterSet("fill_world_dir", fillDir);
    }

    // Push every authored atmospheric value to the GPU. Call after weather
    // / time-of-day code mutates any of the Export fields on this node.
    public void Apply()
    {
        // --- Global uniforms ---------------------------------------------
        RenderingServer.GlobalShaderParameterSet("sun_color", ColorToVec3(sunColor));
        RenderingServer.GlobalShaderParameterSet("sun_ambient", sunAmbient);
        RenderingServer.GlobalShaderParameterSet("sun_tint_color", ColorToVec3(sunTintColor));
        RenderingServer.GlobalShaderParameterSet("fill_tint_color", ColorToVec3(fillTintColor));
        RenderingServer.GlobalShaderParameterSet("horizon_color", ColorToVec3(horizonColor));
        RenderingServer.GlobalShaderParameterSet("zenith_color", ColorToVec3(zenithColor));
        RenderingServer.GlobalShaderParameterSet("cloud_color", ColorToVec3(cloudColor));
        RenderingServer.GlobalShaderParameterSet("cloud_scroll", cloudScroll);
        RenderingServer.GlobalShaderParameterSet("cloud_threshold", cloudThreshold);
        RenderingServer.GlobalShaderParameterSet("cloud_sharpness", cloudSharpness);
        RenderingServer.GlobalShaderParameterSet("cloud_scale", cloudScale);
        RenderingServer.GlobalShaderParameterSet("cloud_altitude", cloudAltitude);
        RenderingServer.GlobalShaderParameterSet("cloud_shadow_strength", cloudShadowStrength);

        // --- Fog material uniforms ---------------------------------------
        if (fogMaterial != null)
        {
            fogMaterial.SetShaderParameter("fog_color", ColorToVec3(fogColor));
            fogMaterial.SetShaderParameter("fog_density", fogDensity);
            fogMaterial.SetShaderParameter("fog_max_distance", fogMaxDistance);
            fogMaterial.SetShaderParameter("fog_steps", fogSteps);
            fogMaterial.SetShaderParameter("dust_density", dustDensity);
            fogMaterial.SetShaderParameter("dust_band_height", dustBandHeight);
            fogMaterial.SetShaderParameter("dust_noise_strength", dustNoiseStrength);
            fogMaterial.SetShaderParameter("dust_noise_scale", dustNoiseScale);
            fogMaterial.SetShaderParameter("dust_noise_threshold", dustNoiseThreshold);
            fogMaterial.SetShaderParameter("dust_noise_sharpness", dustNoiseSharpness);
            fogMaterial.SetShaderParameter("dust_noise_scroll", dustNoiseScroll);
            fogMaterial.SetShaderParameter("sun_shaft_intensity", sunShaftIntensity);
            fogMaterial.SetShaderParameter("block_halo_intensity", blockHaloIntensity);
            fogMaterial.SetShaderParameter("scatter_anisotropy", scatterAnisotropy);
            fogMaterial.SetShaderParameter("shaft_sun_threshold", shaftSunThreshold);
            fogMaterial.SetShaderParameter("cloud_shaft_weight", cloudShaftWeight);
            fogMaterial.SetShaderParameter("cloud_shaft_sharpness", cloudShaftSharpness);
            fogMaterial.SetShaderParameter("shaft_camera_fade_degrees", shaftCameraFadeDegrees);
            fogMaterial.SetShaderParameter("sun_shadow_enabled", sunShadowEnabled);
            fogMaterial.SetShaderParameter("sun_shadow_steps", sunShadowSteps);
            fogMaterial.SetShaderParameter("sun_shadow_distance", sunShadowDistance);
            fogMaterial.SetShaderParameter("sun_shadow_bias", sunShadowBias);
            fogMaterial.SetShaderParameter("shaft_ground_fade", shaftGroundFade);
            fogMaterial.SetShaderParameter("mote_strength", moteStrength);
            fogMaterial.SetShaderParameter("mote_scale", moteScale);
            fogMaterial.SetShaderParameter("mote_scroll", moteScroll);
        }
    }

    private static Vector3 ComputeFillDirection(Vector3 sunDir, float pitchDeg, float yawOffsetDeg)
    {
        // Sun yaw from its horizontal travel direction.
        float sunYaw = Mathf.Atan2(sunDir.X, sunDir.Z);
        float fillYaw = sunYaw + Mathf.DegToRad(yawOffsetDeg);
        float pitch = Mathf.DegToRad(pitchDeg);
        float horiz = Mathf.Cos(pitch);
        Vector3 dir = new Vector3(horiz * Mathf.Sin(fillYaw), -Mathf.Sin(pitch), horiz * Mathf.Cos(fillYaw));
        return dir.Normalized();
    }

    private static Vector3 ColorToVec3(Color c)
    {
        return new Vector3(c.R, c.G, c.B);
    }
}
