using Godot;
using System;
using System.Collections.Generic;

// HUD panel that runs a ConversationData: types out a branch's lines into
// `label`, then spawns one instance of `responseOptionScene` per visible
// response into `responseOptionsContainer`. Picking a response fires its
// actions, advances to its destination branch (or closes the panel if the
// destination is empty), and starts the next branch's typewriter.
//
// State machine:
//   Hidden    — panel offscreen, no conversation
//   Typing    — typewriter walking branch.lineLocKeys; ui_accept reveals
//               then advances, ui_cancel closes
//   Choosing  — response buttons visible, focus on the first one;
//               gamepad ui_up/ui_down + ui_accept work via the buttons'
//               native focus chain, ui_cancel still closes from this
//               controller's _UnhandledInput
//
// Response visibility uses ConversationVisibility (combines authored
// condition + per-response RNG vs. language comprehension). If a branch's
// visible-response set is empty, the conversation ends — there's no "press
// to dismiss" prompt because the typewriter's last ui_accept is what
// transitioned us here.
//
// While open, gameClient.InputSuppressed flips on so the same press that
// reveals / advances the line doesn't also fall through to the gameplay
// buttons in Player.ProcessInput.
[GlobalClass]
public partial class ConversationController : Control
{
	[Export] public Label label;
	[Export] public GameClient gameClient;
	[Export] PackedScene responseOptionScene;
	[Export] Control responseOptionsContainer;

	enum EState { Hidden, Typing, Choosing }
	EState _state = EState.Hidden;

	// Stored across the active conversation so response presses can look
	// up their destination branch and fire actions with the same context
	// the entry was opened with.
	ConversationData _conversation;
	ConversationContext _ctx;
	ConversationBranch _currentBranch;

	readonly List<string> _lines = new();
	int _lineIndex;
	// Float so the per-tick advance can roll fractional characters in
	// without dropping a glyph per frame when speed × dt < 1.
	float _revealedChars;
	Action _onClose;

	// Live response buttons spawned into responseOptionsContainer; tracked
	// separately so we can free them on branch transition / close without
	// touching the Label that shares the container.
	readonly List<Button> _responseButtons = new();

	public bool IsOpen => Visible;

	public override void _Ready()
	{
		Visible = false;
	}

	// Open the panel on a conversation. Picks the entry branch, fires its
	// actions, then kicks the typewriter — does nothing if no entry
	// condition matches or the resolved branch is missing.
	public void Show(ConversationData conversation, ConversationContext ctx, Action onClose = null)
	{
		if (conversation == null)
		{
			return;
		}
		ConversationEntry entry = SelectEntry(conversation, ctx);
		if (entry == null)
		{
			return;
		}
		ConversationBranch branch = FindBranch(conversation, entry.branch);
		if (branch == null)
		{
			// Warned, like the exit-group miss below: an entry naming a branch
			// the conversation does not contain is a DATA error, and returning
			// silently here is indistinguishable from the NPC having nothing to
			// say. In game it reads as the interact prompt flickering once and
			// the panel never opening — with nothing in the log to look up.
			GD.PushWarning($"ConversationController: entry branch '{entry.branch}' not found in "
				+ $"'{conversation.ResourcePath}' — the conversation has "
				+ $"{conversation.branches?.Count ?? 0} branch(es), so this NPC cannot speak.");
			return;
		}

		_conversation = conversation;
		_ctx = ctx;
		// Set on our stored copy so actions / visibility checks downstream
		// see the same controller reference an OpenShopAction would close.
		_ctx.controller = this;
		_onClose = onClose;
		if (gameClient != null)
		{
			gameClient.InputSuppressed = true;
		}
		Visible = true;

		// Fire actions AFTER the panel is wired up so an action that wants
		// to take over the screen (OpenShopAction etc.) can call Close()
		// and have it actually dismiss before StartBranch runs.
		FireActions(entry.actions, _ctx);
		if (!Visible)
		{
			return;
		}
		StartBranch(branch);
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		Visible = false;
		ClearResponseButtons();
		_lines.Clear();
		_lineIndex = 0;
		_revealedChars = 0f;
		_state = EState.Hidden;
		_conversation = null;
		_currentBranch = null;
		if (gameClient != null)
		{
			gameClient.InputSuppressed = false;
		}
		Action cb = _onClose;
		_onClose = null;
		cb?.Invoke();
	}

	// Set up the typewriter for `branch`. If the branch has no lines, jumps
	// straight to the response chooser (which will close the conversation
	// if no responses are visible either).
	void StartBranch(ConversationBranch branch)
	{
		_currentBranch = branch;
		ClearResponseButtons();
		ResolveAndScrambleLines(branch, _ctx);
		_lineIndex = 0;
		_revealedChars = 0f;
		_state = EState.Typing;
		if (label != null)
		{
			label.Text = string.Empty;
		}
		if (_lines.Count == 0)
		{
			ShowResponseChooser();
		}
	}

	// Filter the current branch's responses through ConversationVisibility,
	// spawn buttons for the survivors, and grab focus on the first so
	// gamepad navigation lands somewhere sensible. Closes the conversation
	// if no response is visible.
	void ShowResponseChooser()
	{
		_state = EState.Choosing;
		ClearResponseButtons();
		if (_currentBranch == null)
		{
			Close();
			return;
		}
		// Branch end-actions fire AFTER the typewriter and BEFORE the
		// chooser — so OpenShopAction etc. can close the panel and take
		// over the screen without needing a dummy silent response.
		FireActions(_currentBranch.endActions, _ctx);
		if (!Visible)
		{
			return;
		}

		// Look up the exit group. Empty exitGroup = clean end-of-branch
		// (typewriter done, endActions fired, nothing left to show).
		StringName exitGroup = _currentBranch.exitGroup;
		if (exitGroup == default || exitGroup == "" || _conversation == null)
		{
			Close();
			return;
		}
		ConversationResponseGroup group = FindGroup(_conversation, exitGroup);
		if (group == null || group.responses == null || responseOptionScene == null || responseOptionsContainer == null)
		{
			if (group == null)
			{
				GD.PushWarning($"ConversationController: exit group '{exitGroup}' not found in conversation");
			}
			Close();
			return;
		}

		// Visibility context = the group's canonical entry branch, NOT the
		// current branch. Keeps the visible set stable when looping back
		// through a follow-up branch with shorter / different text.
		ConversationBranch primary = FindPrimaryEntryBranch(_conversation, exitGroup);
		LanguageData lang = primary?.language ?? _ctx.speakerLanguage;
		// Look up the language tuning once. Falls back to a sensible default
		// if SimData is unavailable (e.g. very early bootstrap or tests).
		float grammarWeight = _ctx.sim?.SimData?.languageGrammarWeight ?? 0.2f;
		// Pre-compute the branch score once; Compute mins it with each
		// response's own score so the bottleneck axis caps visibility.
		float branchComp = primary != null
			? ConversationVisibility.ComputeBranchComprehension(primary, lang, _ctx.player, grammarWeight)
			: 1f;
		// Missing components for the primary branch's language — drives
		// the response-text scramble. Uses the same language as the
		// comprehension calc so visibility and scramble agree.
		ELanguageComponents missing = (_ctx.player == null || lang == null)
			? ELanguageComponents.None
			: ELanguageComponents.All & ~_ctx.player.GetLearnedComponents(lang);
		bool debug = CVars.conversationDebug.Value;
		for (int i = 0; i < group.responses.Count; i++)
		{
			ConversationResponse r = group.responses[i];
			if (r == null)
			{
				continue;
			}
			ConversationVisibility.ResponseVisibilityResult vis =
				ConversationVisibility.Compute(r, _ctx, lang, branchComp, grammarWeight);
			// Condition-gated responses stay hidden even in debug — the
			// debug toggle is for the language-comprehension gate only.
			if (!vis.ConditionPassed)
			{
				continue;
			}
			// Roll-hidden + debug-off: skip. Otherwise spawn the button,
			// disabling it when the roll failed so debug shows the gated
			// options visually but unselectable.
			if (!vis.Visible && !debug)
			{
				continue;
			}
			string debugSuffix = debug ? FormatDebugSuffix(vis) : null;
			SpawnResponseButton(r, lang, missing, enabled: vis.Visible, debugSuffix);
		}
		if (_responseButtons.Count == 0)
		{
			Close();
			return;
		}
		// CallDeferred — focus must be grabbed after the button is in the
		// tree and laid out, otherwise GrabFocus is a silent no-op. Find
		// the first enabled button so debug-disabled rows don't trap
		// initial focus.
		for (int i = 0; i < _responseButtons.Count; i++)
		{
			if (!_responseButtons[i].Disabled)
			{
				_responseButtons[i].CallDeferred(Control.MethodName.GrabFocus);
				break;
			}
		}
	}

	static ConversationResponseGroup FindGroup(ConversationData conv, StringName name)
	{
		if (conv.responseGroups == null)
		{
			return null;
		}
		for (int i = 0; i < conv.responseGroups.Count; i++)
		{
			ConversationResponseGroup g = conv.responseGroups[i];
			if (g != null && g.name == name)
			{
				return g;
			}
		}
		return null;
	}

	// Resolves the canonical entry branch for a response group. Walks
	// `branches` once: prefer the first branch whose exitGroup matches
	// and isPrimaryGroupEntry is true; fall back to the first branch
	// exiting to the group at all (so a one-incoming-branch group still
	// has an implicit context without authoring the checkbox). Returns
	// null if no branch exits to this group, which leaves branchComp=1
	// and visibility falls to response-only comprehension.
	static ConversationBranch FindPrimaryEntryBranch(ConversationData conv, StringName groupName)
	{
		if (conv.branches == null)
		{
			return null;
		}
		ConversationBranch fallback = null;
		for (int i = 0; i < conv.branches.Count; i++)
		{
			ConversationBranch b = conv.branches[i];
			if (b == null || b.exitGroup != groupName)
			{
				continue;
			}
			if (b.isPrimaryGroupEntry)
			{
				return b;
			}
			fallback ??= b;
		}
		return fallback;
	}

	static string FormatDebugSuffix(ConversationVisibility.ResponseVisibilityResult vis)
	{
		int scorePct = Mathf.RoundToInt(vis.CombinedScore * 100f);
		int rollPct = Mathf.RoundToInt(vis.Roll * 100f);
		return $"[{scorePct}% / {rollPct}%]";
	}

	void SpawnResponseButton(ConversationResponse response, LanguageData lang, ELanguageComponents missing, bool enabled, string debugSuffix)
	{
		Node instance = responseOptionScene.Instantiate();
		if (instance is not Button btn)
		{
			GD.PushError($"ConversationController: responseOptionScene root must be a Button (got {instance?.GetType().Name ?? "null"})");
			instance?.QueueFree();
			return;
		}
		StringName key = response.textLocKey;
		string label;
		if (key == default || key == "")
		{
			// Silent / continue option — no localized text to scramble.
			label = "...";
		}
		else
		{
			label = Loc.Get(key);
			if (missing != ELanguageComponents.None)
			{
				label = TextScrambler.Scramble(label, lang, missing);
			}
		}
		if (debugSuffix != null)
		{
			label += " " + debugSuffix;
		}
		btn.Text = label;
		btn.Disabled = !enabled;
		// Capture `response` in the closure so the handler knows which
		// branch to advance to without an index lookup. Disabled buttons
		// don't emit Pressed, so it's safe to connect unconditionally.
		btn.Pressed += () => OnResponsePressed(response);
		// FocusAll is the Button default, but be explicit so a custom
		// scene with focus_mode overridden still navigates.
		btn.FocusMode = FocusModeEnum.All;
		responseOptionsContainer.AddChild(btn);
		_responseButtons.Add(btn);
	}

	void OnResponsePressed(ConversationResponse response)
	{
		if (_state != EState.Choosing)
		{
			return;
		}
		// Fire actions FIRST so an action can close / redirect before the
		// destination lookup runs.
		FireActions(response.actions, _ctx);
		if (!Visible)
		{
			return;
		}
		StringName dest = response.destination;
		if (dest == default || dest == "")
		{
			Close();
			return;
		}
		ConversationBranch next = FindBranch(_conversation, dest);
		if (next == null)
		{
			GD.PushWarning($"ConversationController: response destination '{dest}' not found in conversation");
			Close();
			return;
		}
		StartBranch(next);
	}

	void ClearResponseButtons()
	{
		for (int i = 0; i < _responseButtons.Count; i++)
		{
			_responseButtons[i].QueueFree();
		}
		_responseButtons.Clear();
	}

	public override void _Process(double delta)
	{
		if (_state != EState.Typing || label == null || _lineIndex >= _lines.Count)
		{
			return;
		}
		string line = _lines[_lineIndex];
		if (_revealedChars >= line.Length)
		{
			return;
		}
		float speed = Mathf.Max(0f, CVars.dialogueTypingSpeed.Value);
		_revealedChars = Mathf.Min(line.Length, _revealedChars + speed * (float)delta);
		int reveal = Mathf.Min(line.Length, Mathf.FloorToInt(_revealedChars));
		label.Text = line.Substring(0, reveal);
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible)
		{
			return;
		}
		// Cancel works in both Typing and Choosing — Button doesn't
		// consume ui_cancel so the event makes it here from focused
		// response buttons too.
		if (e.IsActionPressed("ui_cancel"))
		{
			GetViewport().SetInputAsHandled();
			Close();
			return;
		}
		// Accept-to-advance is only the typewriter's concern. In Choosing
		// state the focused Button's _gui_input handles ui_accept and
		// emits Pressed, which routes through OnResponsePressed.
		if (_state != EState.Typing || !e.IsActionPressed("ui_accept"))
		{
			return;
		}
		GetViewport().SetInputAsHandled();

		if (_lineIndex >= _lines.Count)
		{
			ShowResponseChooser();
			return;
		}
		string line = _lines[_lineIndex];
		if (_revealedChars < line.Length)
		{
			_revealedChars = line.Length;
			if (label != null)
			{
				label.Text = line;
			}
			return;
		}
		_lineIndex++;
		if (_lineIndex >= _lines.Count)
		{
			ShowResponseChooser();
			return;
		}
		_revealedChars = 0f;
		if (label != null)
		{
			label.Text = string.Empty;
		}
	}

	// Walks entryBranches in order, returning the first entry whose
	// condition passes (or whose condition is null). The branch lookup
	// itself runs in Show — keeping it here would discard the entry's
	// actions list.
	static ConversationEntry SelectEntry(ConversationData conv, ConversationContext ctx)
	{
		if (conv.entryBranches == null)
		{
			return null;
		}
		for (int i = 0; i < conv.entryBranches.Count; i++)
		{
			ConversationEntry entry = conv.entryBranches[i];
			if (entry == null)
			{
				continue;
			}
			if (entry.condition != null && !entry.condition.Evaluate(ctx))
			{
				continue;
			}
			return entry;
		}
		return null;
	}

	// Fires actions in array order. Null entries are skipped; the runtime
	// does not enforce ordering between actions and the branch text — an
	// action that closes the conversation suppresses the next branch.
	static void FireActions(Godot.Collections.Array<ConversationAction> actions, ConversationContext ctx)
	{
		if (actions == null)
		{
			return;
		}
		for (int i = 0; i < actions.Count; i++)
		{
			ConversationAction a = actions[i];
			if (a != null)
			{
				a.Execute(ctx);
			}
		}
	}

	static ConversationBranch FindBranch(ConversationData conv, StringName name)
	{
		if (conv.branches == null)
		{
			return null;
		}
		for (int i = 0; i < conv.branches.Count; i++)
		{
			ConversationBranch b = conv.branches[i];
			if (b != null && b.name == name)
			{
				return b;
			}
		}
		return null;
	}

	void ResolveAndScrambleLines(ConversationBranch branch, ConversationContext ctx)
	{
		_lines.Clear();
		if (branch.lineLocKeys == null)
		{
			return;
		}
		LanguageData lang = branch.language ?? ctx.speakerLanguage;
		ELanguageComponents missing = (ctx.player == null || lang == null)
			? ELanguageComponents.None
			: ELanguageComponents.All & ~ctx.player.GetLearnedComponents(lang);
		for (int i = 0; i < branch.lineLocKeys.Count; i++)
		{
			StringName key = branch.lineLocKeys[i];
			if (key == default || key == "")
			{
				continue;
			}
			string text = Loc.Get(key);
			_lines.Add(missing == ELanguageComponents.None
				? text
				: TextScrambler.Scramble(text, lang, missing));
		}
	}
}
