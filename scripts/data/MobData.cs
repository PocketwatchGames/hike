using Godot;

[GlobalClass]
public partial class MobData : Resource
{
    [Export] public float VisionRange = 15f;
    [Export] public float VisionDotPower = 0.5f;
    [Export] public float PerceptionIncreaseSpeed = 0.5f;
    [Export] public float PerceptionRelaxationSpeed = 0.1f;
    [Export] public float MinPerceptionDelta = 0.05f;
    [Export] public float PerceptionThresholdAlert = 1f;
    [Export] public float visibilityLightMax = 0.75f;
    [Export] public float visibilityMovementMin = 0.5f;
    [Export] public float visibilityMovementPower = 2;
    [Export] public float maxVisibilitySpeed = 5f;
    [Export] public float PlayerSeenRelaxationTime = 3f;
    [Export] public float PlayerPerceptionRelaxationSpeed = 3f;
    [Export] public float PlayerPerceptionSpeed = 5f;
    [Export] public bool canBurrow = false;
    [Export] public float hideRange = 20f;
    [Export] public float maxHealth = 10f;
    [Export] public float yellVolume = 15;
    [Export] public StringName defaultBehavior = "Idle";
    [Export] public bool dangerous = false;
    [Export] public BrainData brain;
}
