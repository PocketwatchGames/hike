using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Which world a run is playing, as a load has to start it: the .hike it was
// loaded from (empty for a generated world) and the WorldGenData that still
// binds the terrain palette for either kind. Set by Main when the world is
// built; recorded in a save's header. Fingerprint names the exact BUILD of that
// world (a .hike's bake id, a generated world's worldgen fingerprint): a save
// only records how the run changed it, so it is refused against any other.
public readonly record struct WorldOrigin(string WorldFile, string GeneratorPath, string Fingerprint);

// A save is the party WAKING AT A CAMPFIRE AT SUNRISE. It is written at that
// moment (GameClient.AutosaveAtWake) and a load reproduces it, so everything a
// sleep-to-sunrise resets — health, transient effects, mobs, dropped loot,
// the leader / spell pick — is never saved: the load re-derives it the way a
// real wake does. The day's rolls (weather, well-rested) are seeded from
// RunSeed + DayNumber, so they come back identical with nothing else stored.
//
// Layout: the HEADER (which world, the run seed, the clock, the campfire) is
// read before any world exists; then the resource table every reference in the
// body indexes — EntitySerializer's, the one WorldFile uses, so a reference into
// the world's authoring document is stored by value here too; then the BODY,
// applied in the steps SaveFile names.
public static class SaveGame
{
	// Anything that isn't exactly this version is rejected — pre-release saves
	// are discarded, never upgraded (CLAUDE.md "Priorities").
	private const int SAVE_VERSION = 11;

	public static string GlobalPath(string path) => ProjectSettings.GlobalizePath(path);

	public static bool Exists(string path) => File.Exists(GlobalPath(path));

	// `nodeFor` finds the Player node hosting a roster member — where the member's
	// inventory, effects and body live.
	public static void Save(string path, WorldState worldState, Func<PlayerState, Player> nodeFor, Vector3 campfire)
	{
		SimState simState = worldState.SimState;
		if (simState.Party == null)
		{
			throw new InvalidOperationException("no party to save");
		}

		// Hashed before the shared table opens: the hash is of a standalone list,
		// the same bytes CaptureEntityBaseline hashed.
		List<Vector3I> changedChunks = FindChangedChunks(worldState);

		// The body is buffered: the table is only complete once the last
		// reference has been interned, and it has to be written ahead of them.
		EntitySerializer.WritePathTable table = EntitySerializer.BeginSharedWrite(worldState.AuthoringDocument);
		byte[] body;
		try
		{
			using var ms = new MemoryStream();
			using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
			{
				// --- SaveFile.ApplyEntities ---
				WriteEntities(bw, worldState, changedChunks);
				// --- SaveFile.ApplyParty ---
				simState.Party.Serialize(bw);
				simState.SerializeStashes(bw);
				// --- SaveFile.ApplyAfterSpawn ---
				WriteMembers(bw, simState.Party, nodeFor);
				simState.ScriptVars.Serialize(bw);
				simState.QuestLog.Serialize(bw);
				simState.SerializeTreasureMaps(bw);
			}
			body = ms.ToArray();
		}
		finally
		{
			EntitySerializer.EndSharedWrite();
		}

		// Written beside and moved over, so a failed autosave never destroys the
		// only save there is.
		string target = GlobalPath(path);
		string temp = target + ".tmp";
		using (var stream = new FileStream(temp, FileMode.Create))
		using (var w = new BinaryWriter(stream))
		{
			w.Write(SAVE_VERSION);
			w.Write(worldState.WorldName ?? "");
			w.Write(worldState.Origin.WorldFile ?? "");
			w.Write(worldState.Origin.GeneratorPath ?? "");
			w.Write(worldState.Origin.Fingerprint ?? "");
			w.Write(worldState.RunSeed);
			w.Write(worldState.DayNumber);
			w.Write(worldState.GameTimeMs);
			w.Write(campfire.X);
			w.Write(campfire.Y);
			w.Write(campfire.Z);
			EntitySerializer.WriteTable(w, table);
			w.Write(body);
		}
		File.Move(temp, target, overwrite: true);
	}

	// Reads the header and the table, and holds the body for SaveFile. Throws
	// InvalidDataException naming the problem on a version mismatch.
	public static SaveFile Read(string path)
	{
		byte[] bytes = File.ReadAllBytes(GlobalPath(path));
		var r = new BinaryReader(new MemoryStream(bytes));
		int version = r.ReadInt32();
		if (version != SAVE_VERSION)
		{
			throw new InvalidDataException(
				$"Unsupported save version {version} (this build writes {SAVE_VERSION}). " +
				"Pre-release saves are not migrated — delete it and start a new game.");
		}
		r.ReadString();   // world name: for the profile screen, which reads it via ReadWorldName
		var origin = new WorldOrigin(r.ReadString(), r.ReadString(), r.ReadString());
		int runSeed = r.ReadInt32();
		int dayNumber = r.ReadInt32();
		ulong gameTimeMs = r.ReadUInt64();
		var campfire = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
		EntitySerializer.ReadPathTable table = EntitySerializer.ReadTable(r);
		return new SaveFile(origin, runSeed, dayNumber, gameTimeMs, campfire, table, r);
	}

	// The name of the world a save plays, read off the header alone so a listing
	// never touches the body or loads a resource. Null when the file can't be
	// read or is not this build's SAVE_VERSION — Read would refuse it too.
	public static string ReadWorldName(string path)
	{
		try
		{
			using var stream = new FileStream(GlobalPath(path), FileMode.Open, System.IO.FileAccess.Read);
			using var r = new BinaryReader(stream);
			if (r.ReadInt32() != SAVE_VERSION)
			{
				return null;
			}
			string name = r.ReadString();
			if (!string.IsNullOrEmpty(name))
			{
				return name;
			}
			// A world whose WorldStartData names nothing: fall back to its file.
			string worldFile = r.ReadString();
			string generator = r.ReadString();
			return (string.IsNullOrEmpty(worldFile) ? generator : worldFile).GetFile().GetBaseName();
		}
		catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
		{
			GD.PrintErr($"[Save] Can't read '{path}': {e.Message}");
			return null;
		}
	}

	public static void Delete(string path)
	{
		File.Delete(GlobalPath(path));
	}

	// Hash every chunk's entity bucket as the world was built, before a save is
	// applied. What FindChangedChunks compares against.
	public static void CaptureEntityBaseline(WorldState worldState)
	{
		var baseline = new Dictionary<Vector3I, ulong>(worldState._entities.Count);
		foreach (KeyValuePair<Vector3I, List<EntitySimState>> kv in worldState._entities)
		{
			baseline[kv.Key] = HashBucket(kv.Value);
		}
		worldState.EntityBaseline = baseline;
	}

	// Every chunk whose entity bucket no longer serializes to what it did when
	// the world was built — found by CONTENT, so no mutation anywhere has to
	// remember to mark anything. An emptied chunk, and one that had no entities
	// at all, compare against the hash of an empty list.
	private static List<Vector3I> FindChangedChunks(WorldState worldState)
	{
		Dictionary<Vector3I, ulong> baseline = worldState.EntityBaseline
			?? throw new InvalidOperationException("no entity baseline was captured for this world");
		ulong empty = HashBucket(null);
		var coords = new HashSet<Vector3I>(baseline.Keys);
		coords.UnionWith(worldState._entities.Keys);
		var changed = new List<Vector3I>();
		foreach (Vector3I coord in coords)
		{
			ulong was = baseline.TryGetValue(coord, out ulong h) ? h : empty;
			if (HashBucket(worldState.GetEntities(coord)) != was)
			{
				changed.Add(coord);
			}
		}
		return changed;
	}

	// FNV-1a over the bucket's standalone serialization. Standalone (its own
	// path table) so the bytes depend on nothing but the bucket.
	private static ulong HashBucket(List<EntitySimState> bucket)
	{
		using var ms = new MemoryStream();
		using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
		{
			EntitySerializer.WriteList(bw, bucket);
		}
		const ulong FNV_OFFSET = 14695981039346656037UL;
		const ulong FNV_PRIME = 1099511628211UL;
		ulong hash = FNV_OFFSET;
		byte[] bytes = ms.GetBuffer();
		for (long i = 0; i < ms.Length; i++)
		{
			hash = (hash ^ bytes[i]) * FNV_PRIME;
		}
		return hash;
	}

	// Each changed chunk's whole bucket (empty if it was emptied), then the
	// persistent list — the companion — which is small and written whole.
	private static void WriteEntities(BinaryWriter w, WorldState worldState, List<Vector3I> changedChunks)
	{
		w.Write(changedChunks.Count);
		foreach (Vector3I coord in changedChunks)
		{
			w.Write(coord.X);
			w.Write(coord.Y);
			w.Write(coord.Z);
			EntitySerializer.WriteList(w, worldState.GetEntities(coord));
		}
		EntitySerializer.WriteList(w, worldState.PersistentEntities);
	}

	// One node section per roster member, in roster order — the order ApplyAfterSpawn
	// reads them back in. Every member has a node (a fallen one is its corpse).
	// Length-prefixed, so a member dropped on load (Party.Read) is skipped whole.
	private static void WriteMembers(BinaryWriter w, Party party, Func<PlayerState, Player> nodeFor)
	{
		for (int i = 0; i < party.Count; i++)
		{
			Player node = nodeFor(party[i]);
			if (node == null)
			{
				throw new InvalidOperationException($"party member '{party[i]?.characterName}' has no Player node");
			}
			using var ms = new MemoryStream();
			using (var mw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
			{
				node.WriteMemberSave(mw);
			}
			w.Write((int)ms.Length);
			w.Write(ms.GetBuffer(), 0, (int)ms.Length);
		}
	}
}

// A save that has been read but not yet applied: the header, plus the body
// waiting for the world the header names to be running. Applied in four steps,
// in order, each at the point (Main, then GameClient.Init) where what it
// restores exists.
public sealed class SaveFile
{
	public readonly WorldOrigin Origin;
	public readonly int RunSeed;
	public readonly int DayNumber;
	public readonly ulong GameTimeMs;
	// Where the party wakes. The campfire here, if there is one, is lit.
	public readonly Vector3 Campfire;

	// How far a campfire's position may sit from the saved anchor and still be
	// the fire the party slept at (the anchor is that fire's node position).
	private const float CAMPFIRE_MATCH_DISTANCE = 0.5f;

	private readonly EntitySerializer.ReadPathTable _table;
	private readonly BinaryReader _body;
	private int _step;
	// The roster as saved (a null where Party.Read dropped a member), keying the
	// per-member node sections ApplyAfterSpawn reads.
	private List<PlayerState> _savedMembers;
	// The chunks ApplyEntities replaced — whose entity-owned voxels
	// ReconcileStamps brings in line.
	private readonly List<Vector3I> _savedChunks = new();

	internal SaveFile(WorldOrigin origin, int runSeed, int dayNumber, ulong gameTimeMs, Vector3 campfire,
		EntitySerializer.ReadPathTable table, BinaryReader body)
	{
		Origin = origin;
		RunSeed = runSeed;
		DayNumber = dayNumber;
		GameTimeMs = gameTimeMs;
		Campfire = campfire;
		_table = table;
		_body = body;
	}

	// 1. Once the world is built and its baseline captured (Main): the chunks
	// the run changed, and the persistent entities.
	public void ApplyEntities(WorldState worldState)
	{
		Step(0);
		EntitySerializer.BeginSharedRead(_table);
		try
		{
			int chunks = _body.ReadInt32();
			for (int i = 0; i < chunks; i++)
			{
				var coord = new Vector3I(_body.ReadInt32(), _body.ReadInt32(), _body.ReadInt32());
				worldState.ReplaceChunkEntities(coord, EntitySerializer.ReadList(_body, _table));
				_savedChunks.Add(coord);
			}
			worldState.ReplacePersistentEntities(EntitySerializer.ReadList(_body, _table));
		}
		finally
		{
			EntitySerializer.EndSharedRead();
		}
	}

	// Once the Sim exists: re-stamp the voxels the saved chunks' entities own (a
	// door the run left open) and relight just the cells that moved — what Door
	// does when it swings. Everything else was stamped as the world was built.
	public void ReconcileStamps(Sim sim)
	{
		var changed = new List<Vector3I>();
		foreach (Vector3I coord in _savedChunks)
		{
			List<EntitySimState> bucket = sim.WorldState.GetEntities(coord);
			if (bucket == null)
			{
				continue;
			}
			foreach (EntitySimState state in bucket)
			{
				if (state is IVoxelStamper stamper)
				{
					EntityVoxelStamper.Apply(sim.WorldState, stamper.ResolveStamp(sim.WorldState), changed);
				}
			}
		}
		if (changed.Count > 0)
		{
			sim.UpdateLighting(changed);
		}
	}

	// 2. Before the Sim is built on this world: the clock and the day's rolls as
	// the wake left them, and the anchor campfire as the world's one lit fire so
	// it streams in lit.
	public void ApplyToWorld(WorldState worldState)
	{
		Step(1);
		worldState.DayNumber = DayNumber;
		worldState.GameTimeMs = GameTimeMs;
		worldState.TimeOfDay01 = WorldState.SunriseTimeOfDay01;
		worldState.TimeOfDayAbsolute = DayNumber + WorldState.SunriseTimeOfDay01;
		worldState.BeginRun(RunSeed);

		// No fire at the anchor (a death before the party ever camped wakes at the
		// world spawn) leaves every campfire as the world has it.
		float matchSq = CAMPFIRE_MATCH_DISTANCE * CAMPFIRE_MATCH_DISTANCE;
		var campfires = new List<CampfireSimState>();
		CampfireSimState lit = null;
		foreach (EntitySimState state in worldState.AllChunkEntities())
		{
			if (state is CampfireSimState campfire)
			{
				campfires.Add(campfire);
				if (lit == null && (campfire.WorldPosition - Campfire).LengthSquared() <= matchSq)
				{
					lit = campfire;
				}
			}
		}
		if (lit == null)
		{
			return;
		}
		foreach (CampfireSimState campfire in campfires)
		{
			campfire.Active = campfire == lit;
		}
		worldState.SimState.LitCampfire = lit;
	}

	// 3. Before the party is built: the roster (Sim.EnsureParty keeps a party
	// that already exists), its knowledge and chart, and the stashes.
	public void ApplyParty(WorldState worldState)
	{
		Step(2);
		SimState simState = worldState.SimState;
		EntitySerializer.BeginSharedRead(_table);
		try
		{
			simState.Party = Party.Read(_body, out _savedMembers);
			simState.DeserializeStashes(_body);
		}
		finally
		{
			EntitySerializer.EndSharedRead();
		}
	}

	// 4. Once the party is spawned: each member's node, then the run's scripting.
	public void ApplyAfterSpawn(WorldState worldState, Func<PlayerState, Player> nodeFor)
	{
		Step(3);
		SimState simState = worldState.SimState;
		EntitySerializer.BeginSharedRead(_table);
		try
		{
			foreach (PlayerState member in _savedMembers)
			{
				byte[] section = _body.ReadBytes(_body.ReadInt32());
				Player node = member != null ? nodeFor(member) : null;
				if (node == null)
				{
					continue;
				}
				using var mr = new BinaryReader(new MemoryStream(section));
				node.RestoreMemberSave(mr);
			}
			simState.ScriptVars.Deserialize(_body);
			simState.QuestLog.Deserialize(_body);
			simState.DeserializeTreasureMaps(_body);
		}
		finally
		{
			EntitySerializer.EndSharedRead();
			_body.Dispose();
		}
	}

	// The body is one sequential stream, so the steps can only run in order.
	private void Step(int expected)
	{
		if (_step != expected)
		{
			throw new InvalidOperationException($"SaveFile applied out of order (step {expected} after {_step})");
		}
		_step++;
	}
}
