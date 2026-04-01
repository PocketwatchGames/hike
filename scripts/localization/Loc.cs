using System;
using System.Collections.Generic;
using Godot;

public static partial class Loc
{
	private const string DEFAULT_LANGUAGE = "english";
	private const string LOCALIZATION_PATH = "res://resources/localization/";

	private static Dictionary<string, string> _strings = new Dictionary<string, string>();
	private static bool _subscribedToCVar;

	public static Action OnLanguageChanged;

	public static void Init(string language)
	{
		if (!_subscribedToCVar)
		{
			CVars.language.OnChanged += (cvar) =>
			{
				Init(cvar.GetString());
				OnLanguageChanged?.Invoke();
			};
			_subscribedToCVar = true;
		}

		if (string.IsNullOrEmpty(language))
		{
			language = DEFAULT_LANGUAGE;
		}

		_strings.Clear();

		string path = LOCALIZATION_PATH + language + ".tsv";
		if (!FileAccess.FileExists(path))
		{
			GD.PrintErr($"Localization file not found: {path}");
			return;
		}

		using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
		if (file == null)
		{
			GD.PrintErr($"Failed to open localization file: {path}");
			return;
		}

		// Skip header row
		if (!file.EofReached())
		{
			file.GetLine();
		}

		while (!file.EofReached())
		{
			string line = file.GetLine();
			if (string.IsNullOrEmpty(line))
			{
				continue;
			}

			int wsIndex = line.IndexOfAny(new[] { ' ', '\t' });
			if (wsIndex < 0)
			{
				continue;
			}

			string key = line.Substring(0, wsIndex);
			string value = line.Substring(wsIndex).TrimStart();
			_strings[key] = value;
		}
	}

	public static string Get(Keys key)
	{
		string keyName = key.ToString();
		if (_strings.TryGetValue(keyName, out string value))
		{
			return value;
		}

		return $"MISSING:{keyName}";
	}

	public static string Format(Keys key, params object[] args)
	{
		string text = Get(key);
		for (int i = 0; i < args.Length; i++)
		{
			text = text.Replace($"%{i}", args[i]?.ToString() ?? "");
		}

		return text;
	}
}
