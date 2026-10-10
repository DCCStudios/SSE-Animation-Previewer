using System;

namespace NiflySharp
{
    /// <summary>
    /// A NIF boolean. 32-bit up to and including file version 4.0.0.2 and 8-bit from 4.1.0.1 on.
    /// Old files often store arbitrary non-zero values, so the raw value is kept as it is.
    /// Behaves like a nullable boolean, where the raw value <see cref="NullValue"/> means "no value".
    /// </summary>
    public struct NiBool : IEquatable<NiBool>
    {
        /// <summary>
        /// Raw value that represents a boolean without a value.
        /// </summary>
        public const uint NullValue = 2;

        private uint _value;

        public NiBool(bool value)
        {
            _value = value ? 1u : 0u;
        }

        public NiBool(bool? value)
        {
            _value = value.HasValue ? (value.Value ? 1u : 0u) : NullValue;
        }

        /// <summary>
        /// Boolean without a value.
        /// </summary>
        public static NiBool Null => new() { _value = NullValue };

        /// <summary>
        /// Raw value as it is stored in the file.
        /// </summary>
        public uint RawValue { readonly get => _value; set => _value = value; }

        /// <summary>
        /// A value other than <see cref="NullValue"/> is stored.
        /// </summary>
        public readonly bool HasValue => _value != NullValue;

        /// <summary>
        /// Boolean value. Any stored value other than 0 and <see cref="NullValue"/> counts as true.
        /// </summary>
        public readonly bool Value => _value != 0 && _value != NullValue;

        /// <summary>
        /// Boolean value, or false if no value is stored.
        /// </summary>
        public readonly bool GetValueOrDefault() => Value;

        /// <summary>
        /// Boolean value, or <paramref name="defaultValue"/> if no value is stored.
        /// </summary>
        public readonly bool GetValueOrDefault(bool defaultValue) => HasValue ? Value : defaultValue;

        public static implicit operator NiBool(bool value) => new(value);

        public static implicit operator NiBool(bool? value) => new(value);

        public static implicit operator bool(NiBool value) => value.Value;

        public static bool operator ==(NiBool a, NiBool b) => a.Value == b.Value && a.HasValue == b.HasValue;

        public static bool operator !=(NiBool a, NiBool b) => !(a == b);

        public static bool operator ==(NiBool a, bool b) => a.Value == b;

        public static bool operator !=(NiBool a, bool b) => !(a == b);

        public readonly bool Equals(NiBool other) => this == other;

        public override readonly bool Equals(object obj) => obj is NiBool other && this == other;

        public override readonly int GetHashCode() => _value.GetHashCode();

        public override readonly string ToString() => HasValue ? Value.ToString() : string.Empty;
    }
}
