using System;
using System.Collections;
using System.Collections.Generic;

namespace VContainer.SourceGenerator
{
    /// <summary>
    /// An immutable array wrapper that provides value (structural) equality so that it can be
    /// safely used as a part of an incremental generator pipeline state.
    /// <see cref="System.Collections.Immutable.ImmutableArray{T}"/> does NOT implement structural
    /// equality, which breaks the caching/"generate skip" of <c>IIncrementalGenerator</c>.
    /// </summary>
    readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
        where T : IEquatable<T>
    {
        public static readonly EquatableArray<T> Empty = new(Array.Empty<T>());

        readonly T[]? array;

        public EquatableArray(T[] array) => this.array = array;

        public int Count => array?.Length ?? 0;

        public T this[int index] => array![index];

        public bool Equals(EquatableArray<T> other)
        {
            var self = array ?? Array.Empty<T>();
            var others = other.array ?? Array.Empty<T>();
            if (self.Length != others.Length)
            {
                return false;
            }
            for (var i = 0; i < self.Length; i++)
            {
                if (!self[i].Equals(others[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            if (array is null)
            {
                return 0;
            }
            var hash = 17;
            foreach (var item in array)
            {
                hash = unchecked(hash * 31 + (item?.GetHashCode() ?? 0));
            }
            return hash;
        }

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(array ?? Array.Empty<T>())).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
