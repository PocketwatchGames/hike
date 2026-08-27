using System.Collections.Generic;
using Godot;

// World-map tab rendered inside AlmanacScreen. The Almanac wrapper owns
// InputSuppressed / hud-visibility / ui_cancel handling; this screen renders
// and drives its own pan / zoom while its tab is visible.
//
// TWO ZOOM LEVELS, EACH WITH ITS OWN RENDER TARGET. Both SubViewports run the
// same minimap shader the HUD uses, at fixed but different resolutions:
//   Overview — `overviewViewMeters` of world across, capped at the world's
//              own extent, linearly filtered and antialiased.
//   Detail   — a window around the pan center at `detailPixelsPerMeter`, sized
//              so it displays 1:1 when fully zoomed in.
// Rendering at a FIXED resolution and magnifying with a nearest filter is the
// point of the split: it is what gives the map an authored pixel size, and what
// leaves several screen pixels per voxel for the per-voxel border lines the
// detail view is meant to draw. Pointing the shader straight at the panel
// instead re-renders at whatever size the panel happens to be, so there is no
// pixel grid to hang a one-pixel line on — the same reason WorldMapCanvas
// magnifies by an integer instead of fitting its image to the control.
//
// Zooming animates ONE framing (center + view radius) that BOTH layers are laid
// out against, so each texture is drawn at the size of the world area it
// actually covers. The two stay registered on each other through the whole
// transition and only their alphas cross-fade.
//
// The circular alpha mask in the shader is disabled via the materials
// (mask_radius = 0.7071, softness = 0). Both layers live inside an
// AspectRatioContainer (ratio 1.0) because the shader's view radius is
// isotropic — a non-square rect would stretch the world.
//
// Region-name labels and marker icons are overlaid at panel scale, never as
// children of the map layers: those are scaled to the zoom and would magnify
// the icons with them. They project with the same UV math the shader uses, so
// they stay locked to the terrain as the view pans, zooms and resizes.
[GlobalClass]
public partial class WorldMapScreen : Control
{
	// Square, un-rotated, clips its contents. Hosts both map layers plus the
	// label / marker overlays; everything below is positioned in its local px.
	[Export] public Control mapView;

	[ExportGroup("Overview Layer")]
	[Export] public SubViewport overviewViewport;
	// Full-rect ColorRect inside overviewViewport carrying the minimap
	// ShaderMaterial. The shader is UV-driven and samples no TEXTURE, so a
	// ColorRect is all it needs.
	[Export] public ColorRect overviewSurface;
	[Export] public TextureRect overviewRect;
	// World meters per texel of the overview render. Bigger = chunkier.
	[Export(PropertyHint.Range, "0.25,32,0.25,or_greater")] public float overviewMetersPerPixel = 1.25f;
	// World distance across the zoomed-OUT view, in METRES — an absolute scale,
	// not a fraction of the world. A fraction made the overview mean something
	// different in every world: the same setting is a 288 m read on the shipped
	// map and a 5000 m one on a large map, so it could not be authored against.
	//
	// It also pins the render target, which a fraction could not: the target is
	// `overviewViewMeters / overviewMetersPerPixel`, both absolute, so its size
	// is the same in every world instead of growing with the world until it hit
	// the maxViewportPixels clamp.
	//
	// CAPPED at the world's own extent, so the overview never zooms out past the
	// map into void — which means that on a world smaller than this, raising it
	// does nothing and the view simply fits the world.
	[Export(PropertyHint.Range, "32,8192,16,or_greater")] public float overviewViewMeters = 512f;

	[ExportGroup("Detail Layer")]
	[Export] public SubViewport detailViewport;
	[Export] public ColorRect detailSurface;
	[Export] public TextureRect detailRect;
	// Texels per world meter of the zoomed-in render. The detail viewport is
	// sized to the panel, so this also sets how much world the zoomed-in view
	// shows: panelPixels / detailPixelsPerMeter meters across.
	//
	// A free float — nothing wants it integral or a power of two. The map is
	// sampled through a 45° rotation, so a voxel never maps to an axis-aligned
	// block of texels whatever this is; the layer displays 1:1, so there is no
	// magnification step to keep integral; and the step lines are measured in
	// output pixels rather than in cells. Raising it costs no memory either,
	// since the target is the panel's size regardless.
	[Export(PropertyHint.Range, "0.25,32,0.25,or_greater")] public float detailPixelsPerMeter = 15f;

	[ExportGroup("Zoom / Pan")]
	[Export(PropertyHint.Range, "0.05,2,0.01")] public float zoomTransitionSeconds = 0.25f;
	// Pan rate as a fraction of the visible width per second, so it feels the
	// same however far the view is zoomed in.
	[Export(PropertyHint.Range, "0.1,4,0.05")] public float panScreensPerSecond = 0.9f;
	// Safety cap on either render target's edge length.
	[Export(PropertyHint.Range, "128,4096,1")] public int maxViewportPixels = 2048;

	[ExportGroup("Overlays")]
	// Overlay control sized to mapView's rect; region labels are added as
	// children and positioned in local pixels each frame.
	[Export] public Control regionLabels;
	[Export(PropertyHint.Range, "8,64,1")] public int regionLabelFontSize = 24;
	// Shared "?" icon drawn for Sensed (unidentified) map markers. Optional —
	// null falls back to a drawn "?" glyph. Identified markers use their own icon.
	[Export] public Texture2D unknownMarkerIcon;
	[Export(PropertyHint.Range, "8,96,1")] public int markerIconSize = 28;

	[ExportGroup("Treasure Maps")]
	// Switches the panel between the world map (item 0) and each collected
	// treasure map. Populated from SimState.TreasureMaps.
	[Export] public OptionButton mapSelector;
	// Zoom used for a treasure map — a small view radius so the marked area reads
	// close-up, versus the radius the world map computes for itself.
	[Export(PropertyHint.Range, "16,200,1")] public float treasureMapViewRadiusMeters = 48f;
	// Icon drawn at the dig spot (map center) on a treasure map. Null = a drawn red X.
	[Export] public Texture2D treasureXIcon;

	// World-sampling spin (radians) that puts game-north (−X,−Z) at the top of
	// the map. +X is screen-right and +Z screen-down in the shader's unrotated
	// frame, so the (−X,−Z) diagonal starts at the upper-left; −π/4 rotates it
	// to straight up. Fed to the shader's map_rotation and mirrored by the label
	// / marker projection below so all three stay locked together.
	const float NorthMapRotation = -Mathf.Pi / 4f;

	// Smallest render target we will ask for, so a degenerate panel size cannot
	// produce a zero-area viewport.
	const int MinViewportPixels = 16;

	// Banked (party-only) markers for the zoomed-in view; the zoomed-out view
	// shows the lit campfire alone so the whole-world read stays uncluttered.
	MapMarkerOverlay _overviewMarkers;
	MapMarkerOverlay _detailMarkers;

	// Full-rect overlay drawing the dig-spot X, shown only in treasure-map mode.
	TreasureXMarker _xMarker;

	GameClient _gameClient;

	public void Initialize(GameClient gameClient)
	{
		_gameClient = gameClient;
	}

	// Textures already pushed to one layer's material, so an unchanged frame
	// costs no managed/native crossings. One set per layer: the two materials
	// are distinct objects and hold their uniforms independently.
	class BoundStateTextures
	{
		public Texture2D Surface, SurfaceBelow1, SurfaceBelow2;
		public Texture2D Exploration, ExplorationBelow1, ExplorationBelow2;
	}
	class LayerBinding
	{
		public Texture2D TileLut;
		public readonly BoundStateTextures A = new();
		public readonly BoundStateTextures B = new();
	}
	readonly LayerBinding _overviewBind = new();
	readonly LayerBinding _detailBind = new();

	// Lazy-created on first visible frame, once WorldState is available.
	readonly Dictionary<RegionData, Label> _labels = new();

	// Rebuild the selector only when the collected-map count changes.
	int _selectorMapCount = -1;

	// false = overview, true = detail. `_zoomBlend` chases it 0 → 1.
	bool _zoomedIn;
	float _zoomBlend;
	// Where the player has panned to, as an offset from the world center, in MAP
	// space (the north-rotated frame the textures are drawn in), meters. Kept in
	// map space because that is the frame pan input arrives in — screen up is −Y
	// here, with no rotation to apply. Stored at the DETAIL level's freedom; each
	// level re-clamps it to its own radius when it draws.
	Vector2 _panOffsetMap;
	// Cleared on open so the first frame with a live world centers on the player.
	bool _panPrimed;

	public override void _Ready()
	{
		Visible = false;
		// This modal is Tab/controller-navigated with no mouse-driven focus, so the
		// selector is unreachable unless we hand it focus when the tab is shown.
		VisibilityChanged += OnVisibilityChanged;
		if (overviewRect != null && overviewViewport != null)
		{
			overviewRect.Texture = overviewViewport.GetTexture();
		}
		if (detailRect != null && detailViewport != null)
		{
			detailRect.Texture = detailViewport.GetTexture();
		}
	}

	void OnVisibilityChanged()
	{
		if (!Visible)
		{
			SetViewportActive(overviewViewport, false);
			SetViewportActive(detailViewport, false);
			return;
		}
		if (mapSelector != null)
		{
			// Deferred: the control cannot take focus in the same frame its
			// visibility flips on.
			mapSelector.CallDeferred(Control.MethodName.GrabFocus);
		}
		// Always open on the whole world; the detail view re-centers on the
		// player so the first zoom-in lands where the party is.
		_zoomedIn = false;
		_zoomBlend = 0f;
		_panPrimed = false;
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible || e is not InputEventMouseButton mb || !mb.Pressed)
		{
			return;
		}
		if (mb.ButtonIndex == MouseButton.WheelUp)
		{
			_zoomedIn = true;
		}
		else if (mb.ButtonIndex == MouseButton.WheelDown)
		{
			_zoomedIn = false;
		}
		else
		{
			return;
		}
		GetViewport().SetInputAsHandled();
	}

	public override void _Process(double delta)
	{
		if (!Visible)
		{
			return;
		}
		Minimap minimap = _gameClient?.Sim?.Minimap;
		if (minimap == null || mapView == null || minimap.StateB.Surface == null)
		{
			return;
		}
		if (overviewSurface?.Material is not ShaderMaterial overviewMat)
		{
			return;
		}
		if (detailSurface?.Material is not ShaderMaterial detailMat)
		{
			return;
		}
		// Containers lay out deferred, so the panel has no size on the first frame.
		if (mapView.Size.X < 1f)
		{
			return;
		}

		SimState simState = _gameClient?.Sim?.WorldState?.SimState;
		SyncSelector(simState);
		EnsureOverlays();
		EnsureViewportSizes(minimap);

		TreasureMapState treasureMap = GetSelectedTreasureMap(simState);
		if (treasureMap != null)
		{
			RenderTreasureMap(detailMat, minimap, treasureMap);
			return;
		}
		RenderWorldMap(overviewMat, detailMat, minimap, (float)delta);
	}

	// ---- framing -----------------------------------------------------------

	// World XZ at the middle of the authored world.
	static Vector2 WorldCenterXZ(Minimap minimap)
	{
		Vector2 extent = minimap.ExtentMeters;
		Vector2I origin = minimap.WorldOriginXZ;
		return new Vector2(origin.X + extent.X * 0.5f, origin.Y + extent.Y * 0.5f);
	}

	// Half-extent that fits the WHOLE world. The map is spun so game-north points
	// up (NorthMapRotation), turning the authored world into a diamond inside the
	// square view — so the half-extent is the rotated world's bounding box,
	// ((extentX+extentZ)/2 · cos45). Rect corners past the diamond read as
	// void_color.
	static float WorldViewRadius(Minimap minimap)
	{
		Vector2 extent = minimap.ExtentMeters;
		return Mathf.Max(1f, (extent.X + extent.Y) * 0.5f * Mathf.Sqrt2 * 0.5f);
	}

	// Half-extent the OVERVIEW level shows: the authored metre extent, but never
	// more than the world has. Where the cap bites, the overview fits the world
	// exactly and has nothing left to pan.
	float OverviewViewRadius(Minimap minimap)
	{
		return Mathf.Max(1f, Mathf.Min(overviewViewMeters * 0.5f, WorldViewRadius(minimap)));
	}

	// Half-extent the detail render covers: its texel count at the authored
	// texels-per-meter.
	float DetailViewRadius()
	{
		int px = detailViewport?.Size.X ?? MinViewportPixels;
		return Mathf.Max(1f, px * 0.5f / Mathf.Max(0.05f, detailPixelsPerMeter));
	}

	// Map space is the world spun by −map_rotation: the frame the shader samples
	// in and the frame the textures are drawn in, with +Y screen-down.
	static Vector2 MapToWorldOffset(Vector2 mapOffset) => mapOffset.Rotated(NorthMapRotation);
	static Vector2 WorldToMapOffset(Vector2 worldOffset) => worldOffset.Rotated(-NorthMapRotation);

	// Each render target is square in MAP space, so in world space it is a square
	// turned 45° — its world-axis bounding half-extent is radius · √2. Clamping
	// that box inside the world rect is what stops a pan running off the edge
	// into void. A world smaller than the detail window collapses the range to
	// zero and the view stays centered.
	Vector2 ClampPan(Vector2 panOffsetMap, Minimap minimap, float detailRadius)
	{
		Vector2 extent = minimap.ExtentMeters;
		float reach = detailRadius * Mathf.Sqrt2;
		float halfX = Mathf.Max(0f, extent.X * 0.5f - reach);
		float halfZ = Mathf.Max(0f, extent.Y * 0.5f - reach);
		Vector2 world = MapToWorldOffset(panOffsetMap);
		world = new Vector2(Mathf.Clamp(world.X, -halfX, halfX), Mathf.Clamp(world.Y, -halfZ, halfZ));
		return WorldToMapOffset(world);
	}

	// The overview covers the whole world at the authored meters-per-texel; the
	// detail render matches the panel so it displays 1:1 when fully zoomed in.
	void EnsureViewportSizes(Minimap minimap)
	{
		int cap = Mathf.Max(MinViewportPixels, maxViewportPixels);
		if (overviewViewport != null)
		{
			float diameter = OverviewViewRadius(minimap) * 2f;
			int px = Mathf.Clamp(
				Mathf.CeilToInt(diameter / Mathf.Max(0.05f, overviewMetersPerPixel)),
				MinViewportPixels, cap);
			if (overviewViewport.Size.X != px)
			{
				overviewViewport.Size = new Vector2I(px, px);
			}
		}
		if (detailViewport != null)
		{
			int px = Mathf.Clamp(Mathf.RoundToInt(mapView.Size.X), MinViewportPixels, cap);
			if (detailViewport.Size.X != px)
			{
				detailViewport.Size = new Vector2I(px, px);
			}
		}
	}

	// ---- world map ---------------------------------------------------------

	// Whole-authored-world view, north-up, with region labels and banked markers.
	void RenderWorldMap(ShaderMaterial overviewMat, ShaderMaterial detailMat, Minimap minimap, float delta)
	{
		Vector2 worldCenter = WorldCenterXZ(minimap);
		float overviewRadius = OverviewViewRadius(minimap);
		float detailRadius = DetailViewRadius();

		// Wait for a real player rather than priming on a null: an unspawned
		// party would park the detail view on the world origin and never retry.
		Player player = _gameClient?.Player;
		if (!_panPrimed && player != null)
		{
			Vector3 anchor = player.GlobalPosition;
			_panOffsetMap = WorldToMapOffset(new Vector2(anchor.X - worldCenter.X, anchor.Z - worldCenter.Y));
			_panPrimed = true;
		}

		// Zoom is a discrete two-level toggle; only the blend between them animates.
		if (Input.IsActionJustPressed("LookUp"))
		{
			_zoomedIn = true;
		}
		else if (Input.IsActionJustPressed("LookDown"))
		{
			_zoomedIn = false;
		}
		_zoomBlend = Mathf.MoveToward(_zoomBlend, _zoomedIn ? 1f : 0f, delta / Mathf.Max(0.01f, zoomTransitionSeconds));
		float t = Mathf.SmoothStep(0f, 1f, _zoomBlend);

		// A view radius is a multiplier, so the zoom interpolates geometrically —
		// a linear lerp crawls at the wide end and lurches at the near one.
		float viewRadius = Mathf.Exp(Mathf.Lerp(Mathf.Log(overviewRadius), Mathf.Log(detailRadius), t));

		Vector2 pan = Input.GetVector("MoveLeft", "MoveRight", "MoveUp", "MoveDown");
		if (pan != Vector2.Zero)
		{
			_panOffsetMap += pan * (panScreensPerSecond * viewRadius * 2f * delta);
		}
		// Each level is clamped by its OWN radius, and the DISPLAYED center
		// interpolates between the two. This is what lets the zoomed-out level be
		// either thing: where `overviewViewMeters` covers the whole world it has no
		// room to pan and sits on the world center while the detail level keeps
		// the pan, and where it covers less it pans within its own limits.
		//
		// The STORED pan is clamped by the DETAIL radius, the loosest of the two.
		// Clamping it by the current (wider) view radius instead dragged it to
		// zero whenever you were zoomed out, so zooming back in landed on the
		// middle of the world rather than where you had been looking.
		_panOffsetMap = ClampPan(_panOffsetMap, minimap, detailRadius);
		Vector2 overviewCenterMap = ClampPan(_panOffsetMap, minimap, overviewRadius);
		Vector2 detailCenterMap = _panOffsetMap;
		Vector2 viewCenterMap = overviewCenterMap.Lerp(detailCenterMap, t);
		Vector2 viewCenterWorld = worldCenter + MapToWorldOffset(viewCenterMap);

		PushLayerState(overviewMat, minimap, _overviewBind, includeStateA: true);
		PushFraming(overviewMat, worldCenter + MapToWorldOffset(overviewCenterMap), overviewRadius,
			NorthMapRotation, minimap.StateTransition, 0f);
		PushLayerState(detailMat, minimap, _detailBind, includeStateA: true);
		PushFraming(detailMat, worldCenter + MapToWorldOffset(detailCenterMap), detailRadius,
			NorthMapRotation, minimap.StateTransition, 0f);

		// The overview draws opaque underneath and the detail dissolves over it,
		// so the pair reads as a straight cross-fade with no panel showing
		// through at half blend.
		LayoutLayer(overviewRect, overviewCenterMap, overviewRadius, viewCenterMap, viewRadius, 1f, t < 0.999f);
		LayoutLayer(detailRect, detailCenterMap, detailRadius, viewCenterMap, viewRadius, t, t > 0.001f);
		SetViewportActive(overviewViewport, t < 0.999f);
		SetViewportActive(detailViewport, t > 0.001f);

		// Region names belong to the whole-world read only.
		UpdateRegionLabels(viewCenterWorld, viewRadius);
		if (regionLabels != null)
		{
			regionLabels.Modulate = new Color(1f, 1f, 1f, 1f - t);
			regionLabels.Visible = t < 0.999f;
		}
		FrameMarkers(_overviewMarkers, viewCenterWorld, viewRadius, 1f - t);
		FrameMarkers(_detailMarkers, viewCenterWorld, viewRadius, t);
		if (_xMarker != null)
		{
			_xMarker.Visible = false;
		}
	}

	// A single treasure map: terrain only (no labels/markers), zoomed in, spun to
	// the map's own random heading, centered on the dig spot, with fog forced off
	// (min_reveal = 1) so the marked land shows even if never explored. Only state
	// B is used — no crossfade — so state_transition is pinned to 1. Drawn on the
	// detail layer at panel scale; the world map's own zoom is parked so switching
	// back to it opens on the whole world again.
	void RenderTreasureMap(ShaderMaterial detailMat, Minimap minimap, TreasureMapState map)
	{
		_zoomedIn = false;
		_zoomBlend = 0f;
		_panPrimed = false;

		PushLayerState(detailMat, minimap, _detailBind, includeStateA: false);
		Vector2 center = new Vector2(map.DigLocation.X, map.DigLocation.Z);
		PushFraming(detailMat, center, treasureMapViewRadiusMeters, map.MapRotation, 1f, 1f);
		// Read the surrounding terrain relative to the dig site's own elevation.
		// Biased into the map textures' height space (see MinimapData.HeightBias);
		// a raw world Y here would classify the whole map to one side.
		detailMat.SetShaderParameter("reference_elevation_b", map.DigLocation.Y - minimap.HeightBias);

		LayoutLayer(overviewRect, Vector2.Zero, 1f, Vector2.Zero, 1f, 0f, false);
		LayoutLayer(detailRect, Vector2.Zero, treasureMapViewRadiusMeters,
			Vector2.Zero, treasureMapViewRadiusMeters, 1f, true);
		SetViewportActive(overviewViewport, false);
		SetViewportActive(detailViewport, true);

		HideOverlays();
		if (_xMarker != null)
		{
			// Dig spot is the view center; place the X there in mapView space,
			// matching the marker-icon projection.
			_xMarker.Position = mapView.Size * 0.5f;
			_xMarker.Visible = true;
		}
	}

	// ---- layers ------------------------------------------------------------

	// Scale and place one render target so the world area it covers (its own
	// center + radius, in map space) lands exactly where the current view framing
	// says it should. This is what keeps the two layers registered on each other
	// mid-zoom.
	void LayoutLayer(TextureRect rect, Vector2 texCenterMap, float texRadius,
		Vector2 viewCenterMap, float viewRadius, float alpha, bool visible)
	{
		if (rect == null)
		{
			return;
		}
		rect.Visible = visible;
		if (!visible)
		{
			return;
		}
		Vector2 panel = mapView.Size;
		float pxPerMeter = panel.X / Mathf.Max(0.001f, viewRadius * 2f);
		float side = texRadius * 2f * pxPerMeter;
		Vector2 center = panel * 0.5f + (texCenterMap - viewCenterMap) * pxPerMeter;
		rect.Size = new Vector2(side, side);
		rect.Position = center - new Vector2(side, side) * 0.5f;
		rect.Modulate = new Color(1f, 1f, 1f, alpha);
	}

	// A layer contributing nothing this frame stops rendering entirely — these
	// are full-screen shader passes and the map is a modal.
	static void SetViewportActive(SubViewport viewport, bool active)
	{
		if (viewport == null)
		{
			return;
		}
		SubViewport.UpdateMode want = active ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
		if (viewport.RenderTargetUpdateMode != want)
		{
			viewport.RenderTargetUpdateMode = want;
		}
	}

	static void PushFraming(ShaderMaterial mat, Vector2 centerWorldXZ, float viewRadius, float rotation,
		float stateTransition, float minReveal)
	{
		mat.SetShaderParameter("player_world_xz", centerWorldXZ);
		mat.SetShaderParameter("view_radius_meters", viewRadius);
		mat.SetShaderParameter("map_rotation", rotation);
		mat.SetShaderParameter("state_transition", stateTransition);
		mat.SetShaderParameter("min_reveal", minReveal);
	}

	// ---- overlays ----------------------------------------------------------

	// Lazily create the panel-scale overlays as children of mapView, above both
	// map layers. The X is added last so it sits on top of the markers.
	void EnsureOverlays()
	{
		if (mapView == null)
		{
			return;
		}
		if (_overviewMarkers == null)
		{
			// World map is banked-only — field markers appear here after camping.
			_overviewMarkers = MapMarkerOverlay.Create(_gameClient, unknownMarkerIcon, markerIconSize,
				includeProvisional: false, circleMaskFraction: 0f);
			_overviewMarkers.ActiveCampfireOnly = true;
			mapView.AddChild(_overviewMarkers);
		}
		if (_detailMarkers == null)
		{
			_detailMarkers = MapMarkerOverlay.Create(_gameClient, unknownMarkerIcon, markerIconSize,
				includeProvisional: false, circleMaskFraction: 0f);
			mapView.AddChild(_detailMarkers);
		}
		if (_xMarker == null)
		{
			_xMarker = new TreasureXMarker { icon = treasureXIcon, MouseFilter = MouseFilterEnum.Ignore };
			mapView.AddChild(_xMarker);
		}
	}

	static void FrameMarkers(MapMarkerOverlay overlay, Vector2 centerWorldXZ, float viewRadius, float alpha)
	{
		if (overlay == null)
		{
			return;
		}
		overlay.Visible = alpha > 0.001f;
		if (!overlay.Visible)
		{
			return;
		}
		overlay.Modulate = new Color(1f, 1f, 1f, alpha);
		overlay.SetFraming(centerWorldXZ, viewRadius, NorthMapRotation);
	}

	// Hide the world-map-only overlays (region labels + markers) for treasure mode.
	void HideOverlays()
	{
		if (regionLabels != null)
		{
			regionLabels.Visible = false;
		}
		if (_overviewMarkers != null)
		{
			_overviewMarkers.Visible = false;
		}
		if (_detailMarkers != null)
		{
			_detailMarkers.Visible = false;
		}
	}

	// ---- treasure-map selector --------------------------------------------

	// Rebuild the selector's item list from the collected maps whenever their
	// count changes (a map found or dug up). Item 0 is the world map; item N is
	// treasure map N. Item index equals item id here (added in order).
	void SyncSelector(SimState simState)
	{
		if (mapSelector == null)
		{
			return;
		}
		int count = simState?.TreasureMaps.Count ?? 0;
		if (count == _selectorMapCount)
		{
			return;
		}
		_selectorMapCount = count;
		int prevId = mapSelector.GetSelectedId();
		mapSelector.Clear();
		mapSelector.AddItem(Loc.Get(Loc.Keys.map_option_world), 0);
		for (int i = 0; i < count; i++)
		{
			mapSelector.AddItem(Loc.Format(Loc.Keys.map_option_treasure, (i + 1).ToString()), i + 1);
		}
		// Keep the prior selection if it still exists, else fall back to the world map.
		int restore = (prevId >= 0 && prevId <= count) ? prevId : 0;
		mapSelector.Select(restore);
	}

	// The treasure map the selector currently points at, or null for the world map
	// (item 0) / no selection.
	TreasureMapState GetSelectedTreasureMap(SimState simState)
	{
		if (simState == null || mapSelector == null)
		{
			return null;
		}
		int idx = mapSelector.GetSelectedId() - 1;
		if (idx < 0 || idx >= simState.TreasureMaps.Count)
		{
			return null;
		}
		return simState.TreasureMaps[idx];
	}

	// ---- region labels -----------------------------------------------------

	// Mirrors the world→UV math in minimap.gdshader so labels track the shader's
	// rendered content exactly: a centroid at centerWorldXZ sits at UV (0.5, 0.5);
	// ±viewRadius on either axis lands at UV 0 / 1. Each label is centered on its
	// centroid.
	void UpdateRegionLabels(Vector2 centerWorldXZ, float viewRadius)
	{
		if (regionLabels == null)
		{
			return;
		}
		WorldState ws = _gameClient?.Sim?.WorldState;
		if (ws == null)
		{
			return;
		}
		IReadOnlyDictionary<RegionData, Vector2> centroids = ws.RegionCentroidsXZ;
		if (centroids == null || centroids.Count == 0)
		{
			return;
		}

		Vector2 panelSize = regionLabels.Size;
		float diameter = viewRadius * 2f;
		if (diameter <= 0f)
		{
			return;
		}

		foreach (var kv in centroids)
		{
			RegionData region = kv.Key;
			if (region == null)
			{
				continue;
			}
			if (!_labels.TryGetValue(region, out Label label))
			{
				label = CreateRegionLabel(region);
				regionLabels.AddChild(label);
				_labels[region] = label;
			}

			// Show a region label once it's on the world map: banked at a
			// campfire, OR captured in the frozen tree-climb scout snapshot
			// (field-discovered regions graduate onto the world map when the
			// player scouts from a tree, and stay frozen there until banked).
			bool show = ws.SimState.IsRegionShownOnWorldMap(region);
			label.Visible = show;
			if (!show)
			{
				continue;
			}

			Vector2 centroid = kv.Value;
			// Mirror the shader: screen offset → world offset is a +NorthMapRotation
			// spin, so world offset → screen inverts it.
			Vector2 uvCentered = WorldToMapOffset((centroid - centerWorldXZ) / diameter);
			Vector2 uv = uvCentered + new Vector2(0.5f, 0.5f);
			Vector2 px = new Vector2(uv.X * panelSize.X, uv.Y * panelSize.Y);
			Vector2 labelSize = label.Size;
			label.Position = new Vector2(px.X - labelSize.X * 0.5f, px.Y - labelSize.Y * 0.5f);
		}
	}

	Label CreateRegionLabel(RegionData region)
	{
		Label label = new Label();
		label.Text = region.displayName.ToString();
		label.HorizontalAlignment = HorizontalAlignment.Center;
		label.VerticalAlignment = VerticalAlignment.Center;
		label.MouseFilter = Control.MouseFilterEnum.Ignore;
		label.AddThemeFontSizeOverride("font_size", regionLabelFontSize);
		// Dark outline keeps the name readable over both bright surface
		// tiles and dark void margins. Match the panel's warm border tint
		// so the labels feel like part of the map's frame, not the HUD.
		label.AddThemeColorOverride("font_color", new Color(0.95f, 0.92f, 0.78f, 1f));
		label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
		label.AddThemeConstantOverride("outline_size", 4);
		return label;
	}

	// ---- shader state ------------------------------------------------------

	void PushLayerState(ShaderMaterial mat, Minimap minimap, LayerBinding bind, bool includeStateA)
	{
		if (minimap.TileLutTexture != bind.TileLut)
		{
			mat.SetShaderParameter("tile_lut", minimap.TileLutTexture);
			bind.TileLut = minimap.TileLutTexture;
		}
		if (includeStateA)
		{
			PushState(mat, minimap.StateA, "_a", bind.A);
		}
		PushState(mat, minimap.StateB, "_b", bind.B);
	}

	static void PushState(ShaderMaterial mat, in Minimap.StateSnapshot s, string suffix, BoundStateTextures bound)
	{
		Texture2D surf = s.Surface;
		Texture2D below1 = s.SurfaceBelow1 ?? surf;
		Texture2D below2 = s.SurfaceBelow2 ?? surf;
		// The world map shows banked (party-only) reveal — un-banked field reveal
		// stays on the minimap until recorded at a campfire. (The HUD minimap uses
		// the party ∪ active Exploration textures instead.)
		Texture2D expl = s.ExplorationBanked ?? surf;
		Texture2D explBelow1 = s.ExplorationBankedBelow1 ?? expl;
		Texture2D explBelow2 = s.ExplorationBankedBelow2 ?? expl;

		if (surf != bound.Surface) { mat.SetShaderParameter("surface_texture" + suffix, surf); bound.Surface = surf; }
		if (below1 != bound.SurfaceBelow1) { mat.SetShaderParameter("surface_texture_below1" + suffix, below1); bound.SurfaceBelow1 = below1; }
		if (below2 != bound.SurfaceBelow2) { mat.SetShaderParameter("surface_texture_below2" + suffix, below2); bound.SurfaceBelow2 = below2; }
		if (expl != bound.Exploration) { mat.SetShaderParameter("exploration_texture" + suffix, expl); bound.Exploration = expl; }
		if (explBelow1 != bound.ExplorationBelow1) { mat.SetShaderParameter("exploration_texture_below1" + suffix, explBelow1); bound.ExplorationBelow1 = explBelow1; }
		if (explBelow2 != bound.ExplorationBelow2) { mat.SetShaderParameter("exploration_texture_below2" + suffix, explBelow2); bound.ExplorationBelow2 = explBelow2; }

		mat.SetShaderParameter("world_origin_xz" + suffix, new Vector2(s.WorldOriginXZ.X, s.WorldOriginXZ.Y));
		mat.SetShaderParameter("world_extent_pixels" + suffix, s.ExtentPixels);
		mat.SetShaderParameter("meters_per_pixel" + suffix, s.MetersPerPixel);
		mat.SetShaderParameter("reference_elevation" + suffix, s.ReferenceElevation);
	}
}
