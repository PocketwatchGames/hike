using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

public static class CVarRegistry
{
	private static List<CVar> _allCvars = new List<CVar>();
	private static Dictionary<string, CVar> _cvarsByName = new Dictionary<string, CVar>(StringComparer.OrdinalIgnoreCase);

	public static void Register(CVar cvar)
	{
		_allCvars.Add(cvar);
		_cvarsByName[cvar.Name] = cvar;
	}

	public static void Init()
	{
		Type cvarType = typeof(CVar);
		foreach (Type type in cvarType.Assembly.GetTypes())
		{
			foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
			{
				if (cvarType.IsAssignableFrom(field.FieldType))
				{
					RuntimeHelpers.RunClassConstructor(type.TypeHandle);
					break;
				}
			}
		}

		_allCvars.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
	}

	public static CVar Find(string name)
	{
		_cvarsByName.TryGetValue(name, out CVar cvar);
		return cvar;
	}

	public static List<string> GetCompletions(string prefix)
	{
		List<string> matches = new List<string>();
		for (int i = 0; i < _allCvars.Count; i++)
		{
			if (_allCvars[i].Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				matches.Add(_allCvars[i].Name);
			}
		}

		return matches;
	}

	public static string ProcessCommand(string input)
	{
		if (string.IsNullOrWhiteSpace(input))
		{
			return "";
		}

		string trimmed = input.Trim();
		int spaceIndex = trimmed.IndexOf(' ');
		string cvarName;
		string valueStr = null;

		if (spaceIndex >= 0)
		{
			cvarName = trimmed.Substring(0, spaceIndex);
			valueStr = trimmed.Substring(spaceIndex + 1).Trim();
		}
		else
		{
			cvarName = trimmed;
		}

		CVar cvar = Find(cvarName);
		if (cvar == null)
		{
			return $"Unknown command: '{cvarName}'";
		}

		if (cvar.Type == CVarType.None)
		{
			cvar.Execute();
			return $"Executed: {cvarName}";
		}

		if (valueStr == null)
		{
			return cvar.ToString();
		}

		string error = cvar.Set(valueStr);
		if (error != null)
		{
			return error;
		}

		return cvar.ToString();
	}

	public static void ExecFile(string path)
	{
		if (!File.Exists(path))
		{
			return;
		}

		string[] lines = File.ReadAllLines(path);
		for (int i = 0; i < lines.Length; i++)
		{
			string line = lines[i].Trim();
			if (line.Length == 0 || line.StartsWith("//"))
			{
				continue;
			}

			ProcessCommand(line);
		}
	}

	public static List<CVar> GetAll()
	{
		return _allCvars;
	}
}
