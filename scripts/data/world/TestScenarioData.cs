using Godot;

// A named, authored test setup: the console commands that put a running game
// into one specific condition so it can be looked at.
//
// A scenario is deliberately just a command list rather than a set of typed
// fields. Every knob worth reaching already exists as a cvar (`time_of_day`,
// `weather`, `tp`, `spawn`, `give`, the whole `debug_*` family), so authoring a
// new scenario costs one resource and no code, and a scenario automatically
// picks up any cvar added later. Lines run top to bottom through
// CVarRegistry.ProcessCommand — the same path the console types into.
//
// Not [Tool]: its owner (SimData) isn't either. See the [Tool] closure rule in
// CLAUDE.md before adding a typed Resource field here.
[GlobalClass]
public partial class TestScenarioData : Resource
{
    // The name typed after `setup`. Case-insensitive, and a unique prefix is
    // enough — keep it short.
    [Export] public string scenarioName = "";

    // What this scenario is for, shown by a bare `setup`. One line.
    [Export] public string description = "";

    // One console command per line; blank lines and `//` lines are skipped.
    // Ordering matters where a command depends on an earlier one (teleport
    // before spawning, so the mob lands on a resident chunk).
    [Export(PropertyHint.MultilineText)] public string commands = "";
}
