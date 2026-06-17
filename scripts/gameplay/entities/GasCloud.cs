using Godot;

// A volumetric hazard that hangs in the world, optionally for a fixed
// duration, and damages anything inside its danger zone. The canonical use
// is a poison cloud spawned by a trap or shattered vial, but the same node
// also serves boss-spawned acid pools or environmental fumaroles —
// composition (start fx, persistent fx, danger zone shape, damage data)
// lives in the .tscn as instanced child scenes, so the whole hazard is
// authored in one scene the artist can open and tune in context.
[GlobalClass]
public partial class GasCloud : Node3D
{
    // Seconds the cloud lives before removing itself. <= 0 means it persists
    // until something else frees the node (a TriggerSource, a vent
    // controller, etc.).
    [Export] public float lifetimeSeconds = 0f;

    // Area3D that does the periodic damage ticking. Wired in the .tscn;
    // disabled when the cloud expires so the final free frame doesn't deal
    // a stray tick after the visuals are gone.
    [Export] private DamageZone _dangerZone;

    // GameTimeMs at which the cloud expires, armed on the first tick. On the sim
    // clock (not wall-clock _Process frames) so the lifetime that bounds the
    // damage zone slows with slow-mo and matches the codebase's duration
    // convention.
    private ulong _expireTimeMs;
    private bool _armed;

    // Applies weapon-authored overrides from a SpawnAreaEffect ItemEvent.
    // `continuous` and `intervals` are pre-resolved by the caller (looked
    // up against the firing entity's continuousProfiles / damageProfiles)
    // since GasCloud doesn't know about WeaponState. Must be called BEFORE
    // the cloud is added to the scene tree — both this and
    // DamageZone.OverrideAuthoring mutate fields that the targets read in
    // their own _Ready, so AddChild must come last. Each field is skipped
    // when its source leaves it at the default sentinel (null /
    // non-positive), so a partial override is fine.
    public void Initialize(
        ItemEvent ev,
        ContinuousDamageData continuous,
        Godot.Collections.Array<IntervalDamageEntry> intervals,
        ETeam attackerTeam)
    {
        if (ev == null)
        {
            return;
        }
        if (ev.areaDurationSeconds > 0f)
        {
            lifetimeSeconds = ev.areaDurationSeconds;
        }
        // Actor-spawned weapon AoE: hand the firing actor's team to the danger
        // zone and switch off its environmental "hit everyone" default, so the
        // shared CanDamage rule spares the firer and their allies. (Trap-driven
        // clouds via GasCloudDeployer never call this, keeping friendlyFire.)
        if (_dangerZone != null)
        {
            _dangerZone.attackerTeam = attackerTeam;
            _dangerZone.friendlyFire = false;
        }
        _dangerZone?.OverrideAuthoring(continuous, intervals, ev.areaRadius);
    }

    // Channeled-zone variant used by the summoner weapon's ActionRunner. The
    // zone's continuous/interval damage is authored in the scene; the runner
    // only stamps the caster's team (so the channel spares the caster + allies
    // and hits enemies) and an optional radius override. Like Initialize, must
    // be called BEFORE AddChild — DamageZone reads these fields in its _Ready.
    // The runner owns the lifetime (frees the node on charge end), so
    // lifetimeSeconds stays at its authored 0 (persist).
    public void InitializeChannel(ETeam attackerTeam, float radius)
    {
        if (_dangerZone != null)
        {
            _dangerZone.attackerTeam = attackerTeam;
            _dangerZone.friendlyFire = false;
            _dangerZone.OverrideAuthoring(null, null, radius);
        }
    }

    public override void _Process(double delta)
    {
        if (lifetimeSeconds <= 0f)
        {
            return;
        }
        World world = World.Current;
        if (world == null)
        {
            return;
        }
        if (!_armed)
        {
            _expireTimeMs = world.GameTimeMs + (ulong)(lifetimeSeconds * 1000f);
            _armed = true;
        }
        if (world.GameTimeMs >= _expireTimeMs)
        {
            Expire();
        }
    }

    private void Expire()
    {
        if (_dangerZone != null)
        {
            _dangerZone.SetActive(false);
        }
        QueueFree();
    }
}
