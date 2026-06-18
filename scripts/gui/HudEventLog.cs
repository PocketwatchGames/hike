using Godot;
using System.Collections.Generic;
using System.Text;

// Bottom-left rolling event log. Replaces the center announcement panel as the
// destination for routine notifications (recipes, item IDs, language learned,
// bestiary, gifts) and for interactive-action refusals ("Danger Nearby", "Too
// Hurt to Rest"). Holds the last `maxEvents` lines; each line lives
// `lifetimeSeconds`, then fades over `fadeSeconds` and drops off the top.
// Newest line sits at the bottom.
//
// Timing is wall-clock (_Process delta): the log is purely presentational, so
// it stays smooth at render fps and isn't dragged by slow-mo (the sim-vs-wall-
// clock split in the root CLAUDE.md).
//
// Lines may carry BBCode (callers wrap titles in [b]…[/b]); the per-line fade
// wraps each in a [color=#ffffffAA] tag, so lines must not set their own color
// or the alpha won't apply to those spans. Bold/italic are fine — they inherit
// the enclosing color.
[GlobalClass]
public partial class HudEventLog : RichTextLabel
{
	[Export(PropertyHint.Range, "1,12,1")] public int maxEvents = 6;
	[Export(PropertyHint.Range, "1,60,0.5")] public float lifetimeSeconds = 8f;
	[Export(PropertyHint.Range, "0.1,5,0.1")] public float fadeSeconds = 1.5f;

	struct Entry
	{
		public string text;   // line content, may contain BBCode
		public float age;     // seconds shown, wall-clock
	}

	readonly List<Entry> _entries = new();
	readonly StringBuilder _sb = new();

	public override void _Ready()
	{
		// Structural config the component depends on — set here so the node
		// functions regardless of how the .tscn was authored.
		BbcodeEnabled = true;
		FitContent = true;
		ScrollActive = false;
		MouseFilter = MouseFilterEnum.Ignore;
	}

	// Append a line to the bottom of the log. No-op on empty text.
	public void Push(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		_entries.Add(new Entry { text = text, age = 0f });
		// Trim the oldest beyond the cap immediately so the log never exceeds
		// maxEvents even before the lifetime timers expire them.
		while (_entries.Count > maxEvents)
		{
			_entries.RemoveAt(0);
		}
		Rebuild();
	}

	public override void _Process(double delta)
	{
		if (_entries.Count == 0)
		{
			return;
		}
		float dt = (float)delta;
		float fadeStart = lifetimeSeconds - fadeSeconds;
		bool dirty = false;
		for (int i = _entries.Count - 1; i >= 0; i--)
		{
			Entry e = _entries[i];
			e.age += dt;
			_entries[i] = e;
			if (e.age >= lifetimeSeconds)
			{
				_entries.RemoveAt(i);
				dirty = true;
			}
			else if (e.age > fadeStart)
			{
				// In the fade window — its alpha changes this frame.
				dirty = true;
			}
		}
		if (dirty)
		{
			Rebuild();
		}
	}

	void Rebuild()
	{
		_sb.Clear();
		float fadeStart = lifetimeSeconds - fadeSeconds;
		for (int i = 0; i < _entries.Count; i++)
		{
			Entry e = _entries[i];
			float alpha = 1f;
			if (fadeSeconds > 0f && e.age > fadeStart)
			{
				alpha = Mathf.Clamp(1f - (e.age - fadeStart) / fadeSeconds, 0f, 1f);
			}
			int a8 = Mathf.RoundToInt(alpha * 255f);
			if (i > 0)
			{
				_sb.Append('\n');
			}
			_sb.Append("[color=#ffffff");
			_sb.Append(a8.ToString("x2"));
			_sb.Append(']');
			_sb.Append(e.text);
			_sb.Append("[/color]");
		}
		Text = _sb.ToString();
	}
}
