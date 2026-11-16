using Godot;
using System.Collections.Generic;

// The controlled member remarking on hunger, thirst and fatigue
// (PlayerData.needRemarks): when a need rises past a remark's threshold the
// line types out in a speech bubble over their head, holds, and fades on its
// own — no press. Remarks wait while the party is in combat or other UI is up,
// and one whose need has since been eased below its threshold is dropped.
//
// Levels are seeded silently on Bind, so a spawn, load or party switch never
// remarks on a need that was already past its threshold.
[GlobalClass]
public partial class NeedRemarks : Control
{
	const int NeedCount = 3;

	[Export] SpeechBubble bubble;
	// Seconds a fully-typed line stays up before it fades.
	[Export(PropertyHint.Range, "0,10,0.1")] public float holdSeconds = 2.5f;
	[Export(PropertyHint.Range, "0,3,0.05")] public float fadeSeconds = 0.4f;
	// Seconds combat and other UI must have been clear before a remark starts.
	[Export(PropertyHint.Range, "0,10,0.1")] public float settleSeconds = 1.5f;

	Player _player;
	// The member's remarks per need, ascending by atStages.
	readonly List<NeedRemarkData>[] _byNeed = { new(), new(), new() };
	// Per need, how many of its remarks the value is currently past.
	readonly int[] _levels = new int[NeedCount];
	// At most one per need — the most severe crossed — oldest first.
	readonly List<NeedRemarkData> _pending = new();
	NeedRemarkData _showing;
	float _holdLeft;
	float _fadeLeft;
	float _clearSeconds;

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Ignore;
	}

	public void Bind(Player player)
	{
		Stop();
		_pending.Clear();
		_player = player;
		foreach (List<NeedRemarkData> list in _byNeed)
		{
			list.Clear();
		}
		Godot.Collections.Array<NeedRemarkData> remarks = player?.data?.needRemarks;
		if (remarks != null)
		{
			foreach (NeedRemarkData remark in remarks)
			{
				if (remark != null && (int)remark.need >= 0 && (int)remark.need < NeedCount)
				{
					_byNeed[(int)remark.need].Add(remark);
				}
			}
		}
		for (int i = 0; i < NeedCount; i++)
		{
			_byNeed[i].Sort((a, b) => a.atStages.CompareTo(b.atStages));
			_levels[i] = _player != null ? LevelOf((ENeed)i) : 0;
		}
	}

	// Wall clock: the bubble is presentational. `uiUp` pulls a showing remark
	// back into the queue; `inCombat` only holds new ones back.
	public void Tick(float delta, bool uiUp, bool inCombat)
	{
		if (_player == null || !IsInstanceValid(_player))
		{
			return;
		}
		UpdateLevels();

		if (uiUp || _player.IsDead)
		{
			_clearSeconds = 0f;
			if (_showing != null)
			{
				_pending.Insert(0, _showing);
				Stop();
			}
			return;
		}
		if (_showing != null)
		{
			TickShowing(delta);
			return;
		}
		if (inCombat)
		{
			_clearSeconds = 0f;
			return;
		}
		_clearSeconds += delta;
		if (_pending.Count > 0 && _clearSeconds >= settleSeconds)
		{
			NeedRemarkData next = _pending[0];
			_pending.RemoveAt(0);
			Show(next);
		}
	}

	void UpdateLevels()
	{
		for (int i = 0; i < NeedCount; i++)
		{
			ENeed need = (ENeed)i;
			int level = LevelOf(need);
			if (level == _levels[i])
			{
				continue;
			}
			if (level > _levels[i])
			{
				RemovePending(need);
				_pending.Add(_byNeed[i][level - 1]);
			}
			else
			{
				float stages = _player.GetNeedStages(need);
				_pending.RemoveAll(r => r.need == need && r.atStages > stages);
			}
			_levels[i] = level;
		}
	}

	int LevelOf(ENeed need)
	{
		float stages = _player.GetNeedStages(need);
		List<NeedRemarkData> list = _byNeed[(int)need];
		int level = 0;
		while (level < list.Count && stages >= list[level].atStages)
		{
			level++;
		}
		return level;
	}

	void RemovePending(ENeed need)
	{
		_pending.RemoveAll(r => r.need == need);
	}

	void Show(NeedRemarkData remark)
	{
		if (bubble == null || remark.textLocKey is null || remark.textLocKey.IsEmpty)
		{
			return;
		}
		_showing = remark;
		_holdLeft = holdSeconds;
		_fadeLeft = fadeSeconds;
		bubble.Modulate = Colors.White;
		bubble.Open(_player, inscribed: false);
		bubble.SetSpeakerName(null);
		bubble.AwaitingContinue = false;
		bubble.Begin(Loc.Get(remark.textLocKey));
	}

	void TickShowing(float delta)
	{
		if (!bubble.HasLiveAnchor)
		{
			Stop();
			return;
		}
		if (!bubble.IsFullyRevealed)
		{
			return;
		}
		if (_holdLeft > 0f)
		{
			_holdLeft -= delta;
			return;
		}
		_fadeLeft -= delta;
		if (_fadeLeft <= 0f)
		{
			Stop();
			return;
		}
		bubble.Modulate = new Color(1f, 1f, 1f, fadeSeconds > 0f ? _fadeLeft / fadeSeconds : 0f);
	}

	void Stop()
	{
		_showing = null;
		if (bubble != null)
		{
			bubble.Close();
			bubble.Modulate = Colors.White;
		}
	}
}
