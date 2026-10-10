using System;
using System.Runtime.CompilerServices;

namespace NiflySharp.Structs
{
    /// <summary>
    /// Fixed-size inline array of four <see cref="Half"/> bone weights stored directly inside
    /// <see cref="BSVertexData"/> / <see cref="BSVertexDataSSE"/>. Having this as a value type
    /// (instead of a managed <c>Half[]</c>) turns the vertex structs into pure value types that
    /// can be copied wholesale via plain struct assignment, <see cref="Array.Copy(Array, Array, int)"/>
    /// or <c>new List&lt;BSVertexData&gt;(other)</c> without any per-element deep-clone.
    /// </summary>
    /// <remarks>
    /// Access elements via the inline-array indexer (<c>weights[0]</c> .. <c>weights[3]</c>) or
    /// implicit conversion to <see cref="Span{T}"/> / <see cref="ReadOnlySpan{T}"/>.
    /// </remarks>
    [InlineArray(Length)]
    public struct BoneWeights4
    {
        /// <summary>
        /// Fixed length of the inline array.
        /// </summary>
        public const int Length = 4;

        private Half _element0;
    }
}
