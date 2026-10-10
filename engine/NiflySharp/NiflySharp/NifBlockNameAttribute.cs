using System;

namespace NiflySharp
{
    /// <summary>
    /// Records the binary NIF block type name for a class whose C# identifier differs
    /// from the original name in nif.xml. Required for types whose binary name contains
    /// characters that are not valid in C# identifiers (e.g. C++ namespace syntax
    /// <c>BSSkin::Instance</c> becomes the C# class <c>BSSkin_Instance</c>).
    /// Emitted automatically by the source generator; consumed at load and save time
    /// to round-trip the original name.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class NifBlockNameAttribute : Attribute
    {
        /// <summary>
        /// The original block type name as written in the NIF binary.
        /// </summary>
        public string Name { get; }

        public NifBlockNameAttribute(string name)
        {
            Name = name;
        }

        /// <summary>
        /// Returns the binary NIF block type name for <paramref name="type"/>.
        /// If the type carries a <see cref="NifBlockNameAttribute"/>, its <see cref="Name"/>
        /// is returned; otherwise the C# class name is returned unchanged.
        /// </summary>
        public static string GetBinaryName(Type type)
        {
            if (type == null)
                return null;

            var attr = (NifBlockNameAttribute)GetCustomAttribute(type, typeof(NifBlockNameAttribute), inherit: false);
            return attr != null ? attr.Name : type.Name;
        }
    }
}
