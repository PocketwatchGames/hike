using Godot;
using System;

// Pixel-art two-pass upscale rig — self-contained so it can be lifted into
// another project. The game renders at low resolution into `sceneViewport`
// (a SubViewport with its own World3D); an outer orthographic pass blits that
// texture to screen through a BloomQuad (carrying `upscaleMaterial`) with the
// OuterEnv's bloom/tonemap on top. Each frame the rig pixel-snaps the camera
// to the inner-texel grid and feeds the upscale shader the residual sub-texel
// offset so the snap is invisible (no crawling within chunky blocks).
[GlobalClass]
public partial class ViewportRig : Node
{
	[Export] public SubViewport sceneViewport;
	[Export] public ShaderMaterial upscaleMaterial;
	[Export] public GameCamera camera;

	// Residual sub-texel camera offset (inner-viewport texels), published to the
	// upscale shader and reused by ProjectToScreen so world→screen projection
	// matches the snapped render exactly.
	Vector2 _subpixelTexelOffset;

	// Integer screen-pixels per inner-viewport pixel.
	public int PixelScale => Math.Max(1, CVars.pixelScale.Value);

	public override void _Ready()
	{
		GetTree().Root.SizeChanged += UpdateViewportSize;
		UpdateViewportSize();
		if (upscaleMaterial != null && sceneViewport != null)
		{
			upscaleMaterial.SetShaderParameter("inner_tex", sceneViewport.GetTexture());
		}
	}

	// Maps a world position to a screen-space pixel coordinate that lines up
	// with the upscaled render. Called by the HUD layers (floating text, mob
	// bars, interact prompts) via GameClient.ProjectToScreen.
	public Vector2 ProjectToScreen(Vector3 worldPos)
	{
		if (camera == null) { return Vector2.Zero; }
		// The upscale shader flips V (sample at 1 - inner_uv.y) to compensate
		// for Godot's Y-up viewport texture storage. That flip inverts the
		// direction of uv_offset.y relative to uv_offset.x, so the Y correction
		// here adds the subpixel offset where X subtracts.
		Vector2 innerPx = camera.UnprojectPosition(worldPos);
		return new Vector2(
			(innerPx.X - _subpixelTexelOffset.X) * PixelScale,
			(innerPx.Y + _subpixelTexelOffset.Y) * PixelScale);
	}

	// Inverse of ProjectToScreen: a window pixel (mouse position) → the inner
	// viewport pixel the same point occupies, so it can be fed to the scene
	// camera's ProjectRay* / picking. Y adds where X subtracts for the same
	// V-flip reason described above.
	public Vector2 ScreenToInner(Vector2 screenPos)
	{
		return new Vector2(
			screenPos.X / PixelScale + _subpixelTexelOffset.X,
			screenPos.Y / PixelScale - _subpixelTexelOffset.Y);
	}

	public void UpdateViewportSize()
	{
		if (sceneViewport == null)
		{
			return;
		}
		Vector2I screenSize = GetTree().Root.Size;
		int scale = Math.Max(1, CVars.pixelScale.Value);
		// +1 pixel padding on each axis for subpixel camera offset.
		int innerW = (screenSize.X + scale - 1) / scale + 1;
		int innerH = (screenSize.Y + scale - 1) / scale + 1;
		sceneViewport.Size = new Vector2I(innerW, innerH);

		if (upscaleMaterial != null)
		{
			Vector2 uvScale = new Vector2(
				(float)screenSize.X / (scale * innerW),
				(float)screenSize.Y / (scale * innerH));
			upscaleMaterial.SetShaderParameter("uv_scale", uvScale);
		}
	}

	// Per-frame entry point called by GameClient (normal + bird's-eye camera
	// modes): pixel-snaps the camera and refreshes the upscale uniforms.
	public void SnapAndUpscale()
	{
		if (sceneViewport == null || upscaleMaterial == null || camera == null)
		{
			return;
		}

		int scale = Math.Max(1, CVars.pixelScale.Value);
		Vector2I screenSize = GetTree().Root.Size;
		Vector2I innerSize = sceneViewport.Size;

		// World units per inner-viewport texel. Orthographic camera.Size is
		// the vertical world extent mapped across innerSize.Y texels (Godot
		// derives horizontal size from viewport aspect, so texel width in
		// world equals this too). The camera must snap in multiples of this
		// so every voxel edge projects to the same sub-texel offset frame
		// to frame — otherwise wall pixels crawl within each chunky block.
		float chunky = camera.Size / Mathf.Max(1, innerSize.Y);
		RenderingServer.GlobalShaderParameterSet("sprite_chunky", chunky);

		// Vertical stretch = 1/cos(camera pitch) — compensates for the main
		// camera's tilt so one source pixel = one screen pixel. The shadow
		// caster uses the same stretch to match the visible sprite's
		// world-space height, keeping shadow length consistent with the view.
		Vector3 mainForward = camera.GlobalBasis.Z;
		float mainPitch = Mathf.Asin(Mathf.Clamp(Mathf.Abs(mainForward.Y), 0f, 1f));
		float spriteStretch = 1f / Mathf.Max(Mathf.Cos(mainPitch), 1e-4f);
		RenderingServer.GlobalShaderParameterSet("sprite_stretch", spriteStretch);
		// Flat-on-ground sprite stretch = 1/sin(camera pitch). Read by the
		// sprite_lit_flat shader. The depth axis (horizontal, away from
		// camera) projects to screen Y with sin(pitch); inverting that
		// recovers a 1:1 source-pixel-to-screen-pixel mapping for flat
		// sprites just like sprite_stretch does for upright. Behaves
		// reciprocally to spriteStretch — high-pitch (camera near vertical)
		// stretches upright sprites toward infinity but leaves flat sprites
		// at ~1, and vice versa.
		float spriteStretchFlat = 1f / Mathf.Max(Mathf.Sin(mainPitch), 1e-4f);
		RenderingServer.GlobalShaderParameterSet("sprite_stretch_flat", spriteStretchFlat);

		Vector3 pos = camera.GlobalPosition;
		Basis basis = camera.GlobalBasis;
		Vector3 right = basis.X;
		Vector3 up = basis.Y;
		Vector3 forward = basis.Z;

		float rx = right.Dot(pos);
		float ry = up.Dot(pos);
		float rz = forward.Dot(pos);

		float sx = Mathf.Floor(rx / chunky) * chunky;
		float sy = Mathf.Floor(ry / chunky) * chunky;
		float fracX = rx - sx;
		float fracY = ry - sy;

		camera.GlobalPosition = sx * right + sy * up + rz * forward;

		// fracX/fracY in [0, chunky); convert to texel units (in [0,1) of a
		// single inner texel) and then to UV.
		float texFracX = fracX / chunky;
		float texFracY = fracY / chunky;
		Vector2 uvOffset = new Vector2(texFracX / innerSize.X, texFracY / innerSize.Y);
		_subpixelTexelOffset = new Vector2(texFracX, texFracY);

		upscaleMaterial.SetShaderParameter("uv_offset", uvOffset);
		// uv_scale may drift if pixel_scale is changed at runtime without a
		// window resize; refresh it every frame so the CVar toggle works live.
		Vector2 uvScale = new Vector2(
			(float)screenSize.X / (scale * innerSize.X),
			(float)screenSize.Y / (scale * innerSize.Y));
		upscaleMaterial.SetShaderParameter("uv_scale", uvScale);

		if (sceneViewport.Size.X != innerSize.X || sceneViewport.Size.Y != innerSize.Y)
		{
			UpdateViewportSize();
		}
	}
}
