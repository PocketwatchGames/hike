using Godot;
using System;
using System.Collections.Generic;
using System.IO;

public static class SaveGame
{
	private const int SAVE_VERSION = 1;

	public static void Save(string filePath)
	{
		using var stream = new FileStream(filePath, FileMode.Create);
		using var w = new BinaryWriter(stream);

		// --- Header ---
		w.Write(SAVE_VERSION);
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
