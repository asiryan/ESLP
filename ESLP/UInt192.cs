using System.Numerics;
using System.Runtime.CompilerServices;

namespace ESLP
{

    // =========================================================
    // CUSTOM ULTRA-FAST MATH STRUCT (192-bit)
    // Works on Stack, Zero GC Allocation, High Performance
    // =========================================================
    public readonly struct UInt192 : IEquatable<UInt192>, IComparable<UInt192>
    {
        // Data fields (Low to High significance)
        public readonly ulong r0;
        public readonly ulong r1;
        public readonly ulong r2;

        // Constructors
        public UInt192(ulong low) { r0 = low; r1 = 0; r2 = 0; }
        public UInt192(ulong v0, ulong v1, ulong v2) { r0 = v0; r1 = v1; r2 = v2; }

        // Implicit conversions
        public static implicit operator UInt192(int v) => new UInt192((ulong)v);
        public static implicit operator UInt192(ulong v) => new UInt192(v);

        // --- ARITHMETIC OPERATORS ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static UInt192 operator +(UInt192 a, UInt192 b)
        {
            ulong v0 = a.r0 + b.r0;
            // Check for overflow (carry) in the first limb
            ulong c0 = (v0 < a.r0) ? 1ul : 0ul;

            ulong v1 = a.r1 + b.r1 + c0;
            // Check for overflow in the second limb
            ulong c1 = ((v1 < a.r1) || (c0 == 1 && v1 == a.r1)) ? 1ul : 0ul;

            ulong v2 = a.r2 + b.r2 + c1;

            return new UInt192(v0, v1, v2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static UInt192 operator *(UInt192 a, UInt192 b)
        {
            // Simplified multiplication (calculates only lower 192 bits of the result)
            // Sufficient for this problem as we don't expect overflow beyond 192 bits

            // 1. Lower limb
            UInt128 p00 = (UInt128)a.r0 * b.r0;
            ulong r0 = (ulong)p00;
            ulong carry = (ulong)(p00 >> 64);

            // 2. Middle limb: contributions from (r0*r1) + (r1*r0) + carry
            UInt128 p01 = (UInt128)a.r0 * b.r1;
            UInt128 p10 = (UInt128)a.r1 * b.r0;
            UInt128 sum1 = p01 + p10 + carry;
            ulong r1 = (ulong)sum1;
            carry = (ulong)(sum1 >> 64);

            // 3. High limb: contributions from (r0*r2) + (r1*r1) + (r2*r0) + carry
            UInt128 p02 = (UInt128)a.r0 * b.r2;
            UInt128 p11 = (UInt128)a.r1 * b.r1;
            UInt128 p20 = (UInt128)a.r2 * b.r0;
            UInt128 sum2 = p02 + p11 + p20 + carry;
            ulong r2 = (ulong)sum2;

            return new UInt192(r0, r1, r2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static UInt192 Pow(int x, int k)
        {
            UInt192 baseVal = (UInt192)(ulong)x;
            int exp = k;

            if (exp == 0) return (UInt192)1;
            if (exp == 1) return baseVal;

            UInt192 result = (UInt192)1;
            UInt192 cur = baseVal;
            int e = exp;

            while (e > 0)
            {
                if ((e & 1) != 0)
                    result *= cur;

                e >>= 1;
                if (e > 0)
                    cur *= cur;
            }
            return result;
        }

        // --- EQUALITY AND COMPARISON ---

        public bool Equals(UInt192 other) => r0 == other.r0 && r1 == other.r1 && r2 == other.r2;

        public override bool Equals(object obj)
        {
            return obj is UInt192 other && Equals(other);
        }

        public override int GetHashCode()
        {
            // Combine hash codes of all fields
            return HashCode.Combine(r0, r1, r2);
        }

        public static bool operator ==(UInt192 a, UInt192 b) => a.Equals(b);
        public static bool operator !=(UInt192 a, UInt192 b) => !a.Equals(b);

        public int CompareTo(UInt192 other)
        {
            if (r2 != other.r2) return r2.CompareTo(other.r2);
            if (r1 != other.r1) return r1.CompareTo(other.r1);
            return r0.CompareTo(other.r0);
        }

        // Comparison operators
        public static bool operator <(UInt192 a, UInt192 b) => a.CompareTo(b) < 0;
        public static bool operator >(UInt192 a, UInt192 b) => a.CompareTo(b) > 0;
        public static bool operator <=(UInt192 a, UInt192 b) => a.CompareTo(b) <= 0;
        public static bool operator >=(UInt192 a, UInt192 b) => a.CompareTo(b) >= 0;

        // --- STRING FORMATTING ---

        public override string ToString()
        {
            // Convert to BigInteger for easy human-readable formatting.
            // This operation is slow (allocates memory), but acceptable for printing results.

            byte[] bytes = new byte[24]; // 192 bits = 24 bytes
            BitConverter.TryWriteBytes(new Span<byte>(bytes, 0, 8), r0);
            BitConverter.TryWriteBytes(new Span<byte>(bytes, 8, 8), r1);
            BitConverter.TryWriteBytes(new Span<byte>(bytes, 16, 8), r2);

            // Append 0 byte to ensure BigInteger treats it as unsigned
            BigInteger bigInt = new BigInteger(bytes, isUnsigned: true, isBigEndian: false);

            return bigInt.ToString();
        }

        // Helper for debugging: prints raw hex values
        public string ToHexString()
        {
            return $"0x{r2:X16}{r1:X16}{r0:X16}";
        }
    }
}
