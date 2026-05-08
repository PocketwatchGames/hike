using System.Collections.Generic;
using Godot;

// Area3D that suppresses and clears the player's Wet status while they stand
// inside it. Authored on lit heat sources (campfire, brazier) and toggled
// active/inactive by the source's lifecycle — see Torch._active.
//
// Tracks every overlapping Player regardless of the active flag so a fire
// that lights while a soggy player is already standing nearby can still dry
// them, and a doused fire can release a player who's still inside the zone
// without waiting for them to walk out and back in.
[GlobalClass]
public partial class WarmthZone : Area3D
{
	// Degrees F this zone adds to the player's sampled environmental
	// temperature while they're inside. Summed across every overlapping
	// active warmth zone in Player.SampleEnvironmentTemperature.
	[Export] public float warmingTemperature = 20f;

	private bool _active = true;
	private readonly List<Player> _overlapping = new();

	public override void _Ready()
	{
		CollisionLayer = 0;
		CollisionMask = (uint)ECollisionLayer.Player;
		BodyEntered += OnBodyEntered;
		BodyExited += OnBodyExited;
	}

	public void SetActive(bool active)
	{
		if (_active == active)
		{
			return;
		}
		_active = active;
		for (int i = 0; i < _overlapping.Count; i++)
		{
			Player p = _overlapping[i];
			if (p == null)
			{
				continue;
			}
			if (_active)
			{
				p.EnterWarmthZone(this);
			}
			else
			{
				p.ExitWarmthZone(this);
			}
		}
	}

	private void OnBodyEntered(Node3D body)
	{
		if (body is not Player p)
		{
			return;
		}
		_overlapping.Add(p);
		if (_active)
		{
			p.EnterWarmthZone(this);
		}
	}

	private void OnBodyExited(Node3D body)
	{
		if (body is not Player p)
		{
			return;
		}
		_overlapping.Remove(p);
		if (_active)
		{
			p.ExitWarmthZone(this);
		}
	}
}
