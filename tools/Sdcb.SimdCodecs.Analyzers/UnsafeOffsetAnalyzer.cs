using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Sdcb.SimdCodecs.Analyzers;

/// <summary>
/// SDC0001: the offset of <c>Unsafe.Add</c> / <c>Unsafe.Subtract</c> must be <c>nint</c> /
/// <c>nuint</c> or a constant, never an <c>int</c> expression.
/// </summary>
/// <remarks>
/// The Mono JIT (.NET for Android without AOT, arm64) zero-extends a computed negative
/// <c>int</c> offset instead of sign-extending it, so <c>Unsafe.Add(ref p, a - b)</c> with
/// <c>a - b == -3</c> lands gigabytes past <c>p</c> (dotnet/runtime#138452). A parameter can
/// be safe on its own and become computed once the method is inlined, so every non-constant
/// <c>int</c> is reported, not only expressions. <c>(nint)</c> costs nothing on CoreCLR: the
/// <c>int</c> overload sign-extends the same way.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnsafeOffsetAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "SDC0001";

    private static readonly DiagnosticDescriptor Rule = new(
        Id,
        "Unsafe.Add / Unsafe.Subtract offset must be nint",
        "The offset '{1}' of Unsafe.{0} is an int; cast it to nint, because the Mono JIT does not sign-extend a computed int offset",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The Mono JIT (.NET for Android) zero-extends a computed negative int offset of Unsafe.Add / Unsafe.Subtract, so the access lands gigabytes away. See AGENTS.md.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var unsafeType = start.Compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.Unsafe");
            if (unsafeType is null) return;
            start.RegisterOperationAction(c => Analyze(c, unsafeType), OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol unsafeType)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (method.Name is not ("Add" or "Subtract")
            || !SymbolEqualityComparer.Default.Equals(method.ContainingType, unsafeType)
            || method.Parameters.Length != 2
            || method.Parameters[1].Type.SpecialType != SpecialType.System_Int32)
            return;

        foreach (var argument in invocation.Arguments)
        {
            if (!SymbolEqualityComparer.Default.Equals(argument.Parameter, method.Parameters[1])) continue;
            if (argument.Value.ConstantValue.HasValue) return;
            var syntax = argument.Value.Syntax;
            context.ReportDiagnostic(Diagnostic.Create(Rule, syntax.GetLocation(), method.Name, syntax.ToString()));
            return;
        }
    }
}
