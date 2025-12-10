using System.Runtime.CompilerServices;

namespace ESLP
{
    // ============================================================
    //                WORKER STATE (custom heap)
    // ============================================================
    struct Node
    {
        public UInt192 Sum;
        public int A;
        public int Packed;  // [31..24]=r, [23..0]=idx

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Node(UInt192 s, int a, int packed)
        {
            Sum = s;
            A = a;
            Packed = packed;
        }
    }
}
