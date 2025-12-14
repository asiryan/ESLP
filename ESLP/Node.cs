using System.Runtime.CompilerServices;

namespace ESLP
{
    // ============================================================
    //                WORKER STATE (custom heap)
    // ============================================================
    [method: MethodImpl(MethodImplOptions.AggressiveInlining)]
    struct Node(UInt192 s, int a, int packed)
    {
        public UInt192 Sum = s;
        public int A = a;
        public int Packed = packed;  // [31..24]=r, [23..0]=idx
    }
}
