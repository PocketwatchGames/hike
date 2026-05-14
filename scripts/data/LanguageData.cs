using Godot;

// A spoken / written language in the world. Mobs reference one as their
// native tongue (drives chatter localization + comprehension gating); signposts
// reference one as the language their text is written in. The shared resource
// instance is the key used in Player.LearnedLanguages, so two mobs that share
// a LanguageData are mutually intelligible to a player who has learned it.
[GlobalClass]
public partial class LanguageData : Resource
{
    // Text shown wherever the language is named in UI (learned-language list,
    // tooltip on unreadable signpost text, etc). Same StringName-as-display-
    // text convention as RegionData.displayName.
    [Export] public StringName displayName;
}
