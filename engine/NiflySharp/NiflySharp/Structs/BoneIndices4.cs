using System;
using System.Runtime.CompilerServices;

namespace NiflySharp.Structs
{
    /// <summary>
    /// Fixed-size inline array of four <see cref="byte"/> bone indices stored directly inside
    /// <see cref="BSVertexData"/> / <see cref="BSVertexDataSSE"/>. Having this as a value type
    /// (instead of a managed <c>byte[]</c>) turns the vertex structs into pure value types that
    /// can be copied wholesale via plain struct assignment, <see cref="Array.Copy(Array, Array, int)"/>
    /// or <c>new List&lt;BSVertexData&gt;(other)</c> without any per-element deep-clone.
    /// </summary>
    /// <remarks>
    /// Access elements via the inline-array indexer (<c>indices[0]</c> .. <c>indices[3]</c>) or
    /// implicit conversion to <see cref="Span{T}"/> / <see cref="ReadOnlySpan{T}"/>.
    /// </remarks>
    [InlineArray(Length)]
    public struct BoneIndices4
    {
        /// <summary>
        /// Fixed length of the inline array.
        /// </summary>
        public const int Length = 4;

        private byte _element0;
    }
}
