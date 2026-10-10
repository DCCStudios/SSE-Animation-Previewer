using System.Collections.Generic;

namespace NiflySharp
{
    public class NiBlockPtrArray<T> : NiBlockRefArray<T> where T : NiObject
    {
        public NiBlockPtrArray()
        {
        }

        // Don't remove this constructor (required for reflection)
        public NiBlockPtrArray(List<NiBlockRef<T>> refs)
        {
            _refs = refs;
        }

        public IEnumerable<INiRef> Pointers
        {
            get
            {
                return _refs;
            }
        }

        public override NiBlockPtrArray<T> Clone()
        {
            var clone = new NiBlockPtrArray<T>
            {
                KeepEmptyRefs = KeepEmptyRefs
            };
            clone.Resize(Count);

            for (int i = 0; i < clone.Count; i++)
                clone.SetBlockRef(i, GetBlockRef(i));

            return clone;
        }
    }
}
