// A discrete vocalization the AI requests the body perform — a server-side
// "what the creature wants to express" signal that carries no client content.
// Behaviors emit one through AIOutput.vocalization; the Mob scene maps each
// value to an Fx scene (and/or animation) via its _vocalizationEffects
// dictionary, so no PackedScene or other asset reference ever reaches the
// behavior layer.
//
// New entries should append to the end so existing serialized resources keep
// their numeric values.
public enum EVocalization
{
    Growl = 0,   // low warning held while standing ground (wary)
    Snarl = 1,   // sharp cry on committing to a fight (entering combat)
    Bark = 2,    // alarmed warning at a dangerous enemy in sight
    Whimper = 3, // injured cry on returning from a fight hurt
    Curious = 4, // inquisitive woof at a harmless creature noticed mid-sniff
    // Alarm shout on engaging / being hit: the loud one. Beyond the shared
    // vocalization handling (Fx + player-awareness noise) it also forces the
    // player to Discover the mob and broadcasts a directed investigation to
    // nearby mobs (allies converge, others glance) — see Mob.Vocalize.
    Yell = 5,
}
