using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Sdcb.SimdCodecs.Analyzers;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// SDC0001 (<see cref="UnsafeOffsetAnalyzer"/>): every non-constant <c>int</c> offset of
/// <c>Unsafe.Add</c> / <c>Unsafe.Subtract</c> is an error, every <c>nint</c> / <c>nuint</c> /
/// constant offset is not. The rows mirror the shapes probed on Android (dotnet/runtime#138452).
/// </summary>
public sealed class UnsafeOffsetAnalyzerTests
{
    private static async Task<string[]> Flagged(string body)
    {
        var source = $$"""
            using System.Runtime.CompilerServices;
            static unsafe class C
            {
                static uint[] U = new uint[64];
                static int F = -3;
                static void M(int a, int b, nint n, nuint u, byte by, uint* p)
                {
                    ref uint r = ref U[32];
                    {{body}}
                }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("probe", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        var diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new UnsafeOffsetAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        diagnostics.ShouldAllBe(d => d.Id == UnsafeOffsetAnalyzer.Id && d.Severity == DiagnosticSeverity.Error);
        return diagnostics.Select(d => d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan)).ToArray();
    }

    [Theory]
    [InlineData("_ = Unsafe.Add(ref r, a - b);", "a - b")]
    [InlineData("_ = Unsafe.Add(ref r, -a);", "-a")]
    [InlineData("_ = Unsafe.Add(ref r, a >> 1);", "a >> 1")]
    [InlineData("_ = Unsafe.Add(ref r, a);", "a")] // a parameter: computed once inlined
    [InlineData("_ = Unsafe.Add(ref r, F);", "F")]
    [InlineData("_ = Unsafe.Add(ref r, by);", "by")] // byte widens to the int overload
    [InlineData("_ = Unsafe.Subtract(ref r, a + b);", "a + b")]
    [InlineData("var d = a - b; _ = Unsafe.Add(ref r, d);", "d")]
    [InlineData("_ = Unsafe.Add<uint>(p, a);", "a")] // the void* overload
    [InlineData("_ = Unsafe.Add(ref r, (int)n);", "(int)n")]
    public async Task Int_offset_is_an_error(string body, string offset) =>
        (await Flagged(body)).ShouldBe([offset]);

    [Theory]
    [InlineData("_ = Unsafe.Add(ref r, (nint)(a - b));")]
    [InlineData("_ = Unsafe.Add(ref r, (nint)a - b);")]
    [InlineData("_ = Unsafe.Add(ref r, n);")]
    [InlineData("_ = Unsafe.Add(ref r, u);")]
    [InlineData("_ = Unsafe.Add(ref r, (nuint)a + 4);")]
    [InlineData("_ = Unsafe.Add(ref r, -3);")]
    [InlineData("_ = Unsafe.Add(ref r, 2 * 8 - 1);")]
    [InlineData("const int K = 7; _ = Unsafe.Subtract(ref r, K);")]
    [InlineData("_ = Unsafe.AddByteOffset(ref r, a - b);")] // takes nint: conv.i sign-extends
    [InlineData("_ = p + (a - b); _ = U[32 + (a - b)];")]
    public async Task Nint_or_constant_offset_is_fine(string body) =>
        (await Flagged(body)).ShouldBeEmpty();

    [Fact]
    public async Task Nested_calls_report_each_offset() =>
        (await Flagged("_ = Unsafe.Add(ref r, (int)Unsafe.Add(ref r, a));")).ShouldBe(["a", "(int)Unsafe.Add(ref r, a)"], ignoreOrder: true);
}
