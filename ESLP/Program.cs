using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ESLP
{
    class Program
    {
        // ================= SETTINGS =================

        static readonly int K_POWER = 3;
        static readonly int START_FROM = 29_000_000;
        static readonly int SEARCH_LIMIT = 30_000_000;

        // k=8  → SEARCH_LIMIT ≲ 15,384,774
        // k=9  → SEARCH_LIMIT ≲ 2,446,388
        // k=10 → SEARCH_LIMIT ≲ 561,917
        // k=12 → SEARCH_LIMIT ≲ 61,857

        const int PARTITIONS = 256;
        const int MASK = PARTITIONS - 1;

        // 256 partitions ensures high parallelism on modern CPUs

        // ================= DATA =================

        static UInt192[] _powersCache;          // pow(i)
        static int[][] _residueIndices;         // lists of numbers by residue
        static double _estimatedTotalPairs;     // for UI
        static long _totalPairsChecked = 0;     // atomic counter
        static volatile bool _isRunning = true; // running or not

        // ============================================================
        //                       MAIN
        // ============================================================

        static void Main(string[] args)
        {
            Console.Clear();

            double n = SEARCH_LIMIT - START_FROM;
            _estimatedTotalPairs = n * n / 2.0;

            PrintHeader();

            Precompute();

            // ====== SEARCH ======
            var swSearch = Stopwatch.StartNew();

            var uiTask = Task.Run(() => UILoop(swSearch));

            int maxA = SEARCH_LIMIT - START_FROM + 1;
            int heapCapacity = maxA + 1; // 1-based heap array

            Parallel.For(
                0, PARTITIONS,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                () => new WorkerState(heapCapacity),
                (p, state, ws) =>
                {
                    ws.ProcessPartition(p);
                    return ws;
                },
                ws => { }
            );

            _isRunning = false;
            uiTask.Wait();
            swSearch.Stop();

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine(new string('=', 50));
            Console.WriteLine($"Search Finished.");
            Console.WriteLine($"Total Time: {swSearch.Elapsed}");
            Console.WriteLine(new string('=', 50));
        }

        static void Precompute()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("Phase 1: Precomputing powers & building inverted index... ");
            Console.ResetColor();

            var swTotal = Stopwatch.StartNew();

            int N = SEARCH_LIMIT;

            _powersCache = new UInt192[N + 1];

            int[] counts = new int[PARTITIONS];

            // Pass 1: compute powers, count residues
            for (int i = START_FROM; i <= N; i++)
            {
                UInt192 pow = UInt192.Pow(i, K_POWER);
                _powersCache[i] = pow;

                int r = (int)(pow.r0 & MASK);
                counts[r]++;
            }

            // Allocate exact arrays
            _residueIndices = new int[PARTITIONS][];
            for (int r = 0; r < PARTITIONS; r++)
                _residueIndices[r] = new int[counts[r]];

            // Pass 2: fill them
            int[] pos = new int[PARTITIONS];
            for (int i = START_FROM; i <= N; i++)
            {
                int r = (int)(_powersCache[i].r0 & MASK);
                _residueIndices[r][pos[r]++] = i;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Done ({swTotal.Elapsed.TotalSeconds:F2}s).");
            Console.WriteLine();
            Console.ResetColor();
        }

        sealed class WorkerState
        {
            private readonly Node[] _heap;
            private readonly int[] _pos; // monotone pointers
            private int _size;

            public WorkerState(int capacity)
            {
                _heap = new Node[capacity];
                _pos = new int[PARTITIONS];
                _size = 0;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool Less(in UInt192 a, in UInt192 b)
            {
                if (a.r2 != b.r2) return a.r2 < b.r2;
                if (a.r1 != b.r1) return a.r1 < b.r1;
                return a.r0 < b.r0;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void SiftUp(int i)
            {
                Node n = _heap[i];
                while (i > 1)
                {
                    int p = i >> 1;
                    if (!Less(n.Sum, _heap[p].Sum)) break;
                    _heap[i] = _heap[p];
                    i = p;
                }
                _heap[i] = n;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void SiftDown(int i)
            {
                Node n = _heap[i];
                int size = _size;

                while (true)
                {
                    int left = i << 1;
                    if (left > size) break;

                    int right = left + 1;
                    int best = left;

                    if (right <= size && Less(_heap[right].Sum, _heap[left].Sum))
                        best = right;

                    if (!Less(_heap[best].Sum, n.Sum)) break;

                    _heap[i] = _heap[best];
                    i = best;
                }
                _heap[i] = n;
            }

            public void ProcessPartition(int pMod)
            {
                Array.Clear(_pos, 0, PARTITIONS);
                _size = 0;

                var powers = _powersCache;
                var residue = _residueIndices;

                // =================== SEED ===================
                for (int a = START_FROM; a <= SEARCH_LIMIT; a++)
                {
                    UInt192 powA = powers[a];
                    int modA = (int)(powA.r0 & MASK);

                    int r = (pMod - modA + PARTITIONS) & MASK;
                    int[] cand = residue[r];
                    if (cand.Length == 0) continue;

                    int idx = _pos[r];
                    while (idx < cand.Length && cand[idx] < a) idx++;
                    _pos[r] = idx;

                    if (idx < cand.Length)
                    {
                        int b = cand[idx];
                        UInt192 sum = powA + powers[b];
                        int packed = (r << 24) | idx;

                        _heap[++_size] = new Node(sum, a, packed);
                    }
                }

                // =================== HEAPIFY ===================
                for (int i = _size >> 1; i >= 1; i--)
                    SiftDown(i);

                // =================== PROCESS ===================
                UInt192 lastSum = default;
                int lastA = -1, lastB = -1;

                long localCounter = 0;
                const int BATCH = 20000;

                while (_size > 0)
                {
                    Node top = _heap[1];

                    Node tail = _heap[_size--];
                    if (_size > 0)
                    {
                        _heap[1] = tail;
                        SiftDown(1);
                    }

                    localCounter++;
                    if (localCounter >= BATCH)
                    {
                        Interlocked.Add(ref _totalPairsChecked, localCounter);
                        localCounter = 0;
                    }

                    int a = top.A;
                    int packed = top.Packed;
                    int r = (int)((uint)packed >> 24);
                    int idx = packed & 0xFFFFFF;

                    int[] cand = residue[r];
                    int b = cand[idx];

                    UInt192 sum = top.Sum;

                    if (sum == lastSum)
                    {
                        if (a != lastA || b != lastB)
                            PrintSolution(lastA, lastB, a, b, sum, pMod);
                    }

                    lastSum = sum;
                    lastA = a;
                    lastB = b;

                    int nextIdx = idx + 1;
                    if (nextIdx < cand.Length)
                    {
                        int b2 = cand[nextIdx];
                        UInt192 nextSum = powers[a] + powers[b2];
                        int packed2 = (r << 24) | nextIdx;

                        _heap[++_size] = new Node(nextSum, a, packed2);
                        SiftUp(_size);
                    }
                }

                if (localCounter > 0)
                    Interlocked.Add(ref _totalPairsChecked, localCounter);
            }
        }


        // ============================================================
        //                        UI & PRINT
        // ============================================================

        static void UILoop(Stopwatch sw)
        {
            while (_isRunning)
            {
                Thread.Sleep(500);

                long current = Interlocked.Read(ref _totalPairsChecked);
                double elapsed = sw.Elapsed.TotalSeconds;
                double speed = elapsed > 0 ? current / 1_000_000.0 / elapsed : 0;

                double percent = _estimatedTotalPairs > 0 ? current / _estimatedTotalPairs * 100.0 : 0;
                if (percent > 100.0) percent = 100.0;

                TimeSpan eta = TimeSpan.Zero;
                if (speed > 0.1)
                {
                    double remainingPairs = _estimatedTotalPairs - current;
                    if (remainingPairs < 0) remainingPairs = 0;

                    double remainingSeconds = remainingPairs / (speed * 1_000_000.0);

                    if (remainingSeconds < 3600 * 24 * 999)
                        eta = TimeSpan.FromSeconds(remainingSeconds);
                }

                string timeStr = $"{sw.Elapsed:hh\\:mm\\:ss}";
                string etaStr = eta == TimeSpan.Zero
                    ? "--:--:--"
                    : (eta.TotalDays >= 1
                       ? $"{eta.Days}d {eta.Hours}h {eta.Minutes}m"
                       : $"{eta:hh\\:mm\\:ss}");

                lock (Console.Out)
                {
                    Console.Write("\r");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write($"[{timeStr}] ");
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.Write("Progress: ");
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write($"{percent,8:F5}% ");
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.Write($"| Speed: {speed,6:F2} M/s | ETA: {etaStr} | Checked: {current:N0}    ");
                    Console.ResetColor();
                }
            }
        }

        static void PrintSolution(int a, int b, int c, int d, UInt192 sum, int p)
        {
            lock (Console.Out)
            {
                Console.WriteLine();
                Console.WriteLine(new string('-', 60));
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"### SOLUTION FOUND [slice {p}] ###");
                Console.WriteLine($"{a}^{K_POWER} + {b}^{K_POWER} == {c}^{K_POWER} + {d}^{K_POWER}");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"Sum Value: {sum}");
                Console.ResetColor();
                Console.WriteLine(new string('-', 60));
                Console.WriteLine();
            }
        }

        static void PrintHeader()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("See also:");
            Console.WriteLine("Valery Asiryan");
            Console.WriteLine("The Linear Slicing Method for Equal Sums of Like Powers: Modular and Geometric Constraints, 2025");
            Console.WriteLine("https://arxiv.org/abs/2512.00551");
            Console.WriteLine(@"
              _   _ _   _____ ____      _       _____    _    ____  _____ 
             | | | | | |_   _|  _ \    / \     |  ___|  / \  / ___||_   _|
             | | | | |   | | | |_) |  / _ \    | |_    / _ \ \___ \  | |  
             | |_| | |___| | |  _ <  / ___ \   |  _|  / ___ \ ___) | | |  
              \___/|_____|_| |_| \_\/_/   \_\  |_|   /_/   \_\____/  |_|  
            ");
            Console.WriteLine("============================================================================================");
            Console.WriteLine($"    Equation    : a^{K_POWER} + b^{K_POWER} = c^{K_POWER} + d^{K_POWER}");
            Console.WriteLine($"    Range       : {START_FROM:N0} .. {SEARCH_LIMIT:N0}");
            Console.WriteLine($"    Partitions  : {PARTITIONS} (Parallel execution)");
            Console.WriteLine($"    Est. Pairs  : {_estimatedTotalPairs:N0}");
            Console.WriteLine("============================================================================================");
            Console.WriteLine();
            Console.ResetColor();
        }
    }
}
