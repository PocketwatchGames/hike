using System;
using Microsoft.CodeAnalysis;

// Shared symbol predicates for the Hike convention analyzers.
internal static class HikeSymbols
{
	public const string Category = "HikeConventions";

	// Walks the base chain by display name. Godot types are matched fully
	// qualified ("Godot.Resource"); repo types have no namespace, so their
	// display string is the bare class name ("Fx").
	public static bool DerivesFrom(ITypeSymbol type, string fullyQualifiedName)
	{
		for (INamedTypeSymbol t = type as INamedTypeSymbol; t != null; t = t.BaseType)
		{
			if (t.ToDisplayString() == fullyQualifiedName)
			{
				return true;
			}
		}
		return false;
	}

	public static bool HasAttribute(ISymbol symbol, string attributeClassName)
	{
		foreach (AttributeData a in symbol.GetAttributes())
		{
			if (a.AttributeClass?.Name == attributeClassName)
			{
				return true;
			}
		}
		return false;
	}

	// True for types that live in the engine assembly (Godot.*). These carry no
	// script, so [Tool] cannot gate them and they are never flagged.
	public static bool IsEngineType(ITypeSymbol type)
	{
		string ns = type?.ContainingNamespace?.ToDisplayString();
		return ns != null && (ns == "Godot" || ns.StartsWith("Godot.", StringComparison.Ordinal));
	}

	// Peels arrays, Godot.Collections.Array<T> and List<T> down to the element
	// type, so an exported collection is checked like a bare field.
	public static ITypeSymbol UnwrapCollection(ITypeSymbol type)
	{
		for (int guard = 0; guard < 4 && type != null; guard++)
		{
			if (type is IArrayTypeSymbol array)
			{
				type = array.ElementType;
				continue;
			}
			if (type is INamedTypeSymbol named && named.IsGenericType && named.TypeArguments.Length > 0)
			{
				string def = named.OriginalDefinition.ToDisplayString();
				if (def.StartsWith("Godot.Collections.Array<", StringComparison.Ordinal)
					|| def.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal))
				{
					type = named.TypeArguments[named.TypeArguments.Length - 1];
					continue;
				}
			}
			break;
		}
		return type;
	}
}
