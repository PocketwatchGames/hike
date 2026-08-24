using Godot;
using System.Collections.Generic;

// Implementations behind the `tp` / `spawn` / `give` / `setup` console verbs.
//
// These exist to collapse time-to-condition: the rest of the console observes
// the running game (the `debug_*` draws, the `*_probe` dumps) or sets global
// state (`time_of_day`, `weather`), but nothing put the player at a specific
// place with specific company, so reaching a test condition meant walking there
// in real time. That cost is paid on every manual check and every automated one.
//
// A dev harness, not gameplay — plain consts for tuning, and printing straight
// to the console rather than returning localized text.
public static class DebugVerbs
{
    // Teleport lands the party on the first voxel with headroom at or above the
    // requested Y, searching this far up. Covers a POI Y sitting exactly on the
    // ground top and a hand-typed Y buried in terrain; past that the request is
    // taken literally rather than silently relocating the player a long way.
    private const int MaxTeleportRise = 8;

    // Mobs spawn on a ring at this radius so a group doesn't stack inside itself
    // and the player can see what arrived.
    private const float SpawnRingRadius = 4f;
    private const int MaxSpawnCount = 32;

    // Dropped items pop up slightly so they settle rather than clipping the floor.
    private static readonly Vector3 GiveImpulse = new(0f, 2.5f, 0f);

    // Argument that asks a verb to list what it accepts. A BARE verb cannot do
    // this: ProcessCommand answers a value-less cvar with its current value and
    // never runs the callback, so there has to be an argument.
    private const string ListToken = "?";

    // --- tp -------------------------------------------------------------

    public static void Teleport(string arg)
    {
        Sim sim = Sim.Current;
        GameClient client = GameClient.Current;
        if (sim == null || client == null || sim.player == null)
        {
            GD.PrintErr("tp: no running game.");
            return;
        }

        WorldState ws = sim.WorldState;
        string[] tokens = Tokenize(arg);
        // CVarRegistry.ProcessCommand returns a bare cvar's VALUE without ever
        // invoking its callback, so a bare `tp` cannot reach this method at all.
        // `?` is the listing token for every verb here.
        if (tokens.Length == 0 || tokens[0] == ListToken)
        {
            ListPois(ws);
            return;
        }

        Vector3 target;
        if (tokens.Length >= 3
            && float.TryParse(tokens[0], out float x)
            && float.TryParse(tokens[1], out float y)
            && float.TryParse(tokens[2], out float z))
        {
            target = new Vector3(x, y, z);
        }
        else
        {
            string name = string.Join(" ", tokens);
            if (!TryFindPoi(ws, name, out target, out string error))
            {
                GD.PrintErr($"tp: {error}");
                ListPois(ws);
                return;
            }
        }

        Vector3 landing = ResolveStandable(ws, target);
        // Move the whole living party, not just the controlled member — leaving
        // the others behind strands the companions and any follow behavior.
        client.GatherPartyAt(landing);
        GD.Print($"tp: {landing.X:0.#} {landing.Y:0.#} {landing.Z:0.#}");
    }

    private static void ListPois(WorldState ws)
    {
        if (ws == null || ws.PointsOfInterest.Count == 0)
        {
            GD.Print("tp: no points of interest in this world. Usage: tp <name> | tp <x> <y> <z>");
            return;
        }
        var names = new List<string>(ws.PointsOfInterest.Keys);
        names.Sort();
        GD.Print($"tp: {names.Count} points of interest — {string.Join(", ", names)}");
    }

    private static bool TryFindPoi(WorldState ws, string name, out Vector3 position, out string error)
    {
        position = Vector3.Zero;
        error = null;
        if (ws == null)
        {
            error = "no world";
            return false;
        }
        foreach (KeyValuePair<string, Vector3> kv in ws.PointsOfInterest)
        {
            if (string.Equals(kv.Key, name, System.StringComparison.OrdinalIgnoreCase))
            {
                position = kv.Value;
                return true;
            }
        }

        var hits = new List<string>();
        foreach (string key in ws.PointsOfInterest.Keys)
        {
            if (key.Contains(name, System.StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(key);
            }
        }
        if (hits.Count == 1)
        {
            position = ws.PointsOfInterest[hits[0]];
            return true;
        }
        if (hits.Count > 1)
        {
            hits.Sort();
            error = $"'{name}' is ambiguous: {string.Join(", ", hits)}";
            return false;
        }
        error = $"unknown point of interest '{name}'";
        return false;
    }

    // First position at or above `p` with two voxels of headroom.
    private static Vector3 ResolveStandable(WorldState ws, Vector3 p)
    {
        if (ws == null)
        {
            return p;
        }
        int vx = Mathf.FloorToInt(p.X);
        int vz = Mathf.FloorToInt(p.Z);
        int vy = Mathf.FloorToInt(p.Y);
        for (int i = 0; i <= MaxTeleportRise; i++)
        {
            if (!Blocks.IsSolid(ws.GetBlockWorld(vx, vy + i, vz))
                && !Blocks.IsSolid(ws.GetBlockWorld(vx, vy + i + 1, vz)))
            {
                return new Vector3(p.X, vy + i, p.Z);
            }
        }
        return p;
    }

    // --- spawn ----------------------------------------------------------

    public static void Spawn(string arg)
    {
        Sim sim = Sim.Current;
        Player player = sim?.player;
        if (sim == null || player == null)
        {
            GD.PrintErr("spawn: no running game.");
            return;
        }

        string[] tokens = Tokenize(arg);
        if (tokens.Length == 0 || tokens[0] == ListToken)
        {
            GD.Print("spawn: usage `spawn <species> [count] [level]`. Known: "
                + string.Join(", ", DebugContentIndex.Names(DebugContentIndex.Species)));
            return;
        }

        SpeciesData species = DebugContentIndex.Resolve<SpeciesData>(DebugContentIndex.Species, tokens[0], out string error);
        if (species == null)
        {
            GD.PrintErr($"spawn: {error}. `spawn ?` lists the known species.");
            return;
        }

        int count = tokens.Length > 1 && int.TryParse(tokens[1], out int c) ? Mathf.Clamp(c, 1, MaxSpawnCount) : 1;
        int level = tokens.Length > 2 && int.TryParse(tokens[2], out int l) ? Mathf.Max(0, l) : 0;

        // A descriptor is the spawn-facing composition of a species with a level;
        // building one here is the same thing an authored spawn entry holds, and
        // it never reaches disk.
        var descriptor = new MobDescriptor { species = species, level = level };
        WorldState ws = sim.WorldState;
        Vector3 origin = player.GlobalPosition;

        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            float angle = Mathf.Tau * i / count;
            Vector3 at = origin + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * SpawnRingRadius;
            // Transient: a debug spawn must not be recorded in WorldState, or it
            // persists into the worldgen cache and re-materializes on every later
            // run of that world.
            if (sim.SpawnMobTransient(descriptor, ResolveStandable(ws, at), ESpawnConditions.None, level) != null)
            {
                spawned++;
            }
        }

        if (spawned < count)
        {
            GD.PrintErr($"spawn: only {spawned}/{count} placed — the rest had no resident chunk (move away from a world edge).");
        }
        GD.Print($"spawn: {spawned}x {species.ResourcePath.GetFile()} at level {level}");
    }

    // --- give -----------------------------------------------------------

    public static void Give(string arg)
    {
        Sim sim = Sim.Current;
        Player player = sim?.player;
        if (sim == null || player == null)
        {
            GD.PrintErr("give: no running game.");
            return;
        }

        string[] tokens = Tokenize(arg);
        if (tokens.Length == 0 || tokens[0] == ListToken)
        {
            GD.Print("give: usage `give <item> [count]`. Known: "
                + string.Join(", ", DebugContentIndex.Names(DebugContentIndex.Items)));
            return;
        }

        ItemData data = DebugContentIndex.Resolve<ItemData>(DebugContentIndex.Items, tokens[0], out string error);
        if (data == null)
        {
            GD.PrintErr($"give: {error}. `give ?` lists the known items.");
            return;
        }

        int count = tokens.Length > 1 && int.TryParse(tokens[1], out int c) ? Mathf.Max(1, c) : 1;
        ItemState state = data.CreateState();
        if (state == null)
        {
            GD.PrintErr($"give: '{tokens[0]}' produced no item state.");
            return;
        }
        if (count > 1)
        {
            state.SetCount(count);
        }

        // Dropped at the player's feet rather than pushed into the backpack: the
        // inventory refuses non-materials, and several item kinds (potions,
        // scrolls, fairy corpses) do their real work in the world-pickup path.
        // Dropping exercises what the player actually does.
        sim.SpawnLoot(player.GlobalPosition + Vector3.Up * 0.5f, GiveImpulse, state);
        GD.Print($"give: {count}x {data.ResourcePath.GetFile()} dropped at your feet");
    }

    // --- setup ----------------------------------------------------------

    public static void Setup(string arg)
    {
        TestScenarioData[] scenarios = Sim.Current?.SimData?.testScenarios;
        if (scenarios == null || scenarios.Length == 0)
        {
            GD.PrintErr("setup: no scenarios authored on SimData.testScenarios.");
            return;
        }

        string name = arg?.Trim() ?? "";
        if (name.Length == 0 || name == ListToken)
        {
            GD.Print("setup: authored scenarios —");
            foreach (TestScenarioData s in scenarios)
            {
                GD.Print($"  {s?.scenarioName,-20} {s?.description}");
            }
            return;
        }

        TestScenarioData match = FindScenario(scenarios, name, out string error);
        if (match == null)
        {
            GD.PrintErr($"setup: {error}. `setup ?` lists the authored scenarios.");
            return;
        }

        GD.Print($"setup: {match.scenarioName} — {match.description}");
        foreach (string raw in (match.commands ?? "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//"))
            {
                continue;
            }
            GD.Print($"  > {line}");
            string result = CVarRegistry.ProcessCommand(line);
            if (!string.IsNullOrEmpty(result))
            {
                GD.Print($"    {result}");
            }
        }
    }

    private static TestScenarioData FindScenario(TestScenarioData[] scenarios, string name, out string error)
    {
        error = null;
        var hits = new List<TestScenarioData>();
        foreach (TestScenarioData s in scenarios)
        {
            if (string.IsNullOrEmpty(s?.scenarioName))
            {
                continue;
            }
            if (string.Equals(s.scenarioName, name, System.StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
            if (s.scenarioName.Contains(name, System.StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(s);
            }
        }
        if (hits.Count == 1)
        {
            return hits[0];
        }
        if (hits.Count > 1)
        {
            var names = new List<string>();
            foreach (TestScenarioData s in hits)
            {
                names.Add(s.scenarioName);
            }
            names.Sort();
            error = $"'{name}' is ambiguous: {string.Join(", ", names)}";
            return null;
        }
        error = $"unknown scenario '{name}'";
        return null;
    }

    private static string[] Tokenize(string arg)
    {
        return string.IsNullOrWhiteSpace(arg)
            ? System.Array.Empty<string>()
            : arg.Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
    }
}
