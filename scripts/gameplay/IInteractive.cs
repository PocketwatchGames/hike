using Godot;

public interface IInteractive
{
    Vector3 hudPosition { get; }
    bool CanInteract();
    bool CanActorInteract(Player player);
    void Complete();
    ulong GetInteractTime(Player player);
}
