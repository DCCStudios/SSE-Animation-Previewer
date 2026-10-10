using NiflySharp.Stream;
using System;
using System.Text;

namespace NiflySharp
{
    public class NiString1 : NiString, INiStreamable
    {
        public const int Size = 1;

        public NiString1() : base()
        {
        }

        public NiString1(string content, bool nullOutput = false) : base(content, nullOutput)
        {
        }

        public void Sync(NiStreamReversible stream)
        {
            Sync(stream, Size);
        }
    }

    public class NiString2 : NiString, INiStreamable
    {
        public const int Size = 2;

        public NiString2() : base()
        {
        }

        public NiString2(string content, bool nullOutput = false) : base(content, nullOutput)
        {
        }

        public void Sync(NiStreamReversible stream)
        {
            Sync(stream, Size);
        }
    }

    public class NiString4 : NiString, INiStreamable
    {
        public const int Size = 4;

        public NiString4() : base()
        {
        }

        public NiString4(string content, bool nullOutput = false) : base(content, nullOutput)
        {
        }

        public void Sync(NiStreamReversible stream)
        {
            Sync(stream, Size);
        }
    }

    public class NiString : ICloneable
    {
        public string Content { get; set; }

        /// <summary>
        /// Append a null byte when writing the string
        /// </summary>
        public bool NullOutput { get; set; }

        public NiString()
        {
        }

        public NiString(string content, bool nullOutput = false)
        {
            Content = content;
            NullOutput = nullOutput;
        }

        public void Read(NiStreamReader stream, int szSize)
        {
            byte[] bytes;
            if (szSize == 1)
            {
                byte smSize = stream.Reader.ReadByte();
                bytes = stream.Reader.ReadBytes(smSize);
            }
            else if (szSize == 2)
            {
                ushort medSize = stream.Reader.ReadUInt16();
                bytes = stream.Reader.ReadBytes(medSize);
            }
            else
            {
                int bigSize = stream.Reader.ReadInt32();
                if (bigSize < 0 || bigSize > NifConstants.ArraySizeLimit)
                    throw new Exception("Read string size is out of range!");

                bytes = stream.Reader.ReadBytes(bigSize);
            }

            Content = Encoding.Latin1.GetString(bytes).TrimEnd('\0');
        }

        public void Write(NiStreamWriter stream, int szSize)
        {
            string content = Content ?? string.Empty;

            // Clamp the content so the size prefix (including the optional null
            // terminator) always fits into the prefix type and matches the payload.
            int maxLength = szSize switch
            {
                1 => byte.MaxValue,
                2 => ushort.MaxValue,
                _ => int.MaxValue
            };

            if (NullOutput)
                maxLength -= 1;

            if (content.Length > maxLength)
                content = content[..maxLength];

            int sz = content.Length + (NullOutput ? 1 : 0);

            if (szSize == 1)
                stream.Writer.Write((byte)sz);
            else if (szSize == 2)
                stream.Writer.Write((ushort)sz);
            else
                stream.Writer.Write((uint)sz);

            if (content.Length > 0)
            {
                var bytes = Encoding.Latin1.GetBytes(content);
                stream.Writer.Write(bytes);
            }

            if (NullOutput)
                stream.Writer.Write('\0');
        }

        public void Sync(NiStreamReversible stream, int szSize)
        {
            if (stream.CurrentMode == NiStreamReversible.Mode.Read)
                Read(stream.In, szSize);
            else
                Write(stream.Out, szSize);
        }

        public object Clone()
        {
            return MemberwiseClone();
        }
    }
}
