using System.Collections.Generic;
using Godot;

// World-map tab rendered inside AlmanacScreen. The Almanac wrapper owns
// InputSuppressed / hud-visibility / ui_cancel handling; this screen just
// renders when its tab is visible.
//
// Renders the entire world via the same minimap shader the HUD uses,
// driving the same state-A / state-B uniforms but with a wider
// view_radius_meters (full world half-extent) and player_world_xz pinned
// to the world center, so the whole authored map fits the panel. The
// circular alpha mask in the shader is disabled via worldmap.tres
// (mask_radius = 0.7071, softness = 0). The TextureRect lives inside an
// AspectRatioContainer (ratio 1.0) because the shader's view radius is
// isotropic — a non-square rect would stretch the world.
//
// Region-name labels are overlaid on top of the rendered map at each
// region's centroid (WorldState.RegionCentroidsXZ) using the same UV math
// the shader uses, so labels stay locked to their region as the panel
// resizes. A label only shows if its region has been entered at least
// once — see SimState.DiscoveredRegions.
//
// No mouse interaction — every Control here keeps mouse_filter = Ignore.
[GlobalClass]
public partial class WorldMapScreen : Control
{
	[Export] public TextureRect mapTexture;
	// Overlay control sized to MapTexture's rect; region labels are
	// added as children and positioned in local pixels each frame.
	[Export] public Control regionLabels;
	[Export(PropertyHint.Range, "8,64,1")] public int regionLabelFontSize = 24;
	// Shared "?" icon drawn for Sensed (unidentified) map markers. Optional —
	// null falls back to a drawn "?" glyph. Identified markers use their own icon.
	[Export] public Texture2D unknownMarkerIcon;
	[Export(PropertyHint.Range, "8,96,1")] public int markerIconSize = 28;

	// Switches the panel between the world map (item 0) and each collected
	// treasure map. Populated from SimState.TreasureMaps.
	[Export] public OptionButton mapSelector;
	// Zoom used for a treasure map — a small view radius so the marked area reads
	// close-up, versus the whole-world radius the world map computes.
	[Export(PropertyHint.Range, "16,200,1")] public float treasureMapViewRadiusMeters = 48f;
	// Icon drawn at the dig spot (map center) on a treasure map. Null = a drawn red X.
	[Export] public Texture2D treasureXIcon;

	// World-sampling spin (radians) that puts game-north (−X,−Z) at the top of
	// the map. +X is screen-right and +Z screen-down in the shader's unrotated
	// frame, so the (−X,−Z) diagonal starts at the upper-left; −π/4 rotates it
	// to straight up. Fed to the shader's map_rotation and mirrored by the label
	// / marker projection below so all three stay locked together.
	const float NorthMapRotation = -Mathf.Pi / 4f;

	// Marker icon overlay, lazily created as a child of regionLabels (which is
	// sized to mapTexture's rect and un-rotated, so markers land on the terrain).
	MapMarkerOverlay _markerOverlay;

	// Full-rect overlay drawing the dig-spot X, shown only in treasure-map mode.
	// A child of regionLabels so it renders above the map texture.
	TreasureXMarker _xMarker;

	GameClient _gameClient;

	public void Initialize(GameClient gameClient)
	{
		_gameClient = gameClient;
	}

	Texture2D _boundTileLut;
	Texture2D _boundFoliageLut;
	struct BoundStateTextures
	{
		public Texture2D Surface, SurfaceBelow1, SurfaceBelow2;
		public Texture2D Exploration, ExplorationBelow1, ExplorationBelow2;
	}
	BoundStateTextures _boundA;
	BoundStateTextures _boundB;

	// Lazy-created on first visible frame, once WorldState is available.
	readonly Dictionary<RegionData, Label> _labels = new();

	// Rebuild the selector only when the collected-map count changes.
	int _selectorMapCount = -1;

	public override void _Ready()
	{
		Visible = false;
		// This modal is Tab/controller-navigated with no mouse-driven focus, so the
		// selector is unreachable unless we hand it focus when the tab is shown.
		VisibilityChanged += OnVisibilityChanged;
	}

	void OnVisibilityChanged()
	{
		if (Visible && mapSelector != null)
		{
			// Deferred: the control can't take focus in the same frame its
			// visibility flips on.
			mapSelector.CallDeferred(Control.MethodName.GrabFocus);
		}
	}

	public override void _Process(double delta)
	{
		if (!Visible)
		{
			return;
		}
		Minimap minimap = _gameClient?.Sim?.Minimap;
		if (minimap == null || mapTexture == null)
		{
			return;
		}
		if (mapTexture.Material is not ShaderMaterial mat)
		{
			return;
		}
		if (minimap.StateB.Surface == null)
		{
			return;
		}

		if (minimap.TileLutTexture != _boundTileLut)
		{
			mat.SetShaderParameter("tile_lut", minimap.TileLutTexture);
			_boundTileLut = minimap.TileLutTexture;
		}
		if (minimap.FoliageLutTexture != _boundFoliageLut)
		{
			mat.SetShaderParameter("foliage_lut", minimap.FoliageLutTexture);
			_boundFoliageLut = minimap.FoliageLutTexture;
		}

		SimState simState = _gameClient?.Sim?.WorldState?.SimState;
		SyncSelector(simState);

		TreasureMapState treasureMap = GetSelectedTreasureMap(simState);
		if (treasureMap != null)
		{
			RenderTreasureMap(mat, minimap, treasureMap);
		}
		else
		{
			RenderWorldMap(mat, minimap);
		}
	}

	// Lazily create the label-plane overlays (marker icons + the treasure X) as
	// children of regionLabels, which is sized to the map rect and drawn above the
	// map texture. The X is added last so it sits on top of the markers.
	void EnsureOverlays()
	{
		if (regionLabels == null)
		{
			return;
		}
		if (_markerOverlay == null)
		{
			// World map is banked-only — field markers appear here after camping.
			_markerOverlay = MapMarkerOverlay.Create(_gameClient, unknownMarkerIcon, markerIconSize, includeProvisional: false, circleMaskFraction: 0f);
			regionLabels.AddChild(_markerOverlay);
		}
		if (_xMarker == null)
		{
			_xMarker = new TreasureXMarker { icon = treasureXIcon, MouseFilter = MouseFilterEnum.Ignore };
			regionLabels.AddChild(_xMarker);
		}
	}

	// Whole-authored-world view, north-up, with region labels and banked markers.
	void RenderWorldMap(ShaderMaterial mat, Minimap minimap)
	{
		PushState(mat, minimap.StateA, "_a", ref _boundA);
		PushState(mat, minimap.StateB, "_b", ref _boundB);

		// Center on world midpoint. The map is spun so game-north (+X,+Z) points
		// up (NorthMapRotation), turning the authored world into a diamond inside
		// the square AspectRatioContainer — so the view half-extent grows to the
		// rotated world's bounding box ((extentX+extentZ)/2 · cos45) to fit the
		// whole diamond. Rect corners past the diamond read as void_color.
		Vector2 extent = minimap.ExtentMeters;
		Vector2I origin = minimap.WorldOriginXZ;
		Vector2 worldCenter = new Vector2(
			origin.X + extent.X * 0.5f,
			origin.Y + extent.Y * 0.5f);
		float viewRadius = (extent.X + extent.Y) * 0.5f * Mathf.Sqrt2 * 0.5f;

		mat.SetShaderParameter("player_world_xz", worldCenter);
		mat.SetShaderParameter("view_radius_meters", viewRadius);
		mat.SetShaderParameter("map_rotation", NorthMapRotation);
		mat.SetShaderParameter("state_transition", minimap.StateTransition);
		mat.SetShaderParameter("min_reveal", 0f);

		UpdateRegionLabels(worldCenter, viewRadius);

		EnsureOverlays();
		if (_markerOverlay != null)
		{
			_markerOverlay.Visible = true;
			_markerOverlay.SetFraming(worldCenter, viewRadius, NorthMapRotation);
		}
		if (_xMarker != null)
		{
			_xMarker.Visible = false;
		}
	}

	// A single treasure map: terrain only (no labels/markers), zoomed in, spun to
	// the map's own random heading, centered on the dig spot, with fog forced off
	// (min_reveal = 1) so the marked land shows even if never explored. Only state
	// B is used — no crossfade — so state_transition is pinned to 1.
	void RenderTreasureMap(ShaderMaterial mat, Minimap minimap, TreasureMapState map)
	{
		PushState(mat, minimap.StateB, "_b", ref _boundB);

		Vector2 center = new Vector2(map.DigLocation.X, map.DigLocation.Z);
		mat.SetShaderParameter("player_world_xz", center);
		mat.SetShaderParameter("view_radius_meters", treasureMapViewRadiusMeters);
		mat.SetShaderParameter("map_rotation", map.MapRotation);
		mat.SetShaderParameter("state_transition", 1f);
		// Read the surrounding terrain relative to the dig site's own elevation.
		// Biased into the map textures' height space (see MinimapData.HeightBias);
		// a raw world Y here would classify the whole map to one side.
		mat.SetShaderParameter("reference_elevation_b", map.DigLocation.Y - minimap.HeightBias);
		mat.SetShaderParameter("min_reveal", 1f);

		EnsureOverlays();
		HideOverlays();
		if (_xMarker != null && regionLabels != null)
		{
			// Dig spot is the view center (UV 0.5,0.5); place the X there in
			// regionLabels space, matching the marker-icon projection.
			_xMarker.Position = regionLabels.Size * 0.5f;
			_xMarker.Visible = true;
		}
	}

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

	// Hide the world-map-only overlays (region labels + markers) for treasure mode.
	void HideOverlays()
	{
		foreach (Label label in _labels.Values)
		{
			label.Visible = false;
		}
		if (_markerOverlay != null)
		{
			_markerOverlay.Visible = false;
		}
	}

	// Mirrors the world→UV math in minimap.gdshader so labels track the
	// shader's rendered content exactly: a centroid at worldCenter sits
	// at UV (0.5, 0.5); ±viewRadius on either axis lands at UV 0 / 1.
	// Each label is centered (anchored at UV 0.5,0.5 so the displayed
	// name reads centered on the centroid).
	void UpdateRegionLabels(Vector2 worldCenter, float viewRadius)
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
			// spin, so world offset → screen inverts it (-NorthMapRotation).
			Vector2 uvCentered = ((centroid - worldCenter) / diameter).Rotated(-NorthMapRotation);
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

	void PushState(ShaderMaterial mat, in Minimap.StateSnapshot s, string suffix, ref BoundStateTextures bound)
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
