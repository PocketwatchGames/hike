using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public static class SaveGame
{
	// Bump when the binary format changes shape. Old saves below this version
	// are rejected outright by Load — the format isn't yet user-facing so
	// we don't keep back-compat readers.
	//   v1: header only.
	//   v2: + player status-effect buildup section.
	//   v3: + scripting-variable bank (quest flags / world state).
	//   v4: + active quest log (Rescue / hunt / Return to Camp / language).
	private const int SAVE_VERSION = 4;

	public static void Save(string filePath)
	{
		using var stream = new FileStream(filePath, FileMode.Create);
		using var w = new BinaryWriter(stream);

		// --- Header ---
		w.Write(SAVE_VERSION);

		// --- Player status-effect buildups (v2+) ---
		// Active StatusEffectState instances (per-stack timers, fx) aren't
		// included — the buildup meter is the only piece serialized today.
		// Item-side controllers (per-armor wetness) likewise wait for an
		// inventory section to land.
		Player player = Sim.Current?.player;
		var playerBuildups = player != null
			? player.EnumerateStatusBuildupsForSave().ToList()
			: new List<(StatusEffectData data, float amount)>();
		WriteBuildupSection(w, playerBuildups);

		// --- Scripting variables (v3+) ---
		ScriptVariableBank bank = Sim.Current?.WorldState?.SimState?.ScriptVars;
		if (bank != null)
		{
			bank.Serialize(w);
		}
		else
		{
			w.Write(0);
		}

		// --- Quest log (v4+) ---
		QuestLog questLog = Sim.Current?.WorldState?.SimState?.QuestLog;
		if (questLog != null)
		{
			questLog.Serialize(w);
		}
		else
		{
			w.Write(0);
		}
	}

	public static void Load(string filePath)
	{
		using var stream = new FileStream(filePath, FileMode.Open);
		using var r = new BinaryReader(stream);

		// --- Header ---
		int version = r.ReadInt32();
		if (version < 1 || version > SAVE_VERSION)
		{
			throw new InvalidDataException($"Unsupported save version: {version}");
		}

		// --- Player status-effect buildups (v2+) ---
		if (version >= 2)
		{
			var playerBuildups = ReadBuildupSection(r);
			Sim.Current?.player?.RestoreStatusBuildups(playerBuildups);
		}

		// --- Scripting variables (v3+) ---
		if (version >= 3)
		{
			Sim.Current?.WorldState?.SimState?.ScriptVars?.Deserialize(r);
		}

		// --- Quest log (v4+) ---
		if (version >= 4)
		{
			Sim.Current?.WorldState?.SimState?.QuestLog?.Deserialize(r);
		}
	}

	// Writes (count, [path, amount]*) for the given buildup snapshot.
	// `path`-keyed rather than via the resource-lookup table because per-
	// actor buildup counts are tiny (<<256) and effect-data refs aren't
	// repeated across the section — the table would be longer than the data.
	private static void WriteBuildupSection(BinaryWriter w, List<(StatusEffectData data, float amount)> entries)
	{
		int valid = 0;
		for (int i = 0; i < entries.Count; i++)
		{
			if (entries[i].data != null) { valid++; }
		}
		w.Write((byte)valid);
		for (int i = 0; i < entries.Count; i++)
		{
			var (data, amount) = entries[i];
			if (data == null) { continue; }
			w.Write(data.ResourcePath);
			w.Write(amount);
		}
	}

	// Reads the inverse of WriteBuildupSection. Resources that fail to load
	// (renamed / removed .tres between save sessions) are silently skipped —
	// the rest of the section still parses so a missing single effect
	// doesn't poison the whole load.
	private static List<(StatusEffectData data, float amount)> ReadBuildupSection(BinaryReader r)
	{
		int count = r.ReadByte();
		var entries = new List<(StatusEffectData data, float amount)>(count);
		for (int i = 0; i < count; i++)
		{
			string path = r.ReadString();
			float amount = r.ReadSingle();
			StatusEffectData data = GD.Load<StatusEffectData>(path);
			if (data == null) { continue; }
			entries.Add((data, amount));
		}
		return entries;
	}

	// --- Helpers ---

	private static string ReadNullableString(BinaryReader r)
	{
		string s = r.ReadString();
		return s.Length > 0 ? s : null;
	}

	private static ResourceLookupTable<T> BuildLookupTable<T>() where T : Resource
	{
		return new ResourceLookupTable<T>();
	}

	private static void WriteLookupTableByte<T>(BinaryWriter w, ResourceLookupTable<T> table) where T : Resource
	{
		var entries = table.GetEntries();
		w.Write((byte)entries.Count);
		foreach (var entry in entries)
		{
			w.Write(entry.ResourcePath);
		}
	}

	private static void WriteLookupTableUShort<T>(BinaryWriter w, ResourceLookupTable<T> table) where T : Resource
	{
		var entries = table.GetEntries();
		w.Write((ushort)entries.Count);
		foreach (var entry in entries)
		{
			w.Write(entry.ResourcePath);
		}
	}

	private static List<T> ReadLookupTableByte<T>(BinaryReader r) where T : Resource
	{
		int count = r.ReadByte();
		var list = new List<T>(count);
		for (int i = 0; i < count; i++)
		{
			list.Add(GD.Load<T>(r.ReadString()));
		}
		return list;
	}

	private static List<T> ReadLookupTableUShort<T>(BinaryReader r) where T : Resource
	{
		int count = r.ReadUInt16();
		var list = new List<T>(count);
		for (int i = 0; i < count; i++)
		{
			list.Add(GD.Load<T>(r.ReadString()));
		}
		return list;
	}

	private class ResourceLookupTable<T> where T : Resource
	{
		private readonly Dictionary<T, int> _resourceToIndex = new Dictionary<T, int>();
		private readonly List<T> _entries = new List<T>();

		public void Add(T resource)
		{
			if (resource != null && !_resourceToIndex.ContainsKey(resource))
			{
				_resourceToIndex[resource] = _entries.Count;
				_entries.Add(resource);
			}
		}

		public int GetIndex(T resource)
		{
			return _resourceToIndex[resource];
		}

		public List<T> GetEntries()
		{
			return _entries;
		}
	}
}
