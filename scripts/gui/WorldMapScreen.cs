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
// once — see WorldSimState.DiscoveredRegions.
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

	// Marker icon overlay, lazily created as a child of regionLabels (which is
	// sized to mapTexture's rect and un-rotated, so markers land on the terrain).
	MapMarkerOverlay _markerOverlay;

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

	public override void _Ready()
	{
		Visible = false;
	}

	public override void _Process(double delta)
	{
		if (!Visible)
		{
			return;
		}
		Minimap minimap = _gameClient?.World?.Minimap;
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

		PushState(mat, minimap.StateA, "_a", ref _boundA);
		PushState(mat, minimap.StateB, "_b", ref _boundB);

		// Center on world midpoint, view radius = max half-extent so the
		// entire authored world fits the square AspectRatioContainer.
		// The smaller axis ends up with void_color margins, which is fine.
		Vector2 extent = minimap.ExtentMeters;
		Vector2I origin = minimap.WorldOriginXZ;
		Vector2 worldCenter = new Vector2(
			origin.X + extent.X * 0.5f,
			origin.Y + extent.Y * 0.5f);
		float viewRadius = Mathf.Max(extent.X, extent.Y) * 0.5f;

		mat.SetShaderParameter("player_world_xz", worldCenter);
		mat.SetShaderParameter("view_radius_meters", viewRadius);
		mat.SetShaderParameter("state_transition", minimap.StateTransition);

		UpdateRegionLabels(worldCenter, viewRadius);

		if (regionLabels != null)
		{
			if (_markerOverlay == null)
			{
				// World map is banked-only — field markers appear here after camping.
				_markerOverlay = MapMarkerOverlay.Create(_gameClient, unknownMarkerIcon, markerIconSize, includeProvisional: false);
				regionLabels.AddChild(_markerOverlay);
			}
			_markerOverlay.SetFraming(worldCenter, viewRadius);
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
		WorldState ws = _gameClient?.World?.WorldState;
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

			// Show a region label only once it's been recorded at a campfire —
			// field discoveries stay hidden (in the active member's provisional
			// store) until banked, matching the exploration fog-of-war split.
			bool show = ws.SimState.IsRegionBanked(region);
			label.Visible = show;
			if (!show)
			{
				continue;
			}

			Vector2 centroid = kv.Value;
			Vector2 uv = (centroid - worldCenter) / diameter + new Vector2(0.5f, 0.5f);
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
