using System;
using System.Collections.Generic;
using System.Linq;

namespace NiflySharp.Extensions
{
    public static class ListExtensions
    {
        public static List<T> Resize<T>(this List<T> list, int size, T element = default)
        {
            list ??= [];

            int count = list.Count;

            if (size < count)
            {
                list.RemoveRange(size, count - size);
            }
            else if (size > count)
            {
                if (size > list.Capacity)   // Optimization
                    list.Capacity = size;

                list.AddRange(Enumerable.Repeat(element, size - count));
            }

            return list;
        }

        public static IEnumerable<List<T>> SplitByFixedSize<T>(this List<T> list, int nSize)
        {
            if (nSize <= 0)
                yield break;

            for (int i = 0; i < list.Count; i += nSize)
            {
                yield return list.GetRange(i, Math.Min(nSize, list.Count - i));
            }
        }

        public static IEnumerable<List<T>> SplitByFlexSize<T, SizeT>(this List<T> list, List<SizeT> sizeList)
        {
            int pos = 0;

            foreach (var sizeValue in sizeList)
            {
                if (pos >= list.Count)
                    yield break;

                int size = Convert.ToInt32(sizeValue);
                if (size <= 0)
                {
                    yield return [];
                    continue;
                }

                var range = list.GetRange(pos, Math.Min(size, list.Count - pos));
                pos += size;
                yield return range;
            }
        }
    }
}
