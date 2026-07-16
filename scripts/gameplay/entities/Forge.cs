using System;
using Godot;

// Smithing forge. On interact (while off cooldown) it opens the ForgeScreen
// offering a single slot-locked "upgrade" (a StatusEffectData with a non-None
// upgradeSlot) drawn from SimData.forgeUpgrades. Accepting applies the upgrade at
// this forge's Level — evicting whatever occupies that slot — and the forge goes
// inert until the next in-world sunrise (a sim-clock deadline persisted on the sim
// state so the cooldown survives chunk streaming and save/load). The upgrade
// effects are authored as sunrise-expiring (durationType TimeOfDay), so they last
// exactly one day, matching the forge's daily re-arm.
//
// The offered upgrade is chosen deterministically from (world position, day), so
// the model hovering over the forge always previews what the player will get.
// Instead of a single orb, the forge floats the model of the offered slot (melee
// sword / ranged bow / armor shield), glowing purple while ready and darkened once
// used.
//
// Distinct from the Campfire cooking station: no lit/doused state, no jobs.
[GlobalClass]
public partial class Forge : Node3D, IInteractive, IWorldEntity
{
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    [Export] private Discoverable _discoverable;
    [Export] private Node3D _hudNode;

    // Hovering purple voxel light: glows while the forge is ready, fades once used.
    [Export] private StationaryLight _light;
    // Voxels above the forge origin at which the orb light is deposited, so the
    // glow centers on the hovering model rather than the pedestal base.
    [Export] private int _orbLightHeight = 3;

    // Slot models — one per upgrade slot, only the offered slot's model is shown.
    // The pivot spins + bobs; the visible model swaps every descendant mesh's
    // material override between the active (purple emissive) and inert (darkened)
    // variants for its atlas. These are Node3D holders (the FBX may split into
    // several MeshInstance3D — the sword blade + scabbard, the skinned bow).
    [Export] private Node3D _modelPivot;
    [Export] private Node3D _modelMelee;   // sword
    [Export] private Node3D _modelRanged;  // bow
    [Export] private Node3D _modelArmor;   // shield

    // Per-level pedestal (station) models, indexed by the forge's Level (0..N). Only
    // the one matching this forge's tier is shown, so a level-5 forge looks grander
    // from a distance than a level-1. Index clamped, so fewer entries than tiers just
    // reuses the top pedestal. Empty = whatever pedestal the scene leaves visible.
    [Export] private Godot.Collections.Array<Node3D> _levelPedestals = new();
    // Active/inert material pairs. The melee/ranged models share one atlas
    // (PolysplitGames); the armor shield uses its own (PolygonDungeon).
    [Export] private Material _slotActiveMaterial;
    [Export] private Material _slotInertMaterial;
    [Export] private Material _armorActiveMaterial;
    [Export] private Material _armorInertMaterial;

    // Presentational hover: height amplitude (m) + cycles/second, and spin speed.
    [Export] private float _bobAmplitude = 0.12f;
    [Export] private float _bobHz = 0.4f;
    [Export] private float _spinDegreesPerSecond = 45f;

    private ForgeSimState _simState;
    private World _world;
    private bool _visualReady = true;
    private float _pivotBaseY;
    private float _bobTime;

    // The upgrade this forge currently offers (and previews as a floating model),
    // resolved deterministically from position + the next-usable day.
    private StatusEffectData _offeredUpgrade;

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

    // Star pips on the interact HUD reflect the forge's power tier (0-4); a
    // level-0 forge shows no pips.
    public int InteractLevel => _simState?.Level ?? 0;

    public void OnSpawned(World world) { }

    public override void _Ready()
    {
        if (_modelPivot != null)
        {
            _pivotBaseY = _modelPivot.Position.Y;
        }
    }

    public override void _ExitTree()
    {
        if (_world != null)
        {
            _world.OnNewDay -= HandleNewDay;
        }
        if (_discoverable != null)
        {
            _discoverable.OnStateChanged -= HandleDiscoveryChanged;
        }
    }

    // True until a Discoverable is wired and reports Hidden — an unwired forge is
    // treated as always visible.
    private bool IsDiscovered => _discoverable == null || _discoverable.IsDiscovered;

    // The floating model + pedestals dither in with discovery via the Discoverable
    // mesh fade; the orb light isn't a mesh, so gate it here too — relight (or
    // snuff) when discovery flips, honoring the current ready state, so its glow
    // doesn't reveal the forge before the player perceives it.
    private void HandleDiscoveryChanged(EPlayerPerceptionState state)
    {
        _light?.SetActive(_visualReady && IsDiscovered, true);
    }

    // Wall-clock spin + bob only — a purely presentational hover, so it stays
    // smooth at render fps and doesn't drag under slow-mo. Ready/inert state is
    // event-driven (use + OnNewDay), not polled here.
    public override void _Process(double delta)
    {
        if (_modelPivot == null)
        {
            return;
        }
        _bobTime += (float)delta;
        Vector3 pos = _modelPivot.Position;
        pos.Y = _pivotBaseY + Mathf.Sin(_bobTime * Mathf.Tau * _bobHz) * _bobAmplitude;
        _modelPivot.Position = pos;
        _modelPivot.RotateY(Mathf.DegToRad(_spinDegreesPerSecond) * (float)delta);
    }

    // The forge re-arms at sunrise; re-roll the offer (a new day picks a new
    // upgrade) and re-evaluate the glow when the day rolls over.
    private void HandleNewDay(int day)
    {
        RefreshOffer();
        ApplyReadyVisual(CanInteract());
    }

    // Pick the offered upgrade for the day the forge is next usable — today while
    // ready, tomorrow while inert — so the floating model previews what you'll
    // actually receive. Deterministic in (position, day) so it's stable across
    // streaming / reload and changes only when the forge re-arms.
    private void RefreshOffer()
    {
        int today = World.Current?.DayNumber ?? 0;
        _offeredUpgrade = _simState == null
            ? null
            : ForgeOffer.Resolve(_world?.SimData?.forgeUpgrades, _simState.WorldPosition, today, _simState.RegrowDay, _simState.Slot);
        ApplyOfferModel();
    }

    // The forge's fixed slot (authored on the spawn entry, or position-derived),
    // resolved at bake time onto ForgeSimState.Slot. The offered upgrade rolls daily
    // among those eligible for it, but the slot — and thus the floating model / marker
    // icon — is constant for this forge.
    private EUpgradeSlot OfferedSlot => _simState?.Slot ?? EUpgradeSlot.None;

    // Show only the model for the offered slot; reapply its ready/inert material.
    private void ApplyOfferModel()
    {
        EUpgradeSlot slot = OfferedSlot;
        SetModel(_modelMelee, slot == EUpgradeSlot.Melee);
        SetModel(_modelRanged, slot == EUpgradeSlot.Ranged);
        SetModel(_modelArmor, slot == EUpgradeSlot.Armor);
        ApplyModelMaterial(_visualReady);
    }

    private static void SetModel(Node3D model, bool shown)
    {
        if (model != null)
        {
            model.Visible = shown;
        }
    }

    // Show only the pedestal for this forge's level (clamped to the authored list),
    // hiding the rest. No-op when no per-level pedestals are wired.
    private void ApplyLevelPedestal()
    {
        if (_levelPedestals == null || _levelPedestals.Count == 0)
        {
            return;
        }
        int lvl = Mathf.Clamp(_simState?.Level ?? 0, 0, _levelPedestals.Count - 1);
        for (int i = 0; i < _levelPedestals.Count; i++)
        {
            if (_levelPedestals[i] != null)
            {
                _levelPedestals[i].Visible = i == lvl;
            }
        }
    }

    // The holder + material pair for the currently-offered slot (armor uses its own
    // atlas materials; the rest share one). Null holder when no slot is offered.
    private void ResolveOfferedModel(out Node3D holder, out Material active, out Material inert)
    {
        switch (OfferedSlot)
        {
            case EUpgradeSlot.Melee:
                holder = _modelMelee; active = _slotActiveMaterial; inert = _slotInertMaterial; return;
            case EUpgradeSlot.Ranged:
                holder = _modelRanged; active = _slotActiveMaterial; inert = _slotInertMaterial; return;
            case EUpgradeSlot.Armor:
                holder = _modelArmor; active = _armorActiveMaterial; inert = _armorInertMaterial; return;
            default:
                holder = null; active = null; inert = null; return;
        }
    }

    private void ApplyModelMaterial(bool ready)
    {
        ResolveOfferedModel(out Node3D holder, out Material active, out Material inert);
        if (holder != null)
        {
            ApplyOverrideRecursive(holder, ready ? active : inert);
        }
    }

    // MaterialOverride replaces every surface, so an FBX that split into several
    // MeshInstance3D (sword blade + scabbard, skinned bow) needs the override on
    // each. Walks the holder subtree.
    private static void ApplyOverrideRecursive(Node node, Material material)
    {
        if (node is MeshInstance3D mesh)
        {
            mesh.MaterialOverride = material;
        }
        foreach (Node child in node.GetChildren())
        {
            ApplyOverrideRecursive(child, material);
        }
    }

    // Purple flickering glow + emissive model while ready; light out + darkened
    // model once used. fade=false snaps (spawn/streaming) so a loading forge
    // doesn't flare up.
    private void ApplyReadyVisual(bool ready, bool fade = true)
    {
        if (ready == _visualReady && fade)
        {
            return;
        }
        _visualReady = ready;
        // Light stays dark until discovered even while ready (see HandleDiscoveryChanged).
        _light?.SetActive(ready && IsDiscovered, fade);
        ApplyModelMaterial(ready);
    }

    public bool CanInteract()
    {
        // Inert until the day advances past the reactivation day (stamped to the
        // next sleep-to-sunrise on use). 0 = ready.
        int today = World.Current?.DayNumber ?? 0;
        return _simState == null || today >= _simState.RegrowDay;
    }

    public bool CanActorInteract(Player player)
    {
        return CanInteract() && (_discoverable == null || _discoverable.IsDiscovered);
    }

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        if (!CanActorInteract(player))
        {
            return null;
        }
        return _actions != null && _actions.Count > 0 ? _actions : null;
    }

    public void Complete(int actionIndex)
    {
        if (!CanInteract() || _offeredUpgrade == null)
        {
            return;
        }
        GameClient gc = GameClient.Current;
        Player player = gc?.Player;
        if (gc == null || player == null)
        {
            return;
        }
        StatusEffectData offered = _offeredUpgrade;
        int level = _simState?.Level ?? 0;
        EUpgradeSlot slot = OfferedSlot;
        StatusEffectData replacing = player.ActiveUpgrade(slot);
        int replacingLevel = player.ActiveUpgradeLevel(slot);
        gc.OpenForgeScreen(offered, replacing, level, replacingLevel, () =>
        {
            player.AddStatusEffect(offered, level, slot);
            BeginCooldown();
        });
    }

    private void BeginCooldown()
    {
        if (_simState == null)
        {
            return;
        }
        _simState.RegrowDay = (World.Current?.DayNumber ?? 0) + 1;
        // Keep the map-marker tint cache current so the icon dims immediately and
        // stays dim while this chunk is unloaded.
        _world?.WorldState?.SimState?.SetForgeReactivate(_simState.WorldPosition, _simState.RegrowDay, _simState.Level, _simState.Slot);
        // Snuff the glow immediately and preview tomorrow's offer (darkened);
        // HandleNewDay relights it at the next sunrise.
        RefreshOffer();
        ApplyReadyVisual(false);
    }

    public static Forge Create(World world, ForgeSimState data)
    {
        var instance = data.Scene.Instantiate<Forge>();
        instance.Position = data.WorldPosition;
        instance._simState = data;
        instance._world = world;
        var lightPos = new Vector3I(
            Mathf.FloorToInt(data.WorldPosition.X),
            Mathf.FloorToInt(data.WorldPosition.Y) + instance._orbLightHeight,
            Mathf.FloorToInt(data.WorldPosition.Z)
        );
        instance._light?.Initialize(world.WorldState, world, lightPos);
        // Register this forge's cooldown deadline so its map marker tints
        // ready/inert even while the chunk is unloaded (mirrors LitCampfire).
        world.WorldState?.SimState?.SetForgeReactivate(data.WorldPosition, data.RegrowDay, data.Level, data.Slot);
        world.AddChild(instance);
        // Pick the pedestal for this forge's tier (bigger station at higher levels).
        instance.ApplyLevelPedestal();
        // Resolve the offered upgrade + model, then snap to the ready/inert state
        // (no fade on stream-in) and relight on the sunrise rollover.
        instance.RefreshOffer();
        instance.ApplyReadyVisual(instance.CanInteract(), fade: false);
        world.OnNewDay += instance.HandleNewDay;
        // Relight the orb once the player discovers the forge (starts Hidden).
        if (instance._discoverable != null)
        {
            instance._discoverable.OnStateChanged += instance.HandleDiscoveryChanged;
        }
        return instance;
    }
}
