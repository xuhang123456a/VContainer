using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace VContainer.SourceGenerator
{
    /// <summary>
    /// A fully value-equatable snapshot of everything the <see cref="Emitter"/> needs to generate an
    /// injector. This is the unit that flows through the incremental pipeline: as long as the shape
    /// described here (constructor / [Inject] members / registered type) is unchanged, the equality
    /// comparison succeeds and the (relatively expensive) emit + AddSource steps are SKIPPED.
    ///
    /// IMPORTANT: source <see cref="LocationInfo"/>s are stored (so diagnostics can point at the right
    /// place) but are deliberately EXCLUDED from equality. Otherwise inserting an unrelated line above
    /// a type would shift every span below it and force regeneration, which is exactly what we want to
    /// avoid. The only observable trade-off is that a diagnostic on already-broken code may point at a
    /// stale offset until that type's shape actually changes.
    /// </summary>
    sealed class InjectorModel : IEquatable<InjectorModel>
    {
        public string TypeName { get; }
        public string FullTypeName { get; }
        public string Namespace { get; }
        public string SymbolName { get; }

        public bool IsNested { get; }
        public bool IsAbstract { get; }
        public bool IsGenerics { get; }
        public bool ExplicitInjectable { get; }
        public bool IsUnityComponent { get; }

        public bool HasMultipleInjectConstructors { get; }
        public bool ConstructorFound { get; }
        public bool ConstructorCanBeCalled { get; }
        public bool ConstructorIsGeneric { get; }
        public EquatableArray<ParameterModel> ConstructorParameters { get; }

        public EquatableArray<MemberModel> InjectFields { get; }
        public EquatableArray<MemberModel> InjectProperties { get; }
        public EquatableArray<MethodModel> InjectMethods { get; }

        public LocationInfo? Location { get; }

        public string HintName => FullTypeName
            .Replace("global::", "")
            .Replace("<", "_")
            .Replace(">", "_") + "GeneratedInjector.g.cs";

        public InjectorModel(
            string typeName,
            string fullTypeName,
            string @namespace,
            string symbolName,
            bool isNested,
            bool isAbstract,
            bool isGenerics,
            bool explicitInjectable,
            bool isUnityComponent,
            bool hasMultipleInjectConstructors,
            bool constructorFound,
            bool constructorCanBeCalled,
            bool constructorIsGeneric,
            EquatableArray<ParameterModel> constructorParameters,
            EquatableArray<MemberModel> injectFields,
            EquatableArray<MemberModel> injectProperties,
            EquatableArray<MethodModel> injectMethods,
            LocationInfo? location)
        {
            TypeName = typeName;
            FullTypeName = fullTypeName;
            Namespace = @namespace;
            SymbolName = symbolName;
            IsNested = isNested;
            IsAbstract = isAbstract;
            IsGenerics = isGenerics;
            ExplicitInjectable = explicitInjectable;
            IsUnityComponent = isUnityComponent;
            HasMultipleInjectConstructors = hasMultipleInjectConstructors;
            ConstructorFound = constructorFound;
            ConstructorCanBeCalled = constructorCanBeCalled;
            ConstructorIsGeneric = constructorIsGeneric;
            ConstructorParameters = constructorParameters;
            InjectFields = injectFields;
            InjectProperties = injectProperties;
            InjectMethods = injectMethods;
            Location = location;
        }

        public bool Equals(InjectorModel? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return TypeName == other.TypeName &&
                   FullTypeName == other.FullTypeName &&
                   Namespace == other.Namespace &&
                   SymbolName == other.SymbolName &&
                   IsNested == other.IsNested &&
                   IsAbstract == other.IsAbstract &&
                   IsGenerics == other.IsGenerics &&
                   ExplicitInjectable == other.ExplicitInjectable &&
                   IsUnityComponent == other.IsUnityComponent &&
                   HasMultipleInjectConstructors == other.HasMultipleInjectConstructors &&
                   ConstructorFound == other.ConstructorFound &&
                   ConstructorCanBeCalled == other.ConstructorCanBeCalled &&
                   ConstructorIsGeneric == other.ConstructorIsGeneric &&
                   ConstructorParameters.Equals(other.ConstructorParameters) &&
                   InjectFields.Equals(other.InjectFields) &&
                   InjectProperties.Equals(other.InjectProperties) &&
                   InjectMethods.Equals(other.InjectMethods);
            // NOTE: Location intentionally not compared.
        }

        public override bool Equals(object? obj) => Equals(obj as InjectorModel);

        public override int GetHashCode()
        {
            var hash = 17;
            hash = unchecked(hash * 31 + FullTypeName.GetHashCode());
            hash = unchecked(hash * 31 + (ExplicitInjectable ? 1 : 0));
            hash = unchecked(hash * 31 + ConstructorParameters.GetHashCode());
            hash = unchecked(hash * 31 + InjectFields.GetHashCode());
            hash = unchecked(hash * 31 + InjectProperties.GetHashCode());
            hash = unchecked(hash * 31 + InjectMethods.GetHashCode());
            return hash;
        }
    }

    /// <summary>A constructor / method parameter to be resolved. Fully part of equality.</summary>
    sealed class ParameterModel : IEquatable<ParameterModel>
    {
        public string TypeFullName { get; }
        public string Name { get; }
        public string KeyLiteral { get; }

        public ParameterModel(string typeFullName, string name, string keyLiteral)
        {
            TypeFullName = typeFullName;
            Name = name;
            KeyLiteral = keyLiteral;
        }

        public bool Equals(ParameterModel? other) =>
            other is not null &&
            TypeFullName == other.TypeFullName &&
            Name == other.Name &&
            KeyLiteral == other.KeyLiteral;

        public override bool Equals(object? obj) => Equals(obj as ParameterModel);

        public override int GetHashCode()
        {
            var hash = 17;
            hash = unchecked(hash * 31 + TypeFullName.GetHashCode());
            hash = unchecked(hash * 31 + Name.GetHashCode());
            hash = unchecked(hash * 31 + KeyLiteral.GetHashCode());
            return hash;
        }
    }

    /// <summary>An [Inject] field or property. <see cref="Location"/> is excluded from equality.</summary>
    sealed class MemberModel : IEquatable<MemberModel>
    {
        public string TypeFullName { get; }
        public string Name { get; }
        public string KeyLiteral { get; }
        public bool CanBeSet { get; }
        public bool IsTypeParameter { get; }
        public LocationInfo? Location { get; }

        public MemberModel(string typeFullName, string name, string keyLiteral, bool canBeSet, bool isTypeParameter, LocationInfo? location)
        {
            TypeFullName = typeFullName;
            Name = name;
            KeyLiteral = keyLiteral;
            CanBeSet = canBeSet;
            IsTypeParameter = isTypeParameter;
            Location = location;
        }

        public bool Equals(MemberModel? other) =>
            other is not null &&
            TypeFullName == other.TypeFullName &&
            Name == other.Name &&
            KeyLiteral == other.KeyLiteral &&
            CanBeSet == other.CanBeSet &&
            IsTypeParameter == other.IsTypeParameter;
            // NOTE: Location intentionally not compared.

        public override bool Equals(object? obj) => Equals(obj as MemberModel);

        public override int GetHashCode()
        {
            var hash = 17;
            hash = unchecked(hash * 31 + TypeFullName.GetHashCode());
            hash = unchecked(hash * 31 + Name.GetHashCode());
            hash = unchecked(hash * 31 + KeyLiteral.GetHashCode());
            hash = unchecked(hash * 31 + (CanBeSet ? 1 : 0));
            hash = unchecked(hash * 31 + (IsTypeParameter ? 1 : 0));
            return hash;
        }
    }

    /// <summary>An [Inject] method. <see cref="Location"/> is excluded from equality.</summary>
    sealed class MethodModel : IEquatable<MethodModel>
    {
        public string Name { get; }
        public bool IsStatic { get; }
        public string ContainingTypeFullName { get; }
        public bool ReturnsVoid { get; }
        public bool CanBeCalled { get; }
        public bool IsGeneric { get; }
        public EquatableArray<ParameterModel> Parameters { get; }
        public LocationInfo? Location { get; }

        public MethodModel(
            string name,
            bool isStatic,
            string containingTypeFullName,
            bool returnsVoid,
            bool canBeCalled,
            bool isGeneric,
            EquatableArray<ParameterModel> parameters,
            LocationInfo? location)
        {
            Name = name;
            IsStatic = isStatic;
            ContainingTypeFullName = containingTypeFullName;
            ReturnsVoid = returnsVoid;
            CanBeCalled = canBeCalled;
            IsGeneric = isGeneric;
            Parameters = parameters;
            Location = location;
        }

        public bool Equals(MethodModel? other) =>
            other is not null &&
            Name == other.Name &&
            IsStatic == other.IsStatic &&
            ContainingTypeFullName == other.ContainingTypeFullName &&
            ReturnsVoid == other.ReturnsVoid &&
            CanBeCalled == other.CanBeCalled &&
            IsGeneric == other.IsGeneric &&
            Parameters.Equals(other.Parameters);
            // NOTE: Location intentionally not compared.

        public override bool Equals(object? obj) => Equals(obj as MethodModel);

        public override int GetHashCode()
        {
            var hash = 17;
            hash = unchecked(hash * 31 + Name.GetHashCode());
            hash = unchecked(hash * 31 + (IsStatic ? 1 : 0));
            hash = unchecked(hash * 31 + (ReturnsVoid ? 1 : 0));
            hash = unchecked(hash * 31 + Parameters.GetHashCode());
            return hash;
        }
    }

    static class InjectorModelBuilder
    {
        /// <summary>
        /// Extracts a value-equatable <see cref="InjectorModel"/> from the symbol-bound
        /// <see cref="TypeMeta"/>. All symbol/syntax access happens here, never downstream.
        /// </summary>
        public static InjectorModel Build(TypeMeta typeMeta, ReferenceSymbols references)
        {
            // Constructor selection (mirrors Emitter.TryEmitCreateInstanceMethod).
            var hasMultiple = typeMeta.ExplicitInjectConstructors.Count > 1;
            IMethodSymbol? constructor = null;
            if (!hasMultiple)
            {
                constructor = typeMeta.ExplicitInjectConstructors.Count == 1
                    ? typeMeta.ExplicitInjectConstructors[0]
                    : typeMeta.ExplicitConstructors.OrderByDescending(ctor => ctor.Parameters.Length).FirstOrDefault();

                constructor ??= typeMeta.Symbol.InstanceConstructors
                    .FirstOrDefault(x => x.IsImplicitlyDeclared && x.Parameters.Length == 0);
            }

            var constructorParameters = constructor is null
                ? EquatableArray<ParameterModel>.Empty
                : new EquatableArray<ParameterModel>(constructor.Parameters.Select(p => BuildParameter(p, references)).ToArray());

            var injectFields = new EquatableArray<MemberModel>(typeMeta.InjectFields
                .Select(f => new MemberModel(
                    EmitTypeName(f.Type),
                    f.Name,
                    KeyLiteralOf(f, references),
                    f.CanBeCallFromInternal(),
                    f.Type is ITypeParameterSymbol,
                    LocationInfo.CreateFrom(f.Locations.FirstOrDefault())))
                .ToArray());

            var injectProperties = new EquatableArray<MemberModel>(typeMeta.InjectProperties
                .Select(p => new MemberModel(
                    EmitTypeName(p.Type),
                    p.Name,
                    KeyLiteralOf(p, references),
                    p.SetMethod != null && !p.SetMethod.IsInitOnly && p.SetMethod.CanBeCallFromInternal(),
                    p.Type is ITypeParameterSymbol,
                    LocationInfo.CreateFrom(p.Locations.FirstOrDefault())))
                .ToArray());

            var injectMethods = new EquatableArray<MethodModel>(typeMeta.InjectMethods
                .Select(m => new MethodModel(
                    m.Name,
                    m.IsStatic,
                    EmitTypeName(m.ContainingType),
                    m.ReturnsVoid,
                    m.CanBeCallFromInternal(),
                    m.Arity > 0,
                    new EquatableArray<ParameterModel>(m.Parameters.Select(p => BuildParameter(p, references)).ToArray()),
                    LocationInfo.CreateFrom(m.Locations.FirstOrDefault())))
                .ToArray());

            var ns = typeMeta.Symbol.ContainingNamespace;
            var isUnityComponent = references.UnityEngineComponent != null &&
                                   typeMeta.InheritsFrom(references.UnityEngineComponent);

            return new InjectorModel(
                typeMeta.TypeName,
                typeMeta.FullTypeName,
                ns.IsGlobalNamespace ? "" : ns.ToString(),
                typeMeta.Symbol.Name,
                typeMeta.IsNested(),
                typeMeta.Symbol.IsAbstract,
                typeMeta.IsGenerics,
                typeMeta.ExplicitInjectable,
                isUnityComponent,
                hasMultiple,
                constructor != null,
                constructor?.CanBeCallFromInternal() ?? false,
                (constructor?.Arity ?? 0) > 0,
                constructorParameters,
                injectFields,
                injectProperties,
                injectMethods,
                LocationInfo.CreateFrom(typeMeta.GetLocation()));
        }

        static ParameterModel BuildParameter(IParameterSymbol parameter, ReferenceSymbols references) =>
            new(EmitTypeName(parameter.Type), parameter.Name, KeyLiteralOf(parameter, references));

        static string EmitTypeName(ITypeSymbol type) =>
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        static string KeyLiteralOf(ISymbol symbol, ReferenceSymbols references) =>
            EmitKeyValue(ExtractKeyFromAttribute(symbol, references));

        static object? ExtractKeyFromAttribute(ISymbol symbol, ReferenceSymbols references)
        {
            if (references.VContainerKeyAttribute == null)
            {
                return null;
            }

            foreach (var attribute in symbol.GetAttributes())
            {
                if (attribute.AttributeClass == null)
                    continue;

                var isKeyAttribute = SymbolEqualityComparer.Default.Equals(
                    attribute.AttributeClass,
                    references.VContainerKeyAttribute);

                if (!isKeyAttribute || attribute.ConstructorArguments.Length <= 0)
                {
                    continue;
                }

                var constructorArg = attribute.ConstructorArguments[0];

                return constructorArg.Kind == TypedConstantKind.Enum
                    ? constructorArg
                    : constructorArg.Value;
            }

            return null;
        }

        static string EmitKeyValue(object? key)
        {
            return key switch
            {
                null => "null",
                string str => $"\"{str}\"",
                bool b => b ? bool.TrueString : bool.FalseString,
                TypedConstant { Kind: TypedConstantKind.Enum, Type: not null } tc => EnumToStringRepresentation(tc),
                TypedConstant { Kind: TypedConstantKind.Primitive, Value: string strVal } => EmitKeyValue(strVal),
                TypedConstant { Value: not null } tc => tc.Value.ToString(),
                _ => key.ToString() ?? "null"
            };
        }

        static string EnumToStringRepresentation(TypedConstant tc) =>
            $"({tc.Type!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){tc.Value}";
    }
}
