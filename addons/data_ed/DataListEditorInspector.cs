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
		System.Type startType = ResolveScriptType(@object);
		// Walk up the type chain — Godot's exported property name matches the
		// C# property/field name verbatim. GetField/GetProperty are queried
		// separately because GetMember on .NET 8 can return zero hits for a
		// plain `[Export] public Foo bar;` declaration even though the field
		// is present — the explicit lookups are unambiguous and faster.
		for (System.Type t = startType; t != null; t = t.BaseType)
		{
			FieldInfo field = t.GetField(memberName, ReflectionScope);
			if (field != null && field.IsDefined(typeof(CompactFlagsAttribute), inherit: false))
			{
				return true;
			}
			PropertyInfo property = t.GetProperty(memberName, ReflectionScope);
			if (property != null && property.IsDefined(typeof(CompactFlagsAttribute), inherit: false))
			{
				return true;
			}
		}
		return false;
	}

	// Sub-resources nested inside another resource come through the inspector
	// hook with @object.GetType() == typeof(Godot.Resource) — Godot's variant
	// boundary drops the C# managed type for nested-resource references and
	// hands the dispatcher the base Resource wrapper. Top-level resources keep
	// their C# type (which is why ItemEvent.type works directly), but a
	// LanguageTeachable embedded in a ScrollData.concept slot does not. Fall
	// back to the attached script's source file name to resolve the real
	// C# type — every script in this project sits in a .cs file whose
	// stem matches the class name.
	private static System.Type ResolveScriptType(GodotObject @object)
	{
		System.Type t = @object.GetType();
		if (t != typeof(Resource) && t != typeof(GodotObject))
		{
			return t;
		}
		Variant scriptVar = @object.Get("script");
		if (scriptVar.VariantType != Variant.Type.Object)
		{
			return t;
		}
		if (scriptVar.AsGodotObject() is not Script script)
		{
			return t;
		}
		string path = script.ResourcePath;
		if (string.IsNullOrEmpty(path))
		{
			return t;
		}
		// Extract "LanguageTeachable" from "res://.../LanguageTeachable.cs".
		int lastSlash = path.LastIndexOf('/');
		int lastDot = path.LastIndexOf('.');
		if (lastDot <= lastSlash + 1)
		{
			return t;
		}
		string className = path.Substring(lastSlash + 1, lastDot - lastSlash - 1);
		System.Reflection.Assembly[] assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
		for (int i = 0; i < assemblies.Length; i++)
		{
			System.Type found = assemblies[i].GetType(className, throwOnError: false);
			if (found != null)
			{
				return found;
			}
		}
		return t;
	}
}

#endif
