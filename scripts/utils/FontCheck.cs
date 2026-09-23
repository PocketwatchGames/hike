using Godot;
using System.Collections.Generic;

// Validates that the UI font can actually draw everything the game asks of it,
// then quits — `--headless -- "font_check 1"`. The twin of shader_check for the
// one thing about in-world scripts that nothing else can report: a
// LanguageData.glyphBase naming a block the glyph font has no art for renders
// as tofu boxes, which compiles, loads and passes resource_check.
//
// Checks two things:
//   - the font still covers ASCII. A FontVariation with no base_font inherits
//     the engine default; if that ever stops being true the whole UI goes
//     blank, so it is worth one assertion.
//   - every language's 36-codepoint block resolves through `fallbacks`.
public static class FontCheck
{
	// Read rather than hardcoded, so the check follows the project's font if it
	// is ever swapped — the fallbacks are what must survive that swap.
	private const string UI_FONT_SETTING = "gui/theme/custom_font";
	private const string DATA_DIR = "res://resources/data";
	private const string LANGUAGE_CLASS = "script_class=\"LanguageData\"";

	public static void RunAndQuit(SceneTree tree)
	{
		int failures = 0;
		string fontPath = ProjectSettings.GetSetting(UI_FONT_SETTING).AsString();
		if (string.IsNullOrEmpty(fontPath))
		{
			GD.PrintErr($"[font_check] FAIL project setting '{UI_FONT_SETTING}' is unset, so no "
				+ "Control reaches the glyph fallbacks and every language renders in Latin");
			tree.Quit();
			return;
		}
		Font font = ResourceLoader.Load<Font>(fontPath);
		if (font == null)
		{
			GD.PrintErr($"[font_check] FAIL could not load {fontPath}");
			tree.Quit();
			return;
		}
		GD.Print($"[font_check] ui font {fontPath}");

		failures += CheckRange(font, 'a', 26, "ASCII lowercase");
		failures += CheckRange(font, 'A', 26, "ASCII uppercase");
		failures += CheckRange(font, '0', 10, "ASCII digits");

		List<string> paths = new List<string>();
		Collect(DATA_DIR, paths);
		paths.Sort();
		int languages = 0;
		foreach (string path in paths)
		{
			LanguageData lang = ResourceLoader.Load<LanguageData>(path);
			if (lang == null)
			{
				GD.PrintErr($"[font_check] FAIL {path} declares LanguageData but did not load as one");
				failures++;
				continue;
			}
			languages++;
			if (lang.glyphBase <= 0)
			{
				GD.Print($"[font_check] {lang.id}: no script of its own, scrambles in Latin");
				continue;
			}
			failures += CheckRange(font, lang.glyphBase, TextScrambler.GlyphLetterCount,
				$"{lang.id} letters");
			failures += CheckRange(font, lang.glyphBase + TextScrambler.GlyphLetterCount,
				TextScrambler.GlyphNumeralCount, $"{lang.id} numerals");
		}

		GD.Print(failures == 0
			? $"[font_check] ok ({languages} languages)"
			: $"[font_check] {failures} FAIL(s) over {languages} languages");
		tree.Quit();
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
