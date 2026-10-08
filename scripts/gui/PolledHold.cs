using Godot;

// Tap / hold on a menu action no slot button sees (send, drop), read by polling.
public sealed class PolledHold
{
	public enum EResult
	{
		None,
		Tap,
		Hold,
	}

	readonly string _action;
	float _held;
	bool _wasDown;
	bool _fired;

	public PolledHold(string action)
	{
		_action = action;
	}

	// `enabled` false cancels a hold in progress (nothing under the cursor).
	public EResult Tick(bool enabled, float dt, float holdSeconds, ButtonHint hint)
	{
		bool down = Input.IsActionPressed(_action);
		EResult result = EResult.None;
		if (!enabled)
		{
			Reset(hint);
		}
		else if (down && _wasDown && !_fired)
		{
			_held += dt;
			hint?.SetProgress(Mathf.Clamp(_held / holdSeconds, 0f, 1f));
			if (_held >= holdSeconds)
			{
				_fired = true;
				hint?.SetProgress(0f);
				result = EResult.Hold;
			}
		}
		else if (!down && _wasDown && !_fired)
		{
			result = EResult.Tap;
		}
		if (!down)
		{
			Reset(hint);
		}
		_wasDown = down;
		return result;
	}

	public void Reset(ButtonHint hint)
	{
		_held = 0f;
		_fired = false;
		hint?.SetProgress(0f);
	}

	// A press already down when the screen opens must come up before it counts.
	public void Arm(ButtonHint hint)
	{
		Reset(hint);
		_wasDown = Input.IsActionPressed(_action);
	}
}
