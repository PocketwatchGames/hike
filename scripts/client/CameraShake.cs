using System.Collections.Generic;
using Godot;

// Per-frame camera-shake driver owned by GameCamera. Two source kinds:
//
// - Impulses: one-shot magnitude + duration that decays linearly to 0 over
//   the duration. Magnitude scaled once at add-time by an optional distance
//   falloff against the player (range == 0 ⇒ no falloff, full strength).
//
// - Continuous registrations: a Node3D source carrying a magnitude + range.
//   Each frame the driver re-samples distance to the player and applies a
//   linear falloff. Sources self-register on _Ready / unregister on
//   _ExitTree (see ContinuousCameraShake).
//
// Multiple sources combine via max(), not sum — piling on three rain-of-
// arrows AoEs shouldn't triple the shake. The resulting intensity drives a
// camera-local random offset (right + up) added to the camera position
// before the chunky-pixel snap, so the shake quantizes onto the snap grid.
public class CameraShake
{
	private struct Impulse
	{
		public float magnitude;
		public float duration;
		public float elapsed;
	}

	private readonly List<Impulse> _impulses = new();
	private readonly List<ContinuousCameraShake> _continuous = new();

	public void AddImpulse(float magnitude, float duration, Vector3 position, float range, Vector3 playerPos)
	{
		if (magnitude <= 0f || duration <= 0f)
		{
			return;
		}
		float scaled = magnitude;
		if (range > 0f)
		{
			float dist = position.DistanceTo(playerPos);
			float falloff = Mathf.Max(0f, 1f - dist / range);
			scaled *= falloff;
			if (scaled <= 0f)
			{
				return;
			}
		}
		_impulses.Add(new Impulse { magnitude = scaled, duration = duration, elapsed = 0f });
	}

	public void RegisterContinuous(ContinuousCameraShake source)
	{
		if (source != null && !_continuous.Contains(source))
		{
			_continuous.Add(source);
		}
	}

	public void UnregisterContinuous(ContinuousCameraShake source)
	{
		_continuous.Remove(source);
	}

	public Vector3 Tick(float dt, Vector3 playerPos, Basis cameraBasis)
	{
		float intensity = 0f;

		// Impulse pass — advance elapsed, drop expired, combine via max.
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
			float current = imp.magnitude * t;
			if (current > intensity) { intensity = current; }
		}

		// Continuous pass — distance falloff against player per frame.
		for (int i = _continuous.Count - 1; i >= 0; i--)
		{
			ContinuousCameraShake src = _continuous[i];
			if (src == null || !GodotObject.IsInstanceValid(src))
			{
				_continuous.RemoveAt(i);
				continue;
			}
			float mag = src.magnitude;
			if (mag <= 0f) { continue; }
			if (src.range > 0f)
			{
				float dist = src.GlobalPosition.DistanceTo(playerPos);
				float falloff = Mathf.Max(0f, 1f - dist / src.range);
				mag *= falloff;
			}
			if (mag > intensity) { intensity = mag; }
		}

		if (intensity <= 0f)
		{
			return Vector3.Zero;
		}

		float angle = (float)GD.RandRange(0.0, Mathf.Tau);
		float rx = Mathf.Cos(angle) * intensity;
		float ry = Mathf.Sin(angle) * intensity;
		return cameraBasis.X * rx + cameraBasis.Y * ry;
	}
}
