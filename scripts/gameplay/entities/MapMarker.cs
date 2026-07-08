using Godot;

// Composable child that puts a landmark on the world map + minimap. Drop into an
// interactive scene (campfire, forge, ...) for objects that should be charted by
// MAP REVEAL rather than perception — the reveal-gated sibling of Discoverable
// (the two can coexist on one host).
//
// The node is PASSIVE: no per-frame tick. It registers with World on _Ready and
// deregisters on TreeExiting, exposing its position + display data + identify
// config. Discovery is driven CENTRALLY by the Minimap at its reveal cadence: the
// Minimap marks this Sensed once the fog clears over WorldPosition, and — in
// Proximity mode — Identified once the player is within identifyRadius. Perception
// mode chains to a sibling Discoverable; Interaction mode waits for the host to
// call Identify(). All discovery is recorded into the two-tier party Knowledge
// (active member's store now, banked at the next campfire) via WorldSimState.
[GlobalClass]
public partial class MapMarker : Node3D
{
    // Icon drawn on the maps once Identified. Until then the maps draw a shared
    // "?" (renderer-owned) — an unidentified landmark can't reveal its silhouette.
    [Export] public Texture2D icon;
    // Name shown on hover once Identified (StringName-as-display-text, same
    // convention as RegionData.displayName — routed through Loc later).
    [Export] public StringName displayName;
    // How this marker climbs Sensed -> Identified. The Sensed step is always
    // map-reveal; this governs only the next step up.
    [Export] public EMapMarkerIdentifyMode identifyMode = EMapMarkerIdentifyMode.Proximity;
    // Proximity mode: identify once the player is within this many meters. Ignored
    // by Perception / Interaction modes.
    [Export(PropertyHint.Range, "0,64,0.5,or_greater")] public float identifyRadius = 12f;
    // Perception mode: the sibling Discoverable whose Discovered state identifies
    // this marker. Wire it in the scene; leave null for the other modes.
    [Export] private Discoverable _discoverable;

    // Two-state (active/inactive) appearance driven by LIVE host state — currently
    // the campfire's lit/unlit. When true the maps tint the Identified icon by
    // activeModulate while the host is active, iconModulate otherwise. The state is
    // read live (WorldSimState.IsMarkerActive) so it updates even when the host's
    // chunk is unloaded. Leave false for single-state markers.
    [Export] public bool hasActiveState = false;
    // Tint on the Identified icon in the inactive (default) state; White = untinted.
    // For a campfire this is the "unlit" (cool) tint.
    [Export] public Color iconModulate = Colors.White;
    // Tint on the Identified icon while the host is active (campfire lit). Only used
    // when hasActiveState is true.
    [Export] public Color activeModulate = Colors.White;

    public Texture2D Icon => icon;
    public StringName DisplayName => displayName;
    public EMapMarkerIdentifyMode IdentifyMode => identifyMode;
    public float IdentifyRadius => identifyRadius;
    public bool HasActiveState => hasActiveState;
    public Color IconModulate => iconModulate;
    public Color ActiveModulate => activeModulate;
    public Vector3 WorldPosition => GlobalPosition;

    private World _world;

    public override void _Ready()
    {
        _world = World.Current;
        if (_world != null)
        {
            _world.onMapMarkerSpawned?.Invoke(this);
            TreeExiting += () =>
            {
                _world.onMapMarkerRemoved?.Invoke(this);
                if (_discoverable != null)
                {
                    _discoverable.OnStateChanged -= OnDiscoverableStateChanged;
                }
            };
        }
        if (identifyMode == EMapMarkerIdentifyMode.Perception && _discoverable != null)
        {
            _discoverable.OnStateChanged += OnDiscoverableStateChanged;
        }
    }

    private void OnDiscoverableStateChanged(EPlayerPerceptionState state)
    {
        if (state == EPlayerPerceptionState.Discovered)
        {
            Identify();
        }
    }

    // Promote this marker straight to Identified in the active member's knowledge.
    // Called by the sibling Discoverable (Perception mode) or by the host on
    // interaction (Interaction mode). Skipping Sensed is intentional — actually
    // seeing / interacting with a landmark means you know both that it exists and
    // what it is. No-op if already Identified+.
    public void Identify()
    {
        _world?.WorldState?.SimState?.RecordMarker(WorldPosition, EMapMarkerLevel.Identified, this);
    }
}
