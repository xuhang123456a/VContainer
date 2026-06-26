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
    /// <summary>Tracking name of the emit (source-string assembly) step, used by incremental-cache tests.</summary>
    public const string EmitStepName = "VContainerInjectorEmit";

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

        // STAGE 1 (semantic analysis): syntax + symbols -> a small, fully value-equatable InjectorModel.
        // Because the model is equatable AND excludes source locations from equality, an edit that does
        // not change the injection shape (constructor / [Inject] members / registered type) produces an
        // equal model.
        //
        // STAGE 2 (emit) is a SEPARATE `Select`, so when STAGE 1's model is unchanged the emit (source
        // string assembly) is served from cache and skipped entirely — not just the final AddSource.
        var typeDeclarationModels = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => IsTypeDeclarationCandidate(node),
                static (ctx, cancellation) => TransformTypeDeclaration(ctx, cancellation))
            .Combine(vcontainerReferenceValueProvider)
            .Where(static tuple => tuple.Right && tuple.Left is not null)
            .Select(static (tuple, _) => tuple.Left!);

        var typeDeclarationSources = typeDeclarationModels
            .Select(static (model, _) => EmitToGeneratedSource(model))
            .WithTrackingName(EmitStepName)
            .Collect();

        var registerInvocationModels = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => Analyzer.IsRegisterSyntaxCandidate(node),
                static (ctx, cancellation) => TransformRegisterInvocation(ctx, cancellation))
            .Combine(vcontainerReferenceValueProvider)
            .Where(static tuple => tuple.Right)
            .Select(static (tuple, _) => tuple.Left);

        var registerInvocationSources = registerInvocationModels
            .Select(static (models, _) =>
                new EquatableArray<GeneratedSource>(models.Select(EmitToGeneratedSource).ToArray()))
            .WithTrackingName(EmitStepName)
            .Collect();

        // The final step consumes ONLY value-equatable data (no Compilation / SemanticModel / symbols),
        // so when nothing relevant changed the collected arrays compare equal and this is skipped.
        context.RegisterSourceOutput(
            typeDeclarationSources.Combine(registerInvocationSources),
            static (sourceProductionContext, tuple) =>
            {
                var emitted = new HashSet<string>();
                foreach (var generated in tuple.Left)
                {
                    EmitOne(sourceProductionContext, generated, emitted);
                }
                foreach (var group in tuple.Right)
                {
                    foreach (var generated in group)
                    {
                        EmitOne(sourceProductionContext, generated, emitted);
                    }
                }
            });
    }

    static void EmitOne(SourceProductionContext context, GeneratedSource generated, HashSet<string> emitted)
    {
        // Dedupe by hint name so a type that is both explicitly injectable and registered
        // (or registered from multiple places) is only emitted/reported once.
        if (!emitted.Add(generated.HintName))
        {
            return;
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

    static InjectorModel? TransformTypeDeclaration(GeneratorSyntaxContext ctx, CancellationToken cancellation)
    {
        var references = ReferenceSymbols.Create(ctx.SemanticModel.Compilation);
        if (references is null)
        {
            return null;
        }

        var candidate = new TypeDeclarationCandidate((TypeDeclarationSyntax)ctx.Node, ctx.SemanticModel);
        var typeMeta = candidate.Analyze(references, cancellation);

        // Only explicitly-injectable types get an injector from a bare declaration.
        if (typeMeta is null || !typeMeta.ExplicitInjectable)
        {
            return null;
        }
        return InjectorModelBuilder.Build(typeMeta, references);
    }

    static EquatableArray<InjectorModel> TransformRegisterInvocation(GeneratorSyntaxContext ctx, CancellationToken cancellation)
    {
        var references = ReferenceSymbols.Create(ctx.SemanticModel.Compilation);
        if (references is null)
        {
            return EquatableArray<InjectorModel>.Empty;
        }

        var candidate = new RegisterInvocationCandidate((InvocationExpressionSyntax)ctx.Node, ctx.SemanticModel);
        var models = new List<InjectorModel>();
        foreach (var typeMeta in candidate.Analyze(references, cancellation))
        {
            models.Add(InjectorModelBuilder.Build(typeMeta, references));
        }
        return models.Count == 0
            ? EquatableArray<InjectorModel>.Empty
            : new EquatableArray<InjectorModel>(models.ToArray());
    }

    static GeneratedSource EmitToGeneratedSource(InjectorModel model)
    {
        var diagnostics = new List<DiagnosticInfo>();
        var codeWriter = new CodeWriter();
        var ok = Emitter.TryEmitGeneratedInjector(model, codeWriter, diagnostics);

        return new GeneratedSource(
            model.HintName,
            ok ? codeWriter.ToString() : null,
            new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
    }
}
