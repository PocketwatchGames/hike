using Godot;
using System.Collections.Generic;

// Validates that every font the UI theme can draw with can actually draw
// everything the game asks of it, then quits — `--headless -- "font_check 1"`.
// The twin of shader_check for the one thing about in-world scripts that
// nothing else can report: a LanguageData.glyphBase naming a block the glyph
// font has no art for renders as tofu boxes, which compiles, loads and passes
// resource_check.
//
// Every font in the theme is checked, not just default_font: any of them can
// end up rendering a language span (a speaker name, an inscription), and a
// face added without the glyph fallback is otherwise found only as tofu.
// Per font:
//   - it still covers ASCII. A FontVariation with no base_font covers nothing
//     but its fallbacks and blanks every Control that uses it.
//   - every language's 36-codepoint block resolves through `fallbacks`.
public static class FontCheck
{
	// Read rather than hardcoded, so the check follows the project's theme if
	// it is ever swapped — the fallbacks are what must survive that swap.
	private const string UI_THEME_SETTING = "gui/theme/custom";
	private const string DATA_DIR = "res://resources/data";
	private const string LANGUAGE_CLASS = "script_class=\"LanguageData\"";

	public static void RunAndQuit(SceneTree tree)
	{
		int failures = 0;
		string themePath = ProjectSettings.GetSetting(UI_THEME_SETTING).AsString();
		Theme theme = string.IsNullOrEmpty(themePath) ? null : ResourceLoader.Load<Theme>(themePath);
		if (theme == null || theme.DefaultFont == null)
		{
			GD.PrintErr($"[font_check] FAIL project setting '{UI_THEME_SETTING}' names no theme with a "
				+ "default_font, so no Control reaches the glyph fallbacks and every language renders in Latin");
			tree.Quit();
			return;
		}
		List<(string label, Font font)> fonts = CollectThemeFonts(theme);
		GD.Print($"[font_check] ui theme {themePath}, {fonts.Count} font(s)");

		List<LanguageData> languages = LoadLanguages(ref failures);
		foreach ((string label, Font font) in fonts)
		{
			failures += CheckRange(font, 'a', 26, $"{label} ASCII lowercase");
			failures += CheckRange(font, 'A', 26, $"{label} ASCII uppercase");
			failures += CheckRange(font, '0', 10, $"{label} ASCII digits");
			foreach (LanguageData lang in languages)
			{
				if (lang.glyphBase <= 0)
				{
					continue;
				}
				failures += CheckRange(font, lang.glyphBase, TextScrambler.GlyphLetterCount,
					$"{label} {lang.id} letters");
				failures += CheckRange(font, lang.glyphBase + TextScrambler.GlyphLetterCount,
					TextScrambler.GlyphNumeralCount, $"{label} {lang.id} numerals");
			}
		}

		GD.Print(failures == 0
			? $"[font_check] ok ({fonts.Count} fonts, {languages.Count} languages)"
			: $"[font_check] {failures} FAIL(s) over {fonts.Count} fonts, {languages.Count} languages");
		tree.Quit();
	}

	// default_font plus every font item of every type (variations included),
	// each distinct resource once.
	private static List<(string, Font)> CollectThemeFonts(Theme theme)
	{
		List<(string, Font)> fonts = new List<(string, Font)>();
		HashSet<Font> seen = new HashSet<Font>();
		seen.Add(theme.DefaultFont);
		fonts.Add(("default_font", theme.DefaultFont));
		foreach (string type in theme.GetFontTypeList())
		{
			foreach (string name in theme.GetFontList(type))
			{
				Font font = theme.GetFont(name, type);
				if (font != null && seen.Add(font))
				{
					fonts.Add(($"{type}/{name} ({font.ResourcePath})", font));
				}
			}
		}
		return fonts;
	}

	private static List<LanguageData> LoadLanguages(ref int failures)
	{
		List<string> paths = new List<string>();
		Collect(DATA_DIR, paths);
		paths.Sort();
		List<LanguageData> languages = new List<LanguageData>();
		foreach (string path in paths)
		{
			LanguageData lang = ResourceLoader.Load<LanguageData>(path);
			if (lang == null)
			{
				GD.PrintErr($"[font_check] FAIL {path} declares LanguageData but did not load as one");
				failures++;
				continue;
			}
			languages.Add(lang);
			if (lang.glyphBase <= 0)
			{
				GD.Print($"[font_check] {lang.id}: no script of its own, scrambles in Latin");
			}
		}
		return languages;
	}

	// Font.HasChar walks the fallback chain, which is the whole mechanism under
	// test: the glyphs live in a separate font reached only that way.
	private static int CheckRange(Font font, int first, int count, string what)
	{
		for (int i = 0; i < count; i++)
		{
			if (!font.HasChar(first + i))
			{
				GD.PrintErr($"[font_check] FAIL {what}: no glyph for U+{(first + i):X4}");
				return 1;
			}
		}
		return 0;
	}

	// Collects .tres files whose header names LanguageData, by reading the
	// first line only — cheaper than loading every resource under the tree.
	private static void Collect(string dir, List<string> into)
	{
		using DirAccess da = DirAccess.Open(dir);
		if (da == null)
		{
			return;
		}
		foreach (string sub in da.GetDirectories())
		{
			Collect($"{dir}/{sub}", into);
		}
		foreach (string file in da.GetFiles())
		{
			if (!file.EndsWith(".tres"))
			{
				continue;
			}
			string path = $"{dir}/{file}";
			using FileAccess fa = FileAccess.Open(path, FileAccess.ModeFlags.Read);
			if (fa != null && fa.GetLine().Contains(LANGUAGE_CLASS))
			{
				into.Add(path);
			}
		}
	}
}
