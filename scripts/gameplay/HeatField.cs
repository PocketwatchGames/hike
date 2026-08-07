using Godot;
using System;
using System.Collections.Generic;

// Source-of-truth for the heat shimmer post-process. Owns a small
// player-centric R8 texture that heat_shimmer.gdshader samples to scale
// per-fragment UV warp.
//
// Two contributors stamp the field every Tick:
//   - Ambient air temperature: a uniform baseline, raised when
//     SampleAirTemperature reads "hot". Drives whole-frame shimmer in
//     the desert / direct-sun zones.
//   - Active WarmthZones: additive disks at each registered zone's
//     world position, max-merged with the baseline. Drives localized
//     shimmer above fire traps and campfires regardless of biome.
//
// Disabled cleanly via `heat_shimmer 0`: the field zeroes out, the
// shader's per-fragment heat reads zero, warp collapses to identity.
[GlobalClass]
public partial class HeatField : Node3D
{
	[ExportGroup("Heat Shimmer")]
	// Texture side length (cells). Locked at boot — the ImageTexture is
	// allocated at this size in _Ready and never resized. Larger = sharper
	// disk edges + finer gradient; cost is N*N bytes per per-frame upload.
	[Export(PropertyHint.Range, "32,1024,1")] public int resolution = 256;
	// Total side length in meters covered by the heat field. Centered on the
	// player; field UVs are 0 at (player − size/2) and 1 at (player + size/2).
	[Export(PropertyHint.Range, "8,512,1")] public float sizeMeters = 64f;
	// Ambient air-temperature ramp (°F). Below START = no shimmer, above
	// FULL = max shimmer; linear interpolation between.
	[Export(PropertyHint.Range, "0,200,0.5")] public float ambientStartF = 90f;
	[Export(PropertyHint.Range, "0,200,0.5")] public float ambientFullF = 120f;
	// WarmthZone shimmer intensity = clamp(warmingTemperature / divisor, 0, 1).
	// 30°F warming hits ~1.0 intensity; the 20°F campfire default lands at ~0.67.
	[Export(PropertyHint.Range, "1,200,0.5")] public float warmIntensityDivisor = 30f;
	// Inner fraction of stamped disks that paints at full intensity. Outside
	// this fraction falls linearly to 0 at the disk edge.
	[Export(PropertyHint.Range, "0,1,0.05")] public float diskInnerFraction = 0.5f;

	// Mirrors the `resolution` export, captured once in _Ready (the texture is
	// allocated at that size and never resized).
	private int _resolution;
	private byte[] _buffer;
	private Image _image;
	private ImageTexture _texture;
	private Sim _world;

	private readonly HashSet<WarmthZone> _activeZones = new();

	public override void _Ready()
	{
		_resolution = Mathf.Max(8, resolution);
		_buffer = new byte[_resolution * _resolution];
		_image = Image.CreateEmpty(_resolution, _resolution, false, Image.Format.R8);
		_texture = ImageTexture.CreateFromImage(_image);
		// Globals are declared in project.godot with a PlaceholderTexture2D
		// so heat_shimmer.gdshader compiles when opened in the editor;
		// runtime swaps in this ImageTexture before the first frame.
		ShaderGlobals.Register("heat_field", RenderingServer.GlobalShaderParameterType.Sampler2D, _texture);
		ShaderGlobals.Register("heat_field_origin_xz", RenderingServer.GlobalShaderParameterType.Vec2, Vector2.Zero);
		ShaderGlobals.Register("heat_field_size", RenderingServer.GlobalShaderParameterType.Float, sizeMeters);
	}

	public override void _ExitTree()
	{
		// Unbind the per-session ImageTexture before it's collected.
		ShaderGlobals.ResetToProjectDefault("heat_field");
	}

	public void Initialize(Sim sim)
	{
		_world = sim;
	}

	public void RegisterZone(WarmthZone zone)
	{
		if (zone != null)
		{
			_activeZones.Add(zone);
		}
	}

	public void UnregisterZone(WarmthZone zone)
	{
		_activeZones.Remove(zone);
	}

	public void Tick()
	{
		if (_world?.player == null)
		{
			return;
		}

		Vector3 playerPos = _world.player.GlobalPosition;
		float warmDivisor = warmIntensityDivisor;

		Vector2 originXZ = new Vector2(
			playerPos.X - sizeMeters * 0.5f,
			playerPos.Z - sizeMeters * 0.5f);

		bool enabled = CVars.heatShimmer.Value;

		float baseline = 0f;
		if (enabled)
		{
			float ambientRange = Mathf.Max(ambientFullF - ambientStartF, 0.001f);
			float airTempF = Sim.Current?.SampleAirTemperature(playerPos) ?? ambientStartF;
			baseline = Mathf.Clamp((airTempF - ambientStartF) / ambientRange, 0f, 1f);
		}

		byte baselineByte = (byte)Mathf.RoundToInt(baseline * 255f);
		Array.Fill(_buffer, baselineByte);

		if (enabled)
		{
			float metersPerCell = sizeMeters / _resolution;
			foreach (WarmthZone zone in _activeZones)
			{
				if (zone == null || !IsInstanceValid(zone))
				{
					continue;
				}
				float radius = GetZoneRadius(zone);
				float intensity = Mathf.Clamp(zone.warmingTemperature / warmDivisor, 0f, 1f);
				StampDisk(zone.GlobalPosition, originXZ, metersPerCell, radius, intensity, diskInnerFraction);
			}
		}

		_image.SetData(_resolution, _resolution, false, Image.Format.R8, _buffer);
		_texture.Update(_image);

		RenderingServer.GlobalShaderParameterSet("heat_field_origin_xz", originXZ);
		RenderingServer.GlobalShaderParameterSet("heat_field_size", sizeMeters);
	}

	// WarmthZones author their footprint as a CollisionShape3D in the .tscn
	// rather than as a numeric field, so we sniff the first child shape.
	private static float GetZoneRadius(WarmthZone zone)
	{
		foreach (Node child in zone.GetChildren())
		{
			if (child is CollisionShape3D cs && cs.Shape != null)
			{
				if (cs.Shape is SphereShape3D sphere)
				{
					return sphere.Radius;
				}
				if (cs.Shape is CapsuleShape3D capsule)
				{
					return capsule.Radius;
				}
				if (cs.Shape is CylinderShape3D cyl)
				{
					return cyl.Radius;
				}
				if (cs.Shape is BoxShape3D box)
				{
					return Mathf.Max(box.Size.X, box.Size.Z) * 0.5f;
				}
			}
		}
		return 4f;
	}

	private void StampDisk(Vector3 worldPos, Vector2 originXZ, float metersPerCell, float radiusMeters, float intensity, float diskInnerFraction)
	{
		if (intensity <= 0f || radiusMeters <= 0f)
		{
			return;
		}
		float cellRadius = radiusMeters / metersPerCell;
		float centerX = (worldPos.X - originXZ.X) / metersPerCell;
		float centerY = (worldPos.Z - originXZ.Y) / metersPerCell;

		int minX = Mathf.Max(0, Mathf.FloorToInt(centerX - cellRadius));
		int maxX = Mathf.Min(_resolution - 1, Mathf.CeilToInt(centerX + cellRadius));
		int minY = Mathf.Max(0, Mathf.FloorToInt(centerY - cellRadius));
		int maxY = Mathf.Min(_resolution - 1, Mathf.CeilToInt(centerY + cellRadius));

		if (maxX < minX || maxY < minY)
		{
			return;
		}

		float radiusSq = cellRadius * cellRadius;
		float innerRadius = cellRadius * diskInnerFraction;
		float falloffRange = cellRadius - innerRadius;

		for (int y = minY; y <= maxY; y++)
		{
			float dy = y + 0.5f - centerY;
			for (int x = minX; x <= maxX; x++)
			{
				float dx = x + 0.5f - centerX;
				float distSq = dx * dx + dy * dy;
				if (distSq > radiusSq)
				{
					continue;
				}
				float falloff;
				if (falloffRange <= 0f)
				{
					falloff = 1f;
				}
				else
				{
					float dist = Mathf.Sqrt(distSq);
					falloff = Mathf.Clamp(1f - (dist - innerRadius) / falloffRange, 0f, 1f);
				}
				int idx = y * _resolution + x;
				byte newVal = (byte)Mathf.RoundToInt(intensity * falloff * 255f);
				if (newVal > _buffer[idx])
				{
					_buffer[idx] = newVal;
				}
			}
		}
	}
}
