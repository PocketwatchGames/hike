#if TOOLS

using Godot;
using System.Reflection;

// Dispatcher for custom inspector property editors. Swaps Godot's default
// 32-checkbox grid for the compact FlagsPropertyEditor dropdown on flags
// properties that opt in via [CompactFlags]. Engine-wide properties with
// PropertyHint.Flags (collision layers, physics layers, etc.) are left
// alone so we don't fight Godot's own UI.
[Tool]
public partial class DataListEditorInspector : EditorInspectorPlugin
{
	private const BindingFlags ReflectionScope =
		BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

	public override bool _CanHandle(GodotObject @object)
	{
		return true;
	}

	public override bool _ParseProperty(
		GodotObject @object,
		Variant.Type type,
		string name,
		PropertyHint hintType,
		string hintString,
		PropertyUsageFlags usageFlags,
		bool wide)
	{
		if (hintType != PropertyHint.Flags || type != Variant.Type.Int)
		{
			return false;
		}
		if (!HasCompactFlags(@object, name))
		{
			return false;
		}
		AddPropertyEditor(name, new FlagsPropertyEditor(hintString));
		return true;
	}

	private static bool HasCompactFlags(GodotObject @object, string memberName)
	{
		if (@object == null)
		{
			return false;
		}
		// Walk up the type chain — Godot's exported property name matches the
		// C# property/field name verbatim, so a single GetMember lookup on
		// the runtime type plus its bases catches inherited declarations.
		for (System.Type t = @object.GetType(); t != null; t = t.BaseType)
		{
			MemberInfo[] members = t.GetMember(memberName, ReflectionScope);
			for (int i = 0; i < members.Length; i++)
			{
				if (members[i].GetCustomAttribute<CompactFlagsAttribute>() != null)
				{
					return true;
				}
			}
		}
		return false;
	}
}

#endif
