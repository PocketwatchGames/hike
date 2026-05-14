using Godot;

// Pool of plain Sprite3D ghosts that snapshot the player's (or any
// LitSprite's) current frame during a dash burst and fade out via the
// sprite_lit shader's `visibility` dither uniform. Reuses the source
// sprite's shared ShaderMaterial so every ghost batches into the same
// draw call as the source — adding the trail costs one extra
// VisualInstance3D per pool slot, with no per-emit allocations.
//
// Per-frame cost shape:
//  - No allocation during a dash; the pool is built once on first emit.
//  - No shadow / reflection / x-ray proxies (unlike LitSprite). One quad
//    per pool slot, period.
//  - Alive ghosts push one RenderingServer.InstanceGeometrySetShaderParameter
//    (visibility) per frame; dead slots do nothing.
[GlobalClass]
public partial class DashGhostTrail : Node3D
{
	// The visible sprite to snapshot. Ghosts copy its Texture / RegionRect /
	// world transform / FlipH at emit time and bind its shared material so
	// they render through the same shader path.
	[Export] private LitSprite _source;
	// Cap on simultaneously-rendered ghosts. With the default emit interval
	// and lifetime this comfortably covers a 200 ms dash burst plus its
	// fade tail; the pool is a hard ceiling regardless.
	[Export] private int _maxGhosts = 6;
	// Seconds between consecutive ghost emits while EmitEnabled is true.
	[Export] private float _emitInterval = 0.03f;
	// How long each individual ghost remains visible before being deactivated.
	[Export] private float _lifetimeSeconds = 0.25f;
	// Starting `visibility` (the sprite_lit dither-fade uniform) for a freshly
	// emitted ghost. 1.0 reads as a solid clone for the first frame; 0.7 reads
	// as a clearly-dithered afterimage. Decays linearly to 0 over
	// _lifetimeSeconds.
	[Export(PropertyHint.Range, "0,1,0.01")] private float _initialVisibility = 0.7f;

	Sprite3D[] _ghosts;
	float[] _ages;
	int _nextIndex;
	float _emitAccum;
	bool _poolBuilt;

	public bool EmitEnabled { get; set; }

	// Lazy pool construction. The source sprite resolves its shared
	// ShaderMaterial in LitSprite.Apply() (called from SpriteBase._Ready),
	// and there is no clean ordering guarantee between sibling _Ready calls
	// — building lazily on the first Emit ensures the source has bound
	// MaterialOverride before we copy it.
	private void EnsurePool()
	{
		if (_poolBuilt) { return; }
		if (_source == null)
		{
			GD.PushError($"DashGhostTrail '{Name}' has no source LitSprite assigned.");
			return;
		}
		_ghosts = new Sprite3D[_maxGhosts];
		_ages = new float[_maxGhosts];
		for (int i = 0; i < _maxGhosts; i++)
		{
			var g = new Sprite3D();
			g.Name = $"Ghost{i}";
			g.Centered = false;
			g.PixelSize = _source.PixelSize;
			g.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
			g.TextureFilter = _source.TextureFilter;
			// World-space parking: a ghost stays where the source was at emit
			// time, so the trail visibly lags behind motion. TopLevel decouples
			// the ghost's transform from its parent (the player) so a moving
			// player produces a real lag effect rather than the ghost sliding
			// along with the player.
			g.TopLevel = true;
			g.Visible = false;
			AddChild(g);
			_ghosts[i] = g;
			_ages[i] = -1f;
		}
		_poolBuilt = true;
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;
		if (EmitEnabled)
		{
			EnsurePool();
			if (_ghosts == null) { return; }
			_emitAccum += dt;
			while (_emitAccum >= _emitInterval)
			{
				_emitAccum -= _emitInterval;
				Emit();
			}
		}
		else
		{
			_emitAccum = 0f;
			if (_ghosts == null) { return; }
		}

		for (int i = 0; i < _ghosts.Length; i++)
		{
			if (_ages[i] < 0f) { continue; }
			_ages[i] += dt;
			if (_ages[i] >= _lifetimeSeconds)
			{
				_ages[i] = -1f;
				_ghosts[i].Visible = false;
				continue;
			}
			float t = _ages[i] / _lifetimeSeconds;
			float vis = _initialVisibility * (1f - t);
			Rid rid = _ghosts[i].GetInstance();
			if (rid.IsValid)
			{
				RenderingServer.InstanceGeometrySetShaderParameter(rid, "visibility", vis);
			}
		}
	}

	// Capture the current source-sprite frame into the next ring-buffer slot.
	// Snapshots transform / texture / region / mirror state and pushes the
	// per-instance shader uniforms sprite_lit needs to render the same frame
	// the source did this tick.
	private void Emit()
	{
		int idx = _nextIndex;
		_nextIndex = (_nextIndex + 1) % _ghosts.Length;
		Sprite3D g = _ghosts[idx];

		g.GlobalTransform = _source.GlobalTransform;
		g.Texture = _source.Texture;
		g.RegionEnabled = _source.RegionEnabled;
		g.RegionRect = _source.RegionRect;
		g.Offset = _source.Offset;
		// FlipH is the authored baseline; the shader-side mirror state is
		// FlipH XOR camera-yaw flip. LitSprite.EffectiveMirror gives the
		// resolved value pushed to its instance uniform this frame.
		bool mirror = _source.EffectiveMirror;
		g.FlipH = mirror;

		// Refresh material binding — the source may rebuild its shared
		// material on an atlas swap. Cheap reference compare after the
		// first dash.
		if (g.MaterialOverride != _source.MaterialOverride)
		{
			g.MaterialOverride = _source.MaterialOverride;
		}

		Vector2I size = new((int)_source.RegionRect.Size.X, (int)_source.RegionRect.Size.Y);
		Vector2I origin = new((int)_source.RegionRect.Position.X, (int)_source.RegionRect.Position.Y);
		Rid rid = g.GetInstance();
		if (rid.IsValid)
		{
			RenderingServer.InstanceGeometrySetShaderParameter(rid, "sprite_size", size);
			RenderingServer.InstanceGeometrySetShaderParameter(rid, "sprite_region_origin", origin);
			RenderingServer.InstanceGeometrySetShaderParameter(rid, "sprite_mirror", mirror);
			RenderingServer.InstanceGeometrySetShaderParameter(rid, "visibility", _initialVisibility);
			RenderingServer.InstanceGeometrySetShaderParameter(rid, "forward_offset", _source.ForwardOffset);
		}
		g.Visible = true;
		_ages[idx] = 0f;
	}
}
