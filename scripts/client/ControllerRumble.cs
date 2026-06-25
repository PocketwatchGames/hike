using System.Collections.Generic;
using Godot;

// Per-frame controller-rumble driver, the haptic sibling of CameraShake.
// Owned and ticked by GameClient (NOT the camera — rumble is a controller-
// feel concern, not a framing one). Trigger it exactly like screen shake:
// GameClient.Current?.Rumble?.AddImpulse(...) from code, or author a
// ControllerRumble ItemEvent on an action timeline.
//
// Two motor channels, mirroring a standard gamepad: `strong` is the
// low-frequency (heavy) motor — big impacts, explosions; `weak` is the
// high-frequency (buzzy) motor — light taps, ticks. Each impulse decays
// linearly to 0 over its duration. Multiple impulses combine per-channel via
// max(), not sum — three overlapping AoEs shouldn't triple the buzz. An
// optional distance falloff against the player scales magnitude at add-time
// (range == 0 ⇒ no falloff, full strength), so a far-off stomp rumbles less
// than one in your face.
public class ControllerRumble
{
	private struct Impulse
	{
		public float weak;
		public float strong;
		public float duration;
		public float elapsed;
	}

	private readonly List<Impulse> _impulses = new();

	// Last envelope issued to the motors, so we only re-issue when it changes
	// (the engine restarts the motor on each StartJoyVibration call).
	private float _lastWeak;
	private float _lastStrong;
	private bool _vibrating;

	// Re-issue indefinitely (duration 0 = until stopped); the per-frame Tick
	// owns the decay and the eventual StopJoyVibration. Anything below this
	// rounds to silent so the motor doesn't idle-hum on a dust-mote impulse.
	private const float MinMagnitude = 0.01f;

	public void AddImpulse(float weak, float strong, float duration, Vector3 position, float range, Vector3 playerPos)
	{
		if (duration <= 0f || (weak <= 0f && strong <= 0f))
		{
			return;
		}
		float scale = 1f;
		if (range > 0f)
		{
			float dist = position.DistanceTo(playerPos);
			scale = Mathf.Max(0f, 1f - dist / range);
			if (scale <= 0f)
			{
				return;
			}
		}
		_impulses.Add(new Impulse
		{
			weak = weak * scale,
			strong = strong * scale,
			duration = duration,
			elapsed = 0f,
		});
	}

	// Advances impulses and drives the connected joypads' motors. dt is
	// wall-clock seconds (rumble is presentational — it doesn't slow under
	// slow-mo). Master gate + scale come from CVars.
	public void Tick(float dt)
	{
		if (!CVars.rumble.Value)
		{
			StopAll();
			return;
		}

		float weak = 0f;
		float strong = 0f;
		for (int i = _impulses.Count - 1; i >= 0; i--)
		{
			Impulse imp = _impulses[i];
			imp.elapsed += dt;
			if (imp.elapsed >= imp.duration)
			{
				_impulses.RemoveAt(i);
				continue;
			}
			_impulses[i] = imp;
			float t = 1f - (imp.elapsed / imp.duration);
			if (imp.weak * t > weak) { weak = imp.weak * t; }
			if (imp.strong * t > strong) { strong = imp.strong * t; }
		}

		float masterScale = Mathf.Max(0f, CVars.rumbleScale.Value);
		weak = Mathf.Clamp(weak * masterScale, 0f, 1f);
		strong = Mathf.Clamp(strong * masterScale, 0f, 1f);

		if (weak < MinMagnitude && strong < MinMagnitude)
		{
			StopAll();
			return;
		}

		// Only re-issue when the envelope moves meaningfully — each
		// StartJoyVibration call restarts the motor, so feeding it an
		// unchanged value every frame would stutter the buzz.
		if (!_vibrating || Mathf.Abs(weak - _lastWeak) > MinMagnitude || Mathf.Abs(strong - _lastStrong) > MinMagnitude)
		{
			foreach (int device in Input.GetConnectedJoypads())
			{
				Input.StartJoyVibration(device, weak, strong, 0f);
			}
			_lastWeak = weak;
			_lastStrong = strong;
			_vibrating = true;
		}
	}

	// Drops all pending impulses and silences every motor. Called when the
	// game pauses / console opens (GameClient gates Tick), and on teardown.
	public void StopAll()
	{
		_impulses.Clear();
		if (!_vibrating)
		{
			return;
		}
		foreach (int device in Input.GetConnectedJoypads())
		{
			Input.StopJoyVibration(device);
		}
		_lastWeak = 0f;
		_lastStrong = 0f;
		_vibrating = false;
	}
}
