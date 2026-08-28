using Godot;

// Teaches an alchemy spell — records it into SimState.KnownSpells so it
// shows up on the alchemy campfire screen (and can be attuned) before the player
// has ever cast it. LearnSpell also silently identifies the spell so the button
// reads with its real name instead of "Unknown Potion".
//
// This is the spell analog of RecipeTeachable: used as an "I know this spell"
// entry in WorldStartData.initialKnowledge, and as the concept payload on a spell
// scroll / NPC reward that grants a castable spell.
[GlobalClass]
public partial class SpellTeachable : TeachableConcept
{
    [Export] public SpellData spell;

    public override string GetDisplayName()
    {
        return spell != null ? spell.displayName.ToString() : string.Empty;
    }

    public override bool Teach(Player player)
    {
        if (player == null || spell == null)
        {
            return false;
        }
        SimState sim = player.Sim?.WorldState?.SimState;
        return sim != null && sim.LearnSpell(spell);
    }

    public override bool IsKnown(Player player)
    {
        if (spell == null)
        {
            return false;
        }
        return player?.Sim?.WorldState?.SimState?.IsSpellKnown(spell) ?? false;
    }
}
