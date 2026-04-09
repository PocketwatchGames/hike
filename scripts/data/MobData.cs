using Godot;

[GlobalClass]
public partial class MobData : Resource
{
    [Export] public float VisionRange = 15f;
    [Export] public float VisionDotPower = 0.5f;
    [Export] public float AggroIncreaseSpeed = 0.5f;
    [Export] public float AggroRelaxationSpeed = 0.1f;
    [Export] public float MinAggroDelta = 0.05f;
    [Export] public float AlertRelaxationTime = 3f;
    [Export] public float AggroThresholdAlert = 1f;
    [Export] public float visibilityLightMax = 0.75f;
    [Export] public float visibilityMovementMin = 0.5f;
    [Export] public float visibilityMovementPower = 2;
    [Export] public float maxVisibilitySpeed = 5f;
    [Export] public float PlayerSeenRelaxationTime = 3f;
    [Export] public float PlayerPerceptionRelaxationSpeed = 3f;
    [Export] public float PlayerPerceptionSpeed = 5f;
}
