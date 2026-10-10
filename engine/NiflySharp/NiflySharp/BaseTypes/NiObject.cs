using NiflySharp.Stream;
using System.Collections.Generic;

namespace NiflySharp
{
    public abstract class NiObject : INiObject
    {
        protected int blockSize = 0;
        protected uint groupId = 0;

        protected NiObject() { }

        /// <summary>
        /// Copy constructor used by generated and hand-written deep-clone code.
        /// Copies state defined on <see cref="NiObject"/> itself; derived
        /// types are responsible for copying their own fields.
        /// </summary>
        protected NiObject(NiObject other)
        {
            blockSize = other.blockSize;
            groupId = other.groupId;
        }

        public virtual IEnumerable<INiRef> References
        {
            get
            {
                return [];
            }
        }

        public virtual IEnumerable<INiRef> Pointers
        {
            get
            {
                return [];
            }
        }

        public virtual IEnumerable<NiRefArray> ReferenceArrays
        {
            get
            {
                return [];
            }
        }

        public virtual IEnumerable<NiStringRef> StringRefs
        {
            get
            {
                return [];
            }
        }

        public void BeforeSync(NiStreamReversible stream) { }
        public void AfterSync(NiStreamReversible stream) { }

        public virtual void Sync(NiStreamReversible stream)
        {
            if (stream.Version.FileVersion >= NiFileVersion.V10_0_0_0 && stream.Version.FileVersion < NiFileVersion.V10_1_0_114)
                stream.Sync(ref groupId);
        }

        public abstract object Clone();
    }
}
