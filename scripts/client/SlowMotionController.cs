using Godot;

// Cinematic slow-motion "death cam". On Trigger it eases Engine.TimeScale down to
// a crawl and punches the camera zoom in (orthographic Size), with a radial zoom
// blur tracking the lunge. The depth is HELD until Release(), then everything
// eases back to real time and the resting zoom.
//
// Wired into the game scene and driven by GameClient: Update() runs each frame
// (between the camera follow and the pixel-snap, so the Size override lands
// before the rig reads it) and Trigger/Release are called from the player
// death/respawn hooks. The transition is wall-clock timed (Time.GetTicksMsec) so
// it isn't slowed by the very time scale it sets.
//
// Currently fired only on player death; the Trigger/Release API is intentionally
// generic so a future "cleared a major engagement" cue can reuse it.
[GlobalClass]
public partial class SlowMotionController : Node
{
	[Export] public GameCamera camera;

	[ExportGroup("Slow Motion")]
	// Engine.TimeScale at the held depth. 1 = no slowdown; lower = heavier crawl.
	// Note: everything driven by frame delta slows with this (physics, animation,
	// AI, the day clock, and the DeathScreen fade) — which is the point.
	[Export(PropertyHint.Range, "0.05,1,0.01")] public float timeScale = 0.2f;

	[ExportGroup("Zoom")]
	// How far to punch in, expressed as pixel-scale steps: the camera zooms by
	// (pixelScale + steps) / pixelScale — i.e. "in by another pixel size" is 1
	// step (~1.25x at the default pixel_scale of 4). This is the zoom MAGNITUDE
	// (one more pixel of scale's worth of framing); the ortho Size shrinks to
	// tighten on the player. 0 disables the zoom.
	[Export(PropertyHint.Range, "0,4,1")] public int zoomPixelSteps = 1;

	[ExportGroup("Transition")]
	// Wall-clock seconds to ease into the held depth (and to ease back out on
	// Release). Real time — not scaled by the time scale being applied.
	[Export(PropertyHint.Range, "0.05,3,0.05")] public float transitionSeconds = 0.4f;
	// Peak radial zoom-blur strength, reached at the start of the punch-in (and
	// the start of the ease-out), tapering to 0 at the held depth — it tracks the
	// zoom velocity, which is highest as the camera leaves each resting point.
	[Export(PropertyHint.Range, "0,1,0.05")] public float motionBlurPeak = 0.8f;

	// Current depth in [0, 1] (0 = real time / resting zoom, 1 = full slow-mo +
	// zoom) and the depth it's easing toward (0 or 1). Reversing mid-transition is
	// just a target flip, so a Release during the punch-in glides back continuously.
	float _depth;
	float _target;
	bool _active;
	ulong _lastRealMs;

	// camera.Size captured at Trigger so the ease and the restore ride the same
	// anchor even if the resting Size is tweaked while engaged.
	float _baseSize = 1f;

	// Radial zoom-blur strength for ScreenEffectsController to composite into the
	// post-process pass. 0 whenever idle.
	public float RadialBlur { get; private set; }
	public bool IsActive => _active;

	// Begin (or hold) the slow-mo punch-in. No-op if disabled via CVar or already
	// engaged and not currently easing back out — a re-trigger during the hold
	// won't restart the zoom.
	public void Trigger()
	{
		if (!CVars.slowMotion.Value || camera == null) { return; }
		if (_active && _target >= 1f) { return; }
		if (!_active)
		{
			_baseSize = camera.Size;
			_lastRealMs = Time.GetTicksMsec();
		}
		_active = true;
		_target = 1f;
	}

	// Ease back to real time and the resting zoom. No-op if not engaged.
	public void Release()
	{
		if (!_active) { return; }
		_target = 0f;
	}

	// Per-frame drive. Advances the wall-clock depth, applies Engine.TimeScale and
	// the camera.Size override, and updates the blur. GameClient calls this after
	// the camera follow and before the pixel-snap.
	public void Update()
	{
		if (!_active) { return; }

		ulong now = Time.GetTicksMsec();
		float dt = (now - _lastRealMs) / 1000f;
		_lastRealMs = now;

		float rate = transitionSeconds > 0f ? dt / transitionSeconds : 1f;
		_depth = Mathf.MoveToward(_depth, _target, rate);

		// Ease-out so the slow-mo leaves fast and settles gently at each end.
		float eased = 1f - (1f - _depth) * (1f - _depth);

		Engine.TimeScale = Mathf.Lerp(1f, timeScale, eased);

		int basePixelScale = Mathf.Max(1, CVars.pixelScale.Value);
		float zoomFactor = (basePixelScale + zoomPixelSteps) / (float)basePixelScale;
		camera.Size = Mathf.Lerp(_baseSize, _baseSize / zoomFactor, eased);

		// Tracks the zoom velocity: heaviest as the camera leaves a resting point
		// (depth near 0, where the ease-out is fastest), zero at the held depth.
		RadialBlur = (1f - eased) * motionBlurPeak;

		// Fully eased back out: restore real time and go idle.
		if (_target <= 0f && _depth <= 0f)
		{
			Engine.TimeScale = 1f;
			camera.Size = _baseSize;
			RadialBlur = 0f;
			_active = false;
		}
	}

	// Safety net: a quit / scene change mid-slow-mo would otherwise strand
	// Engine.TimeScale globally (the menu would run in slow-mo). Reset it if still
	// engaged.
	public override void _ExitTree()
	{
		if (_active)
		{
			Engine.TimeScale = 1f;
		}
	}
}
