public interface IInteractive
{
    bool CanInteract();
    bool CanActorInteract(Player player);
    void Complete();
}
