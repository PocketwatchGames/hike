using Godot;

// Authored template for a lightning strike. Referenced from
// SimData.weatherLightning (weather-driven) and from any future
// weapon/spell that wants to call LightningStrike.Create at an
// arbitrary world position.
//
// One strike has three observable parts:
//   1. WARNING — a localized fx (particles + audio fade-in) at the
//      strike location for `warningDurationSeconds`, telegraphing
//      the imminent hit.
//   2. STRIKE  — instantaneous: bolt flashes on, world directional
//      lights brighten via LightningFlasher.TriggerFlash, a strike
//      fx (thunder crack + sparks) plays at the spot, every HurtBox
//      within `damageRadiusMeters` takes a Hit built from `damage`,
//      and the Hud's full-screen white overlay flashes proportional
//      to the player's distance from the strike.
//   3. FADE   — bolt sprite fades out over `boltFadeSeconds`, then
//      the LightningStrike node frees itself.
//
// Weather-spawn fields drive WeatherLightningSpawner only — weapon
// or scripted callers ignore them.
[GlobalClass]
public partial class LightningData : Resource
{
    // ------- Damage --------

    // Hit payload applied to every HurtBox within damageRadiusMeters
    // at the moment of strike. Knockback direction is hard-coded
    // upward (lightning slams down — a lateral push wouldn't read).
    [Export] public DamageData damage;

    // Radius (meters) of the sphere overlap query around the strike
    // ground point. Tune to roughly the bolt's visible footprint —
    // small enough to feel like a direct hit, big enough to punish
    // standing under one.
    [Export(PropertyHint.Range, "0.5,20,0.1")] public float damageRadiusMeters = 3f;

    // ------- Timing --------

    // Seconds between strike spawn and the strike going off. The
    // warning fx runs for this whole window so the player has time
    // to dodge.
    [Export(PropertyHint.Range, "0,5,0.05")] public float warningDurationSeconds = 0.7f;

    // Seconds the bolt sprite holds at full opacity after strike.
    // Short — the bolt is a flicker, not a sustained beam.
    [Export(PropertyHint.Range, "0,1,0.01")] public float boltVisibleSeconds = 0.12f;

    // Seconds the bolt sprite fades from full opacity to invisible.
    // The node frees itself at the end of this window.
    [Export(PropertyHint.Range, "0,2,0.01")] public float boltFadeSeconds = 0.35f;

    // ------- Visuals --------

    // Local effect that plays during the warning window — particles
    // that fade in + a low rumble. Spawned via Fx.Create as a child
    // of the strike node so it tracks its position.
    [Export] public PackedScene warningFx;

    // Local effect that plays at strike — sparks + thunder crack.
    // Spawned at the moment of strike.
    [Export] public PackedScene strikeFx;

    // Peak amplitude fed to LightningFlasher.TriggerFlash at strike
    // moment. 1.0 = a full world-brightness flash; lower for
    // dimmer/less-overhead strikes.
    [Export(PropertyHint.Range, "0,1,0.01")] public float flashAmplitude = 1f;

    // ------- Screen flash --------

    // Peak alpha (0..1) of the full-screen white overlay when the
    // player is standing exactly at the strike. Falls off with
    // distance per `screenFlashFalloffMeters`.
    [Export(PropertyHint.Range, "0,1,0.01")] public float screenFlashMaxIntensity = 0.85f;

    // Distance (meters) at which the screen flash decays to zero.
    // Linear falloff: alpha = max * clamp(1 - dist/falloff, 0, 1).
    // Tune so distant strikes contribute nothing while a strike
    // landing near the player blows out the screen.
    [Export(PropertyHint.Range, "1,200,0.5")] public float screenFlashFalloffMeters = 35f;

    // Seconds for the screen flash to fade from peak intensity to
    // zero after the strike. Short — should feel like an
    // afterimage, not a held-on white wash.
    [Export(PropertyHint.Range, "0.01,2,0.01")] public float screenFlashFadeOutSeconds = 0.4f;

    // ------- Weather-driven spawn cadence (ignored by weapon callers) --------

    // STORM GATE: applied to AmbienceState.DestinationLightningIntensity
    // (end of current variance crossfade), NOT the in-flight displayed
    // intensity. Should match ThunderScheduler's intensityFloor so
    // strikes and ambient thunder fire together — no thunder without
    // strikes, no strikes without thunder. Transient mid-crossfade
    // blips on non-storm variance values stay below the destination
    // floor and don't trigger either.
    [Export(PropertyHint.Range, "0,1,0.01")] public float weatherSpawnIntensityFloor = 0.10f;

    // Intensity value at which the cadence reaches atPeak. Realistic
    // weather intensity is lightningMax * cloud/rain smoothstep gate
    // * variance — typical heavy storms peak around 0.2–0.3 even
    // though the simulation field is nominally [0, 1]. Setting this
    // to a realistic ceiling rather than 1.0 means moderate storms
    // actually hit the short-interval end of the curve.
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float weatherSpawnIntensityForPeak = 0.3f;

    // Mean inter-strike interval (seconds) at intensity = peak.
    // Sampled around exponentially for Poisson-like cadence. Default
    // 4s means a heavy storm rolls a strike every few seconds.
    [Export(PropertyHint.Range, "0.5,60,0.1")] public float weatherSpawnIntervalAtPeak = 4f;

    // Mean inter-strike interval (seconds) at the intensity floor.
    // A weak storm should still land one occasionally — long enough
    // to feel like punctuation, short enough that the player notices
    // weather has teeth.
    [Export(PropertyHint.Range, "5,600,1")] public float weatherSpawnIntervalAtFloor = 25f;

    // Strikes land in an annulus around the player. Min keeps a
    // small no-strike-here ring so the player isn't instantly hit
    // on spawn; max is the visible range — pick to match the
    // ENTITY_LOAD_RADIUS in chunks so strikes don't pop in past
    // loaded geometry.
    [Export(PropertyHint.Range, "0,40,0.5")] public float weatherSpawnRadiusMinMeters = 6f;
    [Export(PropertyHint.Range, "1,80,0.5")] public float weatherSpawnRadiusMaxMeters = 30f;
}
