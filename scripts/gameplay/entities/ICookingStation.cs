// Somewhere the CookingScreen cooks: the camp's fire or a standalone cookpot.
// They differ in what the camp brings with it.
public interface ICookingStation
{
    // Experimentation inputs the CookingPanel binds to. Drained back to the cook
    // whenever the screen closes.
    ItemState[] CookingSlots { get; }
    ECampfireType CampfireType { get; }
    // At camp the party stash is an ingredient source, and a recipe discovered
    // there is banked into party knowledge at once. Away from camp a discovery
    // stays provisional until the next camp, like any other field lesson.
    bool IsCamp { get; }
}
