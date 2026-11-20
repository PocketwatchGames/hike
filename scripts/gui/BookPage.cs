using System;
using Godot;

// A page of prose read from a knowledge source whose scene reads as a page (a
// book) rather than a speech bubble (a stone). When the read teaches the
// language the prose is written in, the learn label and meter sit under it and
// the prose translates itself as the meter fills (TranslationAnimator) — the
// discovery banner for that language is not shown, the page IS that banner.
//
// A press while it plays finishes it; the next press, or cancel, closes it.
// While open, gameClient.InputSuppressed is on so the press doesn't fall
// through to gameplay, as with ConversationController.
[GlobalClass]
public partial class BookPage : Control
{
    [Export] public GameClient gameClient;
    // The inscription's title, hidden when it has none.
    [Export] RichTextLabel _title;
    [Export] RichTextLabel _prose;
    // The label and meter, shown only when the read teaches a language.
    [Export] Control _meterRow;
    [Export] Label _meterLabel;
    [Export] ProgressBar _meter;
    [Export] AudioStreamPlayer _openSound;

    [ExportGroup("Translation")]
    // The meter sits at its old value this long before filling.
    [Export(PropertyHint.Range, "0,5,0.05")] float _holdBeforeSeconds = 0.6f;
    // The whole translation plays in this time: every letter lands and every
    // word reaches its place as the meter fills.
    [Export(PropertyHint.Range, "0,20,0.1")] float _fillSeconds = 4f;
    // A revealed word's letters each land at a random moment within this window.
    [Export(PropertyHint.Range, "0,5,0.05")] float _revealWindowSeconds = 0.9f;
    // How long before landing a letter flickers through random glyphs.
    [Export(PropertyHint.Range, "0,2,0.01")] float _flickerSeconds = 0.3f;
    [Export(PropertyHint.Range, "0.01,0.5,0.01")] float _flickerIntervalSeconds = 0.06f;

    // The prose drives the meter; the title plays on the same clock beside it.
    TranslationAnimator _animator;
    TranslationAnimator _titleAnimator;
    LanguageData _taught;
    float _from;
    float _to;
    Action _onClose;
    Color _textColor = Colors.White;
    Color _titleColor = Colors.White;

    public bool IsOpen => Visible;

    // Opens the page on `raw` (authored text, LanguageText markup allowed) in
    // `language`, headed by `title` (empty = none). `taught` is the language the
    // read taught (null = none) and `from` / `to` the amount of it learned
    // before and after.
    public void Open(string title, string raw, LanguageData language, LanguageData taught, Player player,
        float from, float to, Action onClose = null)
    {
        Close();
        _onClose = onClose;
        _taught = taught;
        _from = from;
        _to = to;
        _textColor = _prose?.GetThemeColor("default_color") ?? Colors.White;
        _titleColor = _title?.GetThemeColor("default_color") ?? Colors.White;
        var tuning = new TranslationAnimator.Tuning
        {
            holdBeforeSeconds = _holdBeforeSeconds,
            fillSeconds = _fillSeconds,
            revealWindowSeconds = _revealWindowSeconds,
            flickerSeconds = _flickerSeconds,
            flickerIntervalSeconds = _flickerIntervalSeconds,
        };
        _animator = new TranslationAnimator(raw, language, taught, player, from, to, tuning);
        _titleAnimator = string.IsNullOrEmpty(title)
            ? null : new TranslationAnimator(title, language, taught, player, from, to, tuning);
        if (_title != null)
        {
            _title.Visible = _titleAnimator != null;
        }
        if (_meterRow != null)
        {
            _meterRow.Visible = taught != null;
        }
        if (_meterLabel != null && taught != null)
        {
            string name = taught.displayName.ToString();
            _meterLabel.Text = to > from
                ? Loc.Format(Loc.Keys.language_learned_progress, name,
                    Mathf.RoundToInt(from * 100f), Mathf.RoundToInt(to * 100f))
                : Loc.Format(Loc.Keys.book_language_known, name, Mathf.RoundToInt(to * 100f));
        }
        Refresh();
        if (gameClient != null)
        {
            gameClient.InputSuppressed = true;
        }
        Visible = true;
        _openSound?.Play();
    }

    public void Close()
    {
        if (!Visible)
        {
            return;
        }
        Visible = false;
        _animator = null;
        _titleAnimator = null;
        if (gameClient != null)
        {
            gameClient.InputSuppressed = false;
        }
        Action cb = _onClose;
        _onClose = null;
        cb?.Invoke();
    }

    public override void _Process(double delta)
    {
        if (!Visible || _animator == null || _animator.IsDone)
        {
            return;
        }
        _titleAnimator?.Advance((float)delta);
        if (_animator.Advance((float)delta))
        {
            Refresh();
        }
    }

    void Refresh()
    {
        if (_animator == null)
        {
            return;
        }
        if (_prose != null)
        {
            _prose.Text = _animator.Bbcode(_textColor);
        }
        if (_title != null && _titleAnimator != null)
        {
            _title.Text = _titleAnimator.Bbcode(_titleColor);
        }
        if (_meter != null)
        {
            _meter.Value = _animator.Learned;
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible)
        {
            return;
        }
        if (e.IsActionPressed("ui_cancel"))
        {
            GetViewport().SetInputAsHandled();
            Close();
            return;
        }
        if (!e.IsActionPressed("ui_accept"))
        {
            return;
        }
        GetViewport().SetInputAsHandled();
        if (_animator != null && !_animator.IsDone)
        {
            _animator.Finish();
            _titleAnimator?.Finish();
            Refresh();
            return;
        }
        Close();
    }
}
