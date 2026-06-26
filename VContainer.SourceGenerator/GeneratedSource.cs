using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace VContainer.SourceGenerator
{
    /// <summary>
    /// A fully value-equatable description of one source file to be added (and any diagnostics to
    /// report). This is what flows through the incremental pipeline into <c>RegisterSourceOutput</c>
    /// so that unchanged inputs produce equal payloads and the final generation step is skipped.
    /// </summary>
    sealed class GeneratedSource : IEquatable<GeneratedSource>
    {
        public string HintName { get; }

        /// <summary>The generated source text, or null when emission was aborted (diagnostics only).</summary>
        public string? Source { get; }

        public EquatableArray<DiagnosticInfo> Diagnostics { get; }

        public GeneratedSource(string hintName, string? source, EquatableArray<DiagnosticInfo> diagnostics)
        {
            HintName = hintName;
            Source = source;
            Diagnostics = diagnostics;
        }

        public bool Equals(GeneratedSource? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return HintName == other.HintName &&
                   Source == other.Source &&
                   Diagnostics.Equals(other.Diagnostics);
        }

        public override bool Equals(object? obj) => Equals(obj as GeneratedSource);

        public override int GetHashCode()
        {
            var hash = 17;
            hash = unchecked(hash * 31 + HintName.GetHashCode());
            hash = unchecked(hash * 31 + (Source?.GetHashCode() ?? 0));
            hash = unchecked(hash * 31 + Diagnostics.GetHashCode());
            return hash;
        }
    }

    /// <summary>
    /// Value-equatable replacement for <see cref="Location"/>. A raw <see cref="Location"/> holds a
    /// reference to the <see cref="SyntaxTree"/> and therefore is not safe to cache.
    /// </summary>
    sealed class LocationInfo : IEquatable<LocationInfo>
    {
        public string FilePath { get; }
        public TextSpan TextSpan { get; }
        public LinePositionSpan LineSpan { get; }

        LocationInfo(string filePath, TextSpan textSpan, LinePositionSpan lineSpan)
        {
            FilePath = filePath;
            TextSpan = textSpan;
            LineSpan = lineSpan;
        }

        public Location ToLocation() => Location.Create(FilePath, TextSpan, LineSpan);

        public static LocationInfo? CreateFrom(Location? location)
        {
            if (location?.SourceTree is null)
            {
                return null;
            }
            return new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
        }

        public bool Equals(LocationInfo? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return FilePath == other.FilePath &&
                   TextSpan.Equals(other.TextSpan) &&
                   LineSpan.Equals(other.LineSpan);
        }

        public override bool Equals(object? obj) => Equals(obj as LocationInfo);

        public override int GetHashCode()
        {
            var hash = 17;
            hash = unchecked(hash * 31 + FilePath.GetHashCode());
            hash = unchecked(hash * 31 + TextSpan.GetHashCode());
            hash = unchecked(hash * 31 + LineSpan.GetHashCode());
            return hash;
        }
    }

    /// <summary>
    /// Value-equatable replacement for <see cref="Diagnostic"/>. <see cref="DiagnosticDescriptor"/>
    /// instances are static singletons (reference equatable), so caching them is safe.
    /// </summary>
    sealed class DiagnosticInfo : IEquatable<DiagnosticInfo>
    {
        public DiagnosticDescriptor Descriptor { get; }
        public LocationInfo? Location { get; }
        public EquatableArray<string> MessageArgs { get; }

        public DiagnosticInfo(DiagnosticDescriptor descriptor, Location? location, params string[] messageArgs)
            : this(descriptor, LocationInfo.CreateFrom(location), messageArgs)
        {
        }

        public DiagnosticInfo(DiagnosticDescriptor descriptor, LocationInfo? location, params string[] messageArgs)
        {
            Descriptor = descriptor;
            Location = location;
            MessageArgs = new EquatableArray<string>(messageArgs);
        }

        public Diagnostic ToDiagnostic()
        {
            var location = Location?.ToLocation() ?? Microsoft.CodeAnalysis.Location.None;
            var args = new object[MessageArgs.Count];
            for (var i = 0; i < args.Length; i++)
            {
                args[i] = MessageArgs[i];
            }
            return Diagnostic.Create(Descriptor, location, args);
        }

        public bool Equals(DiagnosticInfo? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return ReferenceEquals(Descriptor, other.Descriptor) &&
                   Equals(Location, other.Location) &&
                   MessageArgs.Equals(other.MessageArgs);
        }

        public override bool Equals(object? obj) => Equals(obj as DiagnosticInfo);

        public override int GetHashCode()
        {
            var hash = 17;
            hash = unchecked(hash * 31 + Descriptor.GetHashCode());
            hash = unchecked(hash * 31 + (Location?.GetHashCode() ?? 0));
            hash = unchecked(hash * 31 + MessageArgs.GetHashCode());
            return hash;
        }
    }
}
