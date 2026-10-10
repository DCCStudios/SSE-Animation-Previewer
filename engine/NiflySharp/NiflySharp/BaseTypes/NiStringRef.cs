using NiflySharp.Stream;
using System.Text;

namespace NiflySharp
{
    public class NiStringRef : INiStreamable
    {
        public const int NPOS = -1;

        protected int _index = NPOS; // Temporary index storage for load/save

        protected string _str = string.Empty;

        public int Index { get => _index; set => _index = value; }

        public int Length { get => _str.Length; }

        public string String { get => _str; set => _str = value; }

        public NiStringRef()
        {
        }

        public NiStringRef(string str)
        {
            _str = str;
        }

        public void Clear()
        {
            Index = NPOS;
            _str = string.Empty;
        }

        public void Sync(NiStreamReversible stream)
        {
            if (stream.Version.FileVersion < NiFileVersion.V20_1_0_3)
            {
                const int maxLength = 2048;

                // Clamp before writing the length prefix so prefix and payload agree
                int sz = _str.Length > maxLength ? maxLength : _str.Length;
                stream.Sync(ref sz);

                if (stream.CurrentMode == NiStreamReversible.Mode.Read && (sz < 0 || sz > NifConstants.ArraySizeLimit))
                    throw new System.IO.InvalidDataException("Read string length is out of range!");

                var buf = new byte[sz];

                if (stream.CurrentMode == NiStreamReversible.Mode.Write)
                {
                    if (_str.Length > maxLength)
                        buf = Encoding.Latin1.GetBytes(_str[..maxLength]);
                    else
                        buf = Encoding.Latin1.GetBytes(_str);
                }

                // On read, consume exactly 'sz' bytes so the stream stays aligned
                stream.Sync(ref buf);

                if (stream.CurrentMode == NiStreamReversible.Mode.Read)
                    _str = Encoding.Latin1.GetString(buf);
            }
            else
                stream.Sync(ref _index);
        }

        public override bool Equals(object obj)
        {
            return obj is NiStringRef r && String == r.String;
        }

        public override int GetHashCode()
        {
            return _str.GetHashCode();
        }

        public override string ToString() {
            return _str;
        }

        /// <summary>
        /// Creates a shallow copy of this string reference.
        /// The string content and index are value types (string is immutable),
        /// so a member-wise copy is sufficient for a deep clone.
        /// </summary>
        public NiStringRef Clone()
        {
            return new NiStringRef(_str) { Index = _index };
        }
    }
}
