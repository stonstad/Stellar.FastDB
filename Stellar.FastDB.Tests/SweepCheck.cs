using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.Collections.Tests
{
    /// <summary>
    /// Checks that a compaction sweep finishes, and shrinks the file, on layouts that once kept it moving the same records
    /// to the end of the file lap after lap:
    /// <list type="bullet">
    /// <item>FullBlocks: full blocks of small records, as a bulk add writes them, whose kept size is a little over what
    /// one sweep step moves. The sweep never finished and the file grew without limit.</item>
    /// <item>LargeRecord: one record over 1 MB, more than one sweep step moves. Same.</item>
    /// <item>SmallGap: a few bytes of live data behind a gap under 64 KB. The sweep finished, but only after thousands of
    /// steps, each flushing to disk.</item>
    /// </list>
    /// Each case writes its file with compaction off, then reopens it with compaction on, so the sweep runs at open. The
    /// file must stay under a limit while it does, end no bigger than it started, and reload every record; with
    /// FASTDB_TEST_HOOKS, the open must also flush only a few times.
    /// Usage: sweepcheck [--config=Name] [--case=Name]
    /// </summary>
    public static class SweepCheck
    {
        private const string CollectionName = "Sweep";
        private const int MaxFlushes = 100;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

        private static readonly (string Name, Func<IFastDBCollection<int, byte[]>, Random, Task> Write)[] Cases =
        {
            ("FullBlocks", WriteFullBlocks),
            ("LargeRecord", WriteLargeRecord),
            ("SmallGap", WriteSmallGap),
        };

        public static async Task<int> Run(string[] args)
        {
            string configurationFilter = null;
            string caseFilter = null;
            foreach (string arg in args)
            {
                if (arg.StartsWith("--config="))
                    configurationFilter = arg.Substring("--config=".Length);
                else if (arg.StartsWith("--case="))
                    caseFilter = arg.Substring("--case=".Length);
            }

            string baseDirectory = Path.Combine(Path.GetTempPath(), "FastDB_SweepCheck");
            Console.WriteLine("sweepcheck: compaction at open finishes and shrinks the file");
#if !FASTDB_TEST_HOOKS
            Console.WriteLine("Flush counts skipped: they need FASTDB_TEST_HOOKS, which only Debug builds define.");
#endif
            Console.WriteLine(new string('=', 70));

            int ran = 0;
            int failures = 0;
            foreach ((string name, FastDBOptions options) in ModelCheck.Configurations(baseDirectory))
            {
                if (configurationFilter != null && !string.Equals(configurationFilter, name, StringComparison.OrdinalIgnoreCase))
                    continue;

                ran++;
                Stopwatch stopwatch = Stopwatch.StartNew();
                string error = null;
                foreach ((string caseName, Func<IFastDBCollection<int, byte[]>, Random, Task> write) in Cases)
                {
                    if (caseFilter != null && !string.Equals(caseFilter, caseName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        error = await RunCase(name, options, caseName, write);
                    }
                    catch (Exception ex)
                    {
                        error = ex.ToString();
                    }
                    if (error != null)
                    {
                        error = $"  {caseName}: {error}";
                        break;
                    }
                }
                stopwatch.Stop();

                if (error != null)
                    failures++;
                ModelCheck.WriteResult(name, error == null, error, stopwatch);
            }

            if (ran == 0)
            {
                Console.WriteLine($"No configuration named '{configurationFilter}'.");
                return 1;
            }

            Console.WriteLine(new string('=', 70));
            if (failures == 0)
            {
                Console.WriteLine($"All {ran} configuration(s) passed.");
                return 0;
            }
            Console.WriteLine($"{failures} of {ran} configuration(s) failed.");
            return 1;
        }

        private static async Task<string> RunCase(string name, FastDBOptions options, string caseName, Func<IFastDBCollection<int, byte[]>, Random, Task> write)
        {
            string databaseName = $"{name}_{caseName}";
            string directory = Path.Combine(options.BaseDirectory, databaseName);
            string filePath = Path.Combine(directory, CollectionName + "." + options.FileExtension);
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);

            // write with compaction off, so every dead record is still in the file when it reopens
            FastDB fastDB = new FastDB(databaseName, new FastDBOptions(options) { CompactionEffort = CompactionEffort.Off });
            Dictionary<int, byte[]> expected;
            try
            {
                IFastDBCollection<int, byte[]> written = fastDB.GetCollection<int, byte[]>(CollectionName);
                await write(written, new Random(1));
                expected = written.AsEnumerable().ToDictionary(a => a.Key, a => a.Value);
            }
            finally
            {
                await fastDB.CloseAsync();
            }

            long before = new FileInfo(filePath).Length;
            long limit = 2 * before + 4 * 1024 * 1024;
#if FASTDB_TEST_HOOKS
            long flushes = Interlocked.Read(ref FastDBTestHooks.Flushes);
#endif

            // the sweep runs in the open; watch the file while it does, as a runaway sweep can grow it by gigabytes
            Stopwatch stopwatch = Stopwatch.StartNew();
            Task<FastDB> open = Task.Run(() =>
            {
                FastDB reopened = new FastDB(databaseName, options);
                reopened.GetCollection<int, byte[]>(CollectionName);
                return reopened;
            });
            long largest = before;
            while (!open.IsCompleted)
            {
                largest = Math.Max(largest, new FileInfo(filePath).Length);
                if (largest > limit || stopwatch.Elapsed > Timeout)
                {
                    string problem = $"grew from {before:N0} to {largest:N0} bytes while compacting at open";
                    if (largest <= limit)
                        problem = $"not finished after {Timeout.TotalSeconds:N0} s";
                    ModelCheck.WriteResult(name, false, $"  {caseName}: {problem}.", stopwatch);

                    // the sweep is still running, so exit hard
                    Environment.Exit(2);
                }
                await Task.Delay(10);
            }

            fastDB = await open;
            try
            {
#if FASTDB_TEST_HOOKS
                flushes = Interlocked.Read(ref FastDBTestHooks.Flushes) - flushes;
                if (flushes > MaxFlushes)
                    return $"compacting at open flushed {flushes:N0} times, expected at most {MaxFlushes}.";
#endif
                IFastDBCollection<int, byte[]> collection = fastDB.GetCollection<int, byte[]>(CollectionName);
                if (collection.Count != expected.Count)
                    return $"expected {expected.Count:N0} records after open, found {collection.Count:N0}.";
                foreach ((int key, byte[] value) in expected)
                {
                    if (!collection.TryGet(key, out byte[] actual) || !actual.AsSpan().SequenceEqual(value))
                        return $"record {key} doesn't match after open.";
                }
            }
            finally
            {
                await fastDB.CloseAsync();
            }

            long after = new FileInfo(filePath).Length;
            if (after > before)
                return $"compacting at open grew the file from {before:N0} to {after:N0} bytes.";

            Directory.Delete(directory, true);
            return null;
        }

        // a dead record at the start of the file, so the sweep starts there
        private static void WriteDeadRecord(IFastDBCollection<int, byte[]> collection, Random random)
        {
            collection.Add(0, Bytes(random, 100));
            collection.Update(0, Bytes(random, 100));
        }

        private static Task WriteFullBlocks(IFastDBCollection<int, byte[]> collection, Random random)
        {
            WriteDeadRecord(collection, random);

            // one bulk add is one batch, written as blocks filled to 1 MB; random bytes so compression can't shrink them
            Dictionary<int, byte[]> records = new Dictionary<int, byte[]>();
            for (int key = 1; key <= 30_000; key++)
                records[key] = Bytes(random, 100);
            return collection.AddBulkAsync(records).AsTask();
        }

        private static Task WriteLargeRecord(IFastDBCollection<int, byte[]> collection, Random random)
        {
            WriteDeadRecord(collection, random);
            collection.Add(1, Bytes(random, 2 * 1024 * 1024));
            collection.Add(2, Bytes(random, 100));
            return Task.CompletedTask;
        }

        private static Task WriteSmallGap(IFastDBCollection<int, byte[]> collection, Random random)
        {
            collection.Add(0, Bytes(random, 1));
            for (int i = 0; i < 1000; i++)
                collection.Update(0, Bytes(random, 1));
            return Task.CompletedTask;
        }

        private static byte[] Bytes(Random random, int length)
        {
            byte[] bytes = new byte[length];
            random.NextBytes(bytes);
            return bytes;
        }
    }
}
