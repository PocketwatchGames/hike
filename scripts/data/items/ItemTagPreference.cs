using Godot;

// One rule in a mob's taste model (MobData.itemPreferences). Multiplies the
// subjective value of an item by `multiplier` when the item's typeTags match.
// By default a rule fires when the item carries ANY of `tags`; set
// `whenMissing` to flip it so the rule instead fires when the item carries
// NONE of `tags`.
//
// This single shape covers both "preference" and "requirement": a requirement
// is just a rule whose multiplier is 0 (rejects the item outright), and a
// preference is a rule whose multiplier is >1. Rules compose multiplicatively
// in author order, so a species layers as many likes/dislikes as it needs.
//   Dog:      { Meat, whenMissing=true, x0 }  -> anything that isn't meat is worthless.
//   Villager: { Gross, x0.1 }, { Magic, x3 }, { Potion, x1.5 }, ... -> layered taste.
[GlobalClass]
public partial class ItemTagPreference : Resource
{
    // Tag(s) this rule keys on. A flags mask — matching is ANY-of when more
    // than one bit is set.
    [Export] public EItemType tags = EItemType.None;

    // false: rule fires when the item HAS any of `tags`.
    // true:  rule fires when the item has NONE of `tags`.
    [Export] public bool whenMissing = false;

    // Subjective-value multiplier applied when the rule fires. 0 rejects the
    // item (a requirement); <1 dislikes; >1 prefers.
    [Export(PropertyHint.Range, "0,10,0.05,or_greater")] public float multiplier = 1f;

    // Whether this rule applies to an item carrying `itemTags`.
    public bool Matches(EItemType itemTags)
    {
        bool hasAny = (itemTags & tags) != EItemType.None;
        return whenMissing ? !hasAny : hasAny;
    }
}
