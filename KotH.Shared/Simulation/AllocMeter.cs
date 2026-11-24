using System;
using System.Threading;

namespace KotH.Shared.Simulation
{
    /// <summary>Bytes allocated per part of a frame, for KOTH_FRAMELOG: a part adds what its own thread
    /// allocated between Begin and End, wherever it runs; Take reads and clears it. Off unless Enabled.</summary>
    public static class AllocMeter
    {
        public enum Part { Simulation, Bots, Avalonia, Count }

        public static bool Enabled;
        static readonly long[] bytes = new long[(int)Part.Count];

        public static long Begin() => Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;

        public static void End(Part part, long begun)
        {
            if (Enabled)
                Interlocked.Add(ref bytes[(int)part], GC.GetAllocatedBytesForCurrentThread() - begun);
        }

        public static long Take(Part part) => Interlocked.Exchange(ref bytes[(int)part], 0);
    }
}
