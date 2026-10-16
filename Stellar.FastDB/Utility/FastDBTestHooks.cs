#if FASTDB_TEST_HOOKS
using System;

namespace Stellar.Collections
{
    // compiled in only with FASTDB_TEST_HOOKS (see Directory.Build.props), so packages never contain it
    internal static class FastDBTestHooks
    {
        // When set, receives a copy of a collection file at each point inside compaction where a crash could happen,
        // and the offset of the header slot just written there (or -1). Tests use it to check that every such file
        // still loads correctly.
        public static Action<byte[], int> CrashPoint;

        // When set, called before each block a commit writes to a collection file; returning true makes that write throw
        // an IOException instead, as a full disk would. Tests use it to check how failed writes are reported.
        public static Func<bool> FailWrite;

        // how many times collection files were flushed to disk, and the Stopwatch ticks spent doing it
        public static long Flushes;
        public static long FlushTicks;
    }
}
#endif
