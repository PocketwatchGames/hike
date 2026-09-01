using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

// HK001: a literal res:// path passed to GD.Load / ResourceLoader.Load.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HardcodedResourcePathAnalyzer : DiagnosticAnalyzer
{
	private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
		"HK001",
		"Hardcoded res:// path",
		"'{0}' is loaded by literal path; add an [Export] of the appropriate Data type and wire the .tres in the editor",
		HikeSymbols.Category,
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		description: "CLAUDE.md Key Conventions: never hardcode resource paths in C#. Generic infrastructure with no upstream owner is the only exception - silence those per-file in .editorconfig so the exemption stays visible.");

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

	public override void Initialize(AnalysisContext context)
	{
		context.EnableConcurrentExecution();
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
	}

	private static void Analyze(SyntaxNodeAnalysisContext context)
	{
		var invocation = (InvocationExpressionSyntax)context.Node;
		var method = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
		string owner = method?.ContainingType?.ToDisplayString();
		if (owner != "Godot.GD" && owner != "Godot.ResourceLoader")
		{
			return;
		}
		if (method.Name != "Load" && method.Name != "LoadThreadedRequest" && method.Name != "Exists")
		{
			return;
		}

		foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
		{
			if (argument.Expression is LiteralExpressionSyntax literal
				&& literal.IsKind(SyntaxKind.StringLiteralExpression)
				&& literal.Token.ValueText.StartsWith("res://", StringComparison.Ordinal))
			{
				context.ReportDiagnostic(Diagnostic.Create(Rule, literal.GetLocation(), literal.Token.ValueText));
			}
		}
	}
}

// HK004: an [Export] float/double initialized at or below the default 0.001
// spinbox step. The editor snaps such a value on UI input (often to zero) while
// the .tres still reads correct.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ExportPrecisionAnalyzer : DiagnosticAnalyzer
{
	// Godot's default [Export] float spinbox step is 0.001. The bound sits
	// clear of 0.01 because 0.01f stores as 0.00999999976 and would otherwise
	// trip the check on its own literal.
	private const double UnsafeMagnitude = 0.005;

	private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
		"HK004",
		"[Export] float authored near the default spinbox step",
		"'{0}' defaults to {1}, at or below Godot's 0.001 default step; add [Export(PropertyHint.Range, ...)] with finer precision, or invert the unit",
		HikeSymbols.Category,
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		description: "CLAUDE.md Key Conventions: exported floats authored at sub-0.01 magnitudes need explicit precision. The snap happens on UI input, not on save, so the .tres looks fine while the value the editor writes back is wrong.");

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

	public override void Initialize(AnalysisContext context)
	{
		context.EnableConcurrentExecution();
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.FieldDeclaration, SyntaxKind.PropertyDeclaration);
	}

	private static void Analyze(SyntaxNodeAnalysisContext context)
	{
		SyntaxList<AttributeListSyntax> attributeLists;
		TypeSyntax declaredType;
		string memberName;
		if (context.Node is FieldDeclarationSyntax field)
		{
			attributeLists = field.AttributeLists;
			declaredType = field.Declaration.Type;
			memberName = field.Declaration.Variables.First().Identifier.ValueText;
		}
		else
		{
			var property = (PropertyDeclarationSyntax)context.Node;
			attributeLists = property.AttributeLists;
			declaredType = property.Type;
			memberName = property.Identifier.ValueText;
		}

		AttributeSyntax export = attributeLists
			.SelectMany(list => list.Attributes)
			.FirstOrDefault(a => a.Name.ToString() == "Export" || a.Name.ToString() == "ExportAttribute");
		if (export == null)
		{
			return;
		}
		// A Range hint states the precision explicitly - that is the fix.
		if (export.ArgumentList != null
			&& export.ArgumentList.Arguments.Any(a => a.ToString().Contains("Range")))
		{
			return;
		}

		ITypeSymbol type = context.SemanticModel.GetTypeInfo(declaredType, context.CancellationToken).Type;
		if (type == null
			|| (type.SpecialType != SpecialType.System_Single && type.SpecialType != SpecialType.System_Double))
		{
			return;
		}

		foreach (EqualsValueClauseSyntax initializer in context.Node.DescendantNodes().OfType<EqualsValueClauseSyntax>())
		{
			Optional<object> constant = context.SemanticModel.GetConstantValue(initializer.Value, context.CancellationToken);
			if (!constant.HasValue || constant.Value == null)
			{
				continue;
			}

			double value;
			try
			{
				value = Convert.ToDouble(constant.Value, CultureInfo.InvariantCulture);
			}
			catch (Exception)
			{
				continue;
			}

			double magnitude = Math.Abs(value);
			if (magnitude > 0 && magnitude < UnsafeMagnitude)
			{
				context.ReportDiagnostic(Diagnostic.Create(
					Rule,
					initializer.Value.GetLocation(),
					memberName,
					value.ToString(CultureInfo.InvariantCulture)));
			}
		}
	}
}

// HK005: node lookups in _Ready. Wire an [Export] and assign the path in the
// .tscn instead - a lookup goes silently wrong when the tree shape changes.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NodeLookupInReadyAnalyzer : DiagnosticAnalyzer
{
	private static readonly ImmutableHashSet<string> LookupMethods = ImmutableHashSet.Create(
		"GetNode", "GetNodeOrNull", "GetChild", "GetChildren", "FindChild", "FindChildren");

	private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
		"HK005",
		"Node lookup in _Ready",
		"'{0}' in _Ready; declare an [Export] field and assign the node path in the .tscn instead",
		HikeSymbols.Category,
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		description: "CLAUDE.md Key Conventions: never look up child nodes by iterating children or using GetNode/GetChild in _Ready.");

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

	public override void Initialize(AnalysisContext context)
	{
		context.EnableConcurrentExecution();
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.MethodDeclaration);
	}

	private static void Analyze(SyntaxNodeAnalysisContext context)
	{
		var method = (MethodDeclarationSyntax)context.Node;
		if (method.Identifier.ValueText != "_Ready")
		{
			return;
		}

		foreach (InvocationExpressionSyntax invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
		{
			string name = null;
			if (invocation.Expression is MemberAccessExpressionSyntax access)
			{
				name = access.Name.Identifier.ValueText;
			}
			else if (invocation.Expression is GenericNameSyntax generic)
			{
				name = generic.Identifier.ValueText;
			}
			else if (invocation.Expression is IdentifierNameSyntax identifier)
			{
				name = identifier.Identifier.ValueText;
			}

			if (name != null && LookupMethods.Contains(name))
			{
				context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), name));
			}
		}
	}
}

// HK006/HK007: engine objects that must be authored as scenes/resources rather
// than constructed at runtime.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RuntimeConstructionAnalyzer : DiagnosticAnalyzer
{
	private static readonly ImmutableHashSet<string> FxNodeTypes = ImmutableHashSet.Create(
		"GpuParticles3D", "GpuParticles2D", "CpuParticles3D", "CpuParticles2D",
		"AudioStreamPlayer", "AudioStreamPlayer2D", "AudioStreamPlayer3D");

	private static readonly DiagnosticDescriptor FxRule = new DiagnosticDescriptor(
		"HK006",
		"Particle/audio node constructed outside an Fx scene",
		"'{0}' is constructed in code; author it as an Fx .tscn and spawn it with Fx.Create",
		HikeSymbols.Category,
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		description: "CLAUDE.md Subsystems / Fx: a raw GpuParticles3D or AudioStreamPlayer3D under a non-Fx root never starts.");

	private static readonly DiagnosticDescriptor MaterialRule = new DiagnosticDescriptor(
		"HK007",
		"Godot Material constructed at runtime",
		"'{0}' is constructed in code; author it as a .tres and wire it via an [Export]",
		HikeSymbols.Category,
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		description: "CLAUDE.md Key Conventions: Godot resources (materials, shaders, meshes) should not be created programmatically at runtime.");

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
		=> ImmutableArray.Create(FxRule, MaterialRule);

	public override void Initialize(AnalysisContext context)
	{
		context.EnableConcurrentExecution();
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression);
	}

	private static void Analyze(SyntaxNodeAnalysisContext context)
	{
		var creation = (ObjectCreationExpressionSyntax)context.Node;
		ITypeSymbol created = context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type;
		if (created == null || !HikeSymbols.IsEngineType(created))
		{
			return;
		}

		if (FxNodeTypes.Contains(created.Name))
		{
			// Fx itself owns this plumbing; everything else must go through it.
			INamedTypeSymbol enclosing = context.ContainingSymbol?.ContainingType;
			if (enclosing != null && HikeSymbols.DerivesFrom(enclosing, "Fx"))
			{
				return;
			}
			context.ReportDiagnostic(Diagnostic.Create(FxRule, creation.GetLocation(), created.Name));
			return;
		}

		if (HikeSymbols.DerivesFrom(created, "Godot.Material"))
		{
			context.ReportDiagnostic(Diagnostic.Create(MaterialRule, creation.GetLocation(), created.Name));
		}
	}
}
