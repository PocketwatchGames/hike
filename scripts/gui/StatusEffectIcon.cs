using Godot;

// Bare icon used for the over-player and over-mob status announcements.
// No count badge, no progress bar — Hud + MobHUD use it as a transient
// notification (queued, auto-outro after hold) and a long-lived per-instance
// strip entry (no auto-outro; Outro() called when the effect ends) respectively.
// The detail readout in StatusEffectInfoPanel still uses the heavier
// StatusEffectHud.
[GlobalClass]
public partial class StatusEffectIcon : TextureRect
{
	const float IntroDuration = 0.2f;
	const float HoldDuration = 1.0f;
	const float OutroDuration = 0.3f;
	const float IntroScaleStart = 3.0f;

	public StatusEffectData Data { get; private set; }
	public bool IsFinished { get; private set; }

	float _time;
	bool _autoOutro;
	bool _outroRequested;
	float _outroTime;

	// `autoOutro` true → player flow: hold then fade out automatically.
	// `autoOutro` false → mob flow: hold indefinitely until Outro() is called
	// when the underlying StatusEffectState is removed.
	public void Init(StatusEffectData data, bool autoOutro)
	{
		Data = data;
		Texture = data?.icon;
		_autoOutro = autoOutro;
		_outroRequested = false;
		_time = 0f;
		_outroTime = 0f;
		IsFinished = false;
		Modulate = new Color(1f, 1f, 1f, 0f);
		Scale = new Vector2(IntroScaleStart, IntroScaleStart);
		PivotOffset = Size * 0.5f;
		SetProcess(true);
	}

	// Persistent-display path for inventory slots — no intro / hold / outro
	// animation, just the icon at full opacity. Disables _Process so a strip
	// of static icons doesn't pay the per-frame animation cost. Distinct from
	// Init() because the slot grid rebuilds icons on every SetItem; running
	// the intro pop each time would be visually wrong and CPU-wasteful.
	public void InitStatic(StatusEffectData data)
	{
		Data = data;
		Texture = data?.icon;
		_autoOutro = false;
		_outroRequested = false;
		_time = 0f;
		_outroTime = 0f;
		IsFinished = true;
		Modulate = Colors.White;
		Scale = Vector2.One;
		SetProcess(false);
	}

	public void Outro()
	{
		if (_outroRequested)
		{
			return;
		}
		_outroRequested = true;
		_outroTime = 0f;
	}

	public override void _Process(double delta)
	{
		if (IsFinished)
		{
			return;
		}
		float dt = (float)delta;
		_time += dt;
		// Keep the scale anchored to the icon's center even if a layout pass
		// resizes us (e.g. a sibling in the mob HBox getting added/removed).
		PivotOffset = Size * 0.5f;

		if (_time < IntroDuration)
		{
			float t = _time / IntroDuration;
			float s = Mathf.Lerp(IntroScaleStart, 1f, t);
			Scale = new Vector2(s, s);
			Modulate = new Color(1f, 1f, 1f, t);
			return;
		}

		Scale = Vector2.One;

		if (_autoOutro)
		{
			float postIntro = _time - IntroDuration;
			if (postIntro < HoldDuration)
			{
				Modulate = Colors.White;
			}
			else if (postIntro < HoldDuration + OutroDuration)
			{
				float t = (postIntro - HoldDuration) / OutroDuration;
				Modulate = new Color(1f, 1f, 1f, 1f - t);
			}
			else
			{
				Modulate = new Color(1f, 1f, 1f, 0f);
				IsFinished = true;
			}
			return;
		}

		if (!_outroRequested)
		{
			Modulate = Colors.White;
			return;
		}

		_outroTime += dt;
		if (_outroTime < OutroDuration)
		{
			Modulate = new Color(1f, 1f, 1f, 1f - _outroTime / OutroDuration);
		}
		else
		{
			Modulate = new Color(1f, 1f, 1f, 0f);
			IsFinished = true;
		}
	}
}
