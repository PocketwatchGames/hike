using Godot;

[GlobalClass]
public partial class SimData : Resource
{
    [Export] public float Gravity = 9.8f;
    [Export] public float VisibleTime = 0.25f;
}
