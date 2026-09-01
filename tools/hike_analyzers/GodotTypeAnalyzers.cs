using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

// HK002: a Godot Node/Resource subclass that is not [GlobalClass].
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GlobalClassAnalyzer : DiagnosticAnalyzer
{
	private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
		"HK002",
		"Godot type is missing [GlobalClass]",
		"'{0}' derives from {1} but is not marked [GlobalClass]",
		HikeSymbols.Category,
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		description: "CLAUDE.md Key Conventions: any class derived from a Godot Node or Resource must be tagged [GlobalClass]. Abstract, generic and nested types are exempt - the editor cannot register those.");

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

	public override void Initialize(AnalysisContext context)
	{
		context.EnableConcurrentExecution();
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
	}

	private static void Analyze(SymbolAnalysisContext context)
	{
		var type = (INamedTypeSymbol)context.Symbol;
		if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic
			|| type.IsGenericType || type.ContainingType != null)
		{
			return;
		}

		string baseName = HikeSymbols.DerivesFrom(type, "Godot.Node") ? "Godot.Node"
			: HikeSymbols.DerivesFrom(type, "Godot.Resource") ? "Godot.Resource"
			: null;
		if (baseName == null || HikeSymbols.HasAttribute(type, "GlobalClassAttribute"))
		{
			return;
		}

		foreach (Location location in type.Locations)
		{
			if (location.IsInSource)
			{
				context.ReportDiagnostic(Diagnostic.Create(Rule, location, type.Name, baseName));
				return;
			}
		}
	}
}

// HK003: the [Tool] closure. A [Tool] class exporting a scripted Resource type
// that is not itself [Tool] loses that reference the next time the editor saves.
// This is the runtime `resource_check` rule, moved to the compiler so it fires
// before any data is lost.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ToolClosureAnalyzer : DiagnosticAnalyzer
{
	private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
		"HK003",
		"[Tool] closure gap - exported resource type is not [Tool]",
		"'{0}' is exported from [Tool] class '{1}' but '{0}' is not [Tool]; the editor will materialize it as a bare Godot.Resource and drop the reference on save",
		HikeSymbols.Category,
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description: "CLAUDE.md Key Conventions: every Resource type reachable from a typed [Export] on a [Tool] class must itself be [Tool]. Editor-only data loss - runtime has no [Tool] gate and hides it.");

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

	public override void Initialize(AnalysisContext context)
	{
		context.EnableConcurrentExecution();
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
	}

	private static void Analyze(SymbolAnalysisContext context)
	{
		var type = (INamedTypeSymbol)context.Symbol;
		if (type.TypeKind != TypeKind.Class || !HikeSymbols.HasAttribute(type, "ToolAttribute"))
		{
			return;
		}

		foreach (ISymbol member in type.GetMembers())
		{
			ITypeSymbol memberType = member is IFieldSymbol f ? f.Type
				: member is IPropertySymbol p ? p.Type
				: null;
			if (memberType == null || !HikeSymbols.HasAttribute(member, "ExportAttribute"))
			{
				continue;
			}

			ITypeSymbol element = HikeSymbols.UnwrapCollection(memberType);
			// Engine types carry no script for [Tool] to gate; a field typed as
			// bare Godot.Resource has no cast that can fail.
			if (element == null || HikeSymbols.IsEngineType(element)
				|| !HikeSymbols.DerivesFrom(element, "Godot.Resource")
				|| HikeSymbols.HasAttribute(element, "ToolAttribute"))
			{
				continue;
			}

			foreach (Location location in member.Locations)
			{
				if (location.IsInSource)
				{
					context.ReportDiagnostic(Diagnostic.Create(Rule, location, element.Name, type.Name));
					break;
				}
			}
		}
	}
}

// HK008: `.Count` on a Godot.Collections container in a loop condition. Each
// read is a native call, so the loop crosses the managed/native boundary twice
// per element. Hoist it into a local.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GodotCollectionCountAnalyzer : DiagnosticAnalyzer
{
	private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
		"HK008",
		"Godot collection .Count read in a loop condition",
		"'.Count' on Godot collection '{0}' is re-evaluated every iteration; hoist it into a local",
		HikeSymbols.Category,
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		description: "CLAUDE.md: Godot.Collections.Array/Dictionary are handles over a native Variant container. .Count is itself a native call, so a loop condition crosses the boundary once per element on top of each element access.");

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

	public override void Initialize(AnalysisContext context)
	{
		context.EnableConcurrentExecution();
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ForStatement, SyntaxKind.WhileStatement);
	}

	private static void Analyze(SyntaxNodeAnalysisContext context)
	{
		ExpressionSyntax condition = context.Node is ForStatementSyntax forStatement
			? forStatement.Condition
			: ((WhileStatementSyntax)context.Node).Condition;
		if (condition == null)
		{
			return;
		}

		foreach (MemberAccessExpressionSyntax access in condition.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
		{
			if (access.Name.Identifier.ValueText != "Count")
			{
				continue;
			}
			ITypeSymbol owner = context.SemanticModel.GetTypeInfo(access.Expression, context.CancellationToken).Type;
			string def = owner?.OriginalDefinition?.ToDisplayString();
			if (def == null)
			{
				continue;
			}
			if (def.StartsWith("Godot.Collections.Array", StringComparison.Ordinal)
				|| def.StartsWith("Godot.Collections.Dictionary", StringComparison.Ordinal))
			{
				context.ReportDiagnostic(Diagnostic.Create(Rule, access.GetLocation(), access.Expression.ToString()));
			}
		}
	}
}
