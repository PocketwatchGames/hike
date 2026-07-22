using Godot;
using Godot.Collections;

// Conditional partial-override layer on top of a base DamageData. Authored
// as an entry in DamageData.modifiers; the receiver folds matching entries
// onto the live hit via HitInfo.ApplyTrigger when its trigger condition is
// met (crit-eligible target, this hit landed dizzy, etc.).
//
// `overrides` is a flag mask — only the fields whose bit is set are read.
// The inspector hides untoggled fields via `_ValidateProperty`; storage is
// preserved while hidden so toggling a flag off and back on doesn't drop
// previously-authored values (same pattern as ItemEvent). `[Tool]` is
// required for `_ValidateProperty` to fire in the editor.
[Tool]
[GlobalClass]
public partial class DamageDataModifier : Resource
{
	[Export] public EDamageTrigger trigger = EDamageTrigger.OnCrit;

	private EDamageFields _overrides;
	[Export, CompactFlags] public EDamageFields overrides
	{
		get => _overrides;
		set
		{
			if (_overrides == value) { return; }
			_overrides = value;
			// Defer the property-list rebuild so a custom editor that triggered
			// this set (FlagsPropertyEditor) isn't torn down mid-callback —
			// same shape as ItemEvent.type.
			CallDeferred(MethodName.NotifyPropertyListChanged);
			EmitChanged();
		}
	}

	[Export] public float healthDamage;
	// Relative alternative to the absolute healthDamage replacement above:
	// scales the live hit's damage (health, plus the armor chip / aggro that
	// derive from it) — the natural authoring for "crits deal 3× damage".
	[Export] public float damageMultiplier = 1f;
	[Export] public float hitstun;
	[Export] public float knockbackDistance;
	[Export] public float knockbackTime;
	[Export(PropertyHint.Range, "0,1,0.01")] public float armorPenetration;
	[Export] public float blunt;
	// Appended to the running buildups list (NOT replacing it) when the
	// AddBuildups bit is set — meter contributions and/or immediate-apply
	// effects (StatusEffectBuildup.applyImmediately). See EDamageFields.AddBuildups.
	[Export] public Array<StatusEffectBuildup> addBuildups;

	public override void _ValidateProperty(Dictionary property)
	{
		string name = property["name"].AsString();
		EDamageFields requiredFlag = GetRequiredFlag(name);
		if (requiredFlag == EDamageFields.None) { return; }
		if ((_overrides & requiredFlag) != 0) { return; }
		PropertyUsageFlags usage = property["usage"].As<PropertyUsageFlags>() & ~PropertyUsageFlags.Editor;
		property["usage"] = (int)usage;
	}

	private static EDamageFields GetRequiredFlag(string fieldName)
	{
		return fieldName switch
		{
			nameof(healthDamage) => EDamageFields.HealthDamage,
			nameof(damageMultiplier) => EDamageFields.DamageMultiplier,
			nameof(hitstun) => EDamageFields.Hitstun,
			nameof(knockbackDistance) => EDamageFields.KnockbackDistance,
			nameof(knockbackTime) => EDamageFields.KnockbackTime,
			nameof(addBuildups) => EDamageFields.AddBuildups,
			nameof(armorPenetration) => EDamageFields.ArmorPenetration,
			nameof(blunt) => EDamageFields.Blunt,
			_ => EDamageFields.None,
		};
	}
}
