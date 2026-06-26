using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace VContainer.SourceGenerator;

[Generator]
public class VContainerIncrementalSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Assembly filter. Produces an equatable `bool`, so combining it into the per-node pipeline
        // does not invalidate node-level caching (it only changes when the bool itself flips).
        var vcontainerReferenceValueProvider = context.CompilationProvider
            .Select((compilation, cancellation) =>
            {
                if (compilation.AssemblyName?.StartsWith("VContainer") == true &&
                    !compilation.AssemblyName.Contains("Test"))
                {
                    return false;
                }

                if (compilation.AssemblyName?.StartsWith("UnityEngine.") == true ||
                    compilation.AssemblyName?.StartsWith("Unity.") == true)
                {
                    return false;
                }

                foreach (var referencedAssemblyName in compilation.ReferencedAssemblyNames)
                {
                    if (referencedAssemblyName.Name.StartsWith("VContainer"))
                        return true;
                }
                return false;
            });

        // Find types by explicit [Inject]. The transform does the full semantic analysis AND emission
        // up-front, so the payload that flows downstream is fully value-equatable (strings only).
        // This is what lets the incremental engine reuse cached results / skip generation.
        var typeDeclarationSources = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => IsTypeDeclarationCandidate(node),
                static (ctx, cancellation) => TransformTypeDeclaration(ctx, cancellation))
            .Combine(vcontainerReferenceValueProvider)
            .Where(static tuple => tuple.Right)
            .Select(static (tuple, _) => tuple.Left);

        // Find types based on Register* / Add<T>() invocations.
        var registerInvocationSources = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => Analyzer.IsRegisterSyntaxCandidate(node),
                static (ctx, cancellation) => TransformRegisterInvocation(ctx, cancellation))
            .Combine(vcontainerReferenceValueProvider)
            .Where(static tuple => tuple.Right)
            .Select(static (tuple, _) => tuple.Left);

        // The final step consumes ONLY value-equatable data (no Compilation / SemanticModel / symbols),
        // so when nothing relevant changed the collected arrays compare equal and this is skipped.
        context.RegisterSourceOutput(
            typeDeclarationSources.Collect().Combine(registerInvocationSources.Collect()),
            static (sourceProductionContext, tuple) =>
            {
                var emitted = new HashSet<string>();
                Emit(sourceProductionContext, tuple.Left, emitted);
                Emit(sourceProductionContext, tuple.Right, emitted);
            });
    }

    static void Emit(
        SourceProductionContext context,
        System.Collections.Immutable.ImmutableArray<EquatableArray<GeneratedSource>> groups,
        HashSet<string> emitted)
    {
        foreach (var group in groups)
        {
            foreach (var generated in group)
            {
                // Dedupe by hint name so a type that is both explicitly injectable and registered
                // (or registered from multiple places) is only emitted/reported once.
                if (!emitted.Add(generated.HintName))
                {
                    continue;
                }

                foreach (var diagnostic in generated.Diagnostics)
                {
                    context.ReportDiagnostic(diagnostic.ToDiagnostic());
                }

                if (generated.Source != null)
                {
                    context.AddSource(generated.HintName, generated.Source);
                }
            }
        }
    }

    static bool IsTypeDeclarationCandidate(SyntaxNode node)
    {
        if (!node.IsKind(SyntaxKind.ClassDeclaration)) return false;
        if (node is not ClassDeclarationSyntax syntax) return false;

        if (syntax.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.AbstractKeyword) ||
                                             modifier.IsKind(SyntaxKind.StaticKeyword)))
        {
            return false;
        }
        return true;
    }

    static EquatableArray<GeneratedSource> TransformTypeDeclaration(
        GeneratorSyntaxContext ctx,
        CancellationToken cancellation)
    {
        var references = ReferenceSymbols.Create(ctx.SemanticModel.Compilation);
        if (references is null)
        {
            return EquatableArray<GeneratedSource>.Empty;
        }

        var candidate = new TypeDeclarationCandidate((TypeDeclarationSyntax)ctx.Node, ctx.SemanticModel);
        var typeMeta = candidate.Analyze(references, cancellation);

        // Only explicitly-injectable types get an injector from a bare declaration.
        if (typeMeta is null || !typeMeta.ExplicitInjectable)
        {
            return EquatableArray<GeneratedSource>.Empty;
        }

        return new EquatableArray<GeneratedSource>(new[] { EmitToGeneratedSource(typeMeta, references) });
    }

    static EquatableArray<GeneratedSource> TransformRegisterInvocation(
        GeneratorSyntaxContext ctx,
        CancellationToken cancellation)
    {
        var references = ReferenceSymbols.Create(ctx.SemanticModel.Compilation);
        if (references is null)
        {
            return EquatableArray<GeneratedSource>.Empty;
        }

        var candidate = new RegisterInvocationCandidate((InvocationExpressionSyntax)ctx.Node, ctx.SemanticModel);
        var results = new List<GeneratedSource>();
        foreach (var typeMeta in candidate.Analyze(references, cancellation))
        {
            results.Add(EmitToGeneratedSource(typeMeta, references));
        }
        return results.Count == 0
            ? EquatableArray<GeneratedSource>.Empty
            : new EquatableArray<GeneratedSource>(results.ToArray());
    }

    static GeneratedSource EmitToGeneratedSource(TypeMeta typeMeta, ReferenceSymbols references)
    {
        var hintName = typeMeta.FullTypeName
            .Replace("global::", "")
            .Replace("<", "_")
            .Replace(">", "_") + "GeneratedInjector.g.cs";

        var diagnostics = new List<DiagnosticInfo>();
        var codeWriter = new CodeWriter();
        var ok = Emitter.TryEmitGeneratedInjector(typeMeta, codeWriter, references, diagnostics);

        return new GeneratedSource(
            hintName,
            ok ? codeWriter.ToString() : null,
            new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
    }
}
