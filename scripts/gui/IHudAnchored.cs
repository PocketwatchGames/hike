using Godot;

// Something the HUD can float a widget over: a speech bubble's tail points at
// hudPosition.
public interface IHudAnchored
{
	Vector3 hudPosition { get; }
}
