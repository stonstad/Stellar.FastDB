using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.Collections.Tests
{
    /// <summary>
    /// Randomized model-based correctness checks, run per configuration. Each is a separate command:
    /// <list type="bullet">
    /// <item>modelcheck: ordinary use. The same random add, update, remove, bulk, flush, reopen and clear sequence is
    /// applied to FastDB and to a reference Dictionary; after every reopen the data must match the reference exactly.</item>
    /// <item>crashcheck: the end of the file is damaged at random offsets; each reopen must keep exactly what was written
    /// before the damage.</item>
    /// <item>concurrentcheck: several threads write overlapping keys; what reopens must match what was in memory.</item>
    /// <item>compactioncheck: the modelcheck sequence plus explicit Compact() calls, checking copies of the file taken
    /// where a crash could interrupt compaction.</item>
    /// </list>
    /// Usage: modelcheck|crashcheck|concurrentcheck|compactioncheck [--seed=N] [--ops=N] [--config=Name] [--no-clear] [--sync-only]
    /// </summary>
    public static class ModelCheck
    {
        private const int KeySpace = 5000;
        private const string Password = "open-sesame";
        private const int HistoryLength = 25;

        private const int CrashOperations = 300;
        private const int CrashCuts = 50;

        private const int ConcurrentThreads = 8;
        private const int ConcurrentKeys = 64;
        private const int ConcurrentRounds = 3;

        private const int CrashCopiesPerCompact = 2;

        // a configuration is reported as hung if no operation completes for this long
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan CloseAfterFailureTimeout = TimeSpan.FromSeconds(10);

        public static readonly string[] Checks = { "modelcheck", "crashcheck", "concurrentcheck", "compactioncheck" };

        private sealed class Settings
        {
            public string Check = "modelcheck";
            public int Seed = 1;
            public int Operations = 1000;
            public bool IncludeClear = true;
            public bool SyncOnly = false;
        }

        private sealed class Progress
        {
            public volatile int Operation;
        }

        public static async Task<int> Run(string[] args)
        {
            Settings settings = new Settings();
            string configurationFilter = null;
            if (args.Length > 0)
                settings.Check = args[0];

            foreach (string arg in args)
            {
                if (arg.StartsWith("--seed="))
                    settings.Seed = int.Parse(arg.Substring("--seed=".Length));
                else if (arg.StartsWith("--ops="))
                    settings.Operations = int.Parse(arg.Substring("--ops=".Length));
                else if (arg.StartsWith("--config="))
                    configurationFilter = arg.Substring("--config=".Length);
                else if (arg == "--no-clear")
                    settings.IncludeClear = false;
                else if (arg == "--sync-only")
                    settings.SyncOnly = true;
            }

            string baseDirectory = Path.Combine(Path.GetTempPath(), "FastDB_ModelCheck");

            List<(string Name, FastDBOptions Options)> configurations = Configurations(baseDirectory);

            string clearSetting = "on";
            if (!settings.IncludeClear)
                clearSetting = "off";

            string asyncSetting = "on";
            if (settings.SyncOnly)
                asyncSetting = "off";

            Console.WriteLine($"{settings.Check}: seed {settings.Seed}, {settings.Operations:N0} operations per configuration, " +
                $"clear {clearSetting}, async {asyncSetting}");
            Console.WriteLine(new string('=', 70));

            int ran = 0;
            int failures = 0;
            bool hung = false;

            foreach ((string name, FastDBOptions options) in configurations)
            {
                if (configurationFilter != null && !string.Equals(configurationFilter, name, StringComparison.OrdinalIgnoreCase))
                    continue;

                ran++;
                Progress progress = new Progress();
                Stopwatch stopwatch = Stopwatch.StartNew();
#if FASTDB_TEST_HOOKS
                long flushes = Interlocked.Read(ref FastDBTestHooks.Flushes);
                long flushTicks = Interlocked.Read(ref FastDBTestHooks.FlushTicks);
#endif

                Task<string> run = Task.Run(() =>
                {
                    if (settings.Check == "crashcheck")
                        return Task.FromResult(RunCrashRecovery(name, options, settings, progress));
                    if (settings.Check == "concurrentcheck")
                        return RunConcurrent(name, options, settings, progress);
                    return RunConfiguration(name, options, settings, progress, settings.Check == "compactioncheck");
                });

                // watchdog: only a lack of progress counts as a hang, not a long run
                int lastOperation = -1;
                Stopwatch sinceProgress = Stopwatch.StartNew();
                bool stalled = false;
                while (!run.IsCompleted)
                {
                    await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(1)));
                    int current = progress.Operation;
                    if (current != lastOperation)
                    {
                        lastOperation = current;
                        sinceProgress.Restart();
                    }
                    else if (sinceProgress.Elapsed > StallTimeout)
                    {
                        stalled = true;
                        break;
                    }
                }
                stopwatch.Stop();

                if (stalled)
                {
                    failures++;
                    hung = true;
                    WriteResult(name, false, $"  HUNG: no progress for {StallTimeout.TotalSeconds:N0} s at operation #{lastOperation}." +
                        $"{Environment.NewLine}  Rerun: {RerunCommand(name, settings)}", stopwatch);
                    continue;
                }

                string error = await run;
                if (error == null)
                    WriteResult(name, true, null, stopwatch);
                else
                {
                    failures++;
                    WriteResult(name, false, error, stopwatch);
                }

#if FASTDB_TEST_HOOKS
                flushes = Interlocked.Read(ref FastDBTestHooks.Flushes) - flushes;
                flushTicks = Interlocked.Read(ref FastDBTestHooks.FlushTicks) - flushTicks;
                Console.WriteLine($"     {flushes:N0} flushes to disk taking {flushTicks * 1000 / Stopwatch.Frequency:N0} ms");
#endif
            }

            if (ran == 0)
            {
                Console.WriteLine($"No configuration named '{configurationFilter}'. Available: {string.Join(", ", configurations.ConvertAll(a => a.Name))}");
                return 1;
            }

            Console.WriteLine(new string('=', 70));
            if (failures == 0)
                Console.WriteLine($"All {ran} configuration(s) passed.");
            else
                Console.WriteLine($"{failures} of {ran} configuration(s) failed.");

            // a hung configuration leaves background work running, so exit hard
            if (hung)
                Environment.Exit(2);

            if (failures == 0)
                return 0;
            return 1;
        }

        internal static List<(string Name, FastDBOptions Options)> Configurations(string baseDirectory)
        {
            return new List<(string, FastDBOptions)>()
            {
                ("Default", new FastDBOptions() { BaseDirectory = baseDirectory }),
                ("Json", new FastDBOptions() { BaseDirectory = baseDirectory, Serializer = SerializerType.SystemTextJson_JSON }),
                ("Compressed", new FastDBOptions() { BaseDirectory = baseDirectory, IsCompressionEnabled = true }),
                ("Encrypted", new FastDBOptions() { BaseDirectory = baseDirectory, IsEncryptionEnabled = true, EncryptionPassword = Password }),
                ("EncryptedCompressed", new FastDBOptions() { BaseDirectory = baseDirectory, IsEncryptionEnabled = true, IsCompressionEnabled = true, EncryptionPassword = Password }),
                ("Buffered", new FastDBOptions() { BaseDirectory = baseDirectory, Durability = DurabilityMode.Buffered }),
                ("Synced", new FastDBOptions() { BaseDirectory = baseDirectory, Durability = DurabilityMode.Synced }),
                ("SyncedEncryptedCompressed", new FastDBOptions() { BaseDirectory = baseDirectory, Durability = DurabilityMode.Synced, IsEncryptionEnabled = true, IsCompressionEnabled = true, EncryptionPassword = Password }),
                ("CompactionFlatOut", new FastDBOptions() { BaseDirectory = baseDirectory, CompactionEffort = CompactionEffort.Maximum }),
            };
        }

        internal static void WriteResult(string name, bool passed, string detail, Stopwatch stopwatch)
        {
            if (passed)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("PASS ");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("FAIL ");
            }
            Console.ResetColor();
            Console.WriteLine($"{name,-34} {stopwatch.ElapsedMilliseconds,7} ms");
            if (detail != null)
                Console.WriteLine(detail);
        }

        private static string RerunCommand(string name, Settings settings)
        {
            string command = $"{settings.Check} --seed={settings.Seed} --ops={settings.Operations} --config={name}";
            if (!settings.IncludeClear)
                command += " --no-clear";
            if (settings.SyncOnly)
                command += " --sync-only";
            return command;
        }

        // "Add" or "AddAsync", for the operation history
        private static string OperationName(string operation, bool useAsync)
        {
            if (useAsync)
                return operation + "Async";
            return operation;
        }

        /// <summary>
        /// Runs one configuration of modelcheck, or of compactioncheck when compaction is set. Returns null on success, or
        /// a description of the first failure.
        /// </summary>
        private static async Task<string> RunConfiguration(string name, FastDBOptions options, Settings settings, Progress progress, bool compaction)
        {
            string databaseName = name;
            if (compaction)
                databaseName = name + "_Compaction";
            string directory = Path.Combine(options.BaseDirectory, databaseName);
            int operations = settings.Operations;
            bool includeClear = settings.IncludeClear;

            Random random = new Random(settings.Seed);
            Dictionary<int, Customer> model = new Dictionary<int, Customer>();
            KeySet keys = new KeySet();
            Queue<string> history = new Queue<string>();
            int operation = 0;

            void Record(string description)
            {
                progress.Operation = operation;
                history.Enqueue($"#{operation}: {description}");
                if (history.Count > HistoryLength)
                    history.Dequeue();
            }

            FastDB fastDB = null;
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);

                fastDB = new FastDB(databaseName, options);
                IFastDBCollection<int, Customer> collection = fastDB.GetCollection<int, Customer>();

                for (operation = 0; operation < operations; operation++)
                {
                    int roll = random.Next(1000);
                    bool full = keys.Count >= KeySpace * 3 / 4;
                    bool useAsync = random.Next(4) == 0 && !settings.SyncOnly;

                    if (roll < 300 && !full)
                    {
                        int key = NewKey(random, keys, null);
                        Customer customer = CreateCustomer(random, key);
                        Record($"{OperationName("Add", useAsync)}({key}, {customer.Name.Length} chars)");
                        if (useAsync)
                            await collection.AddAsync(key, customer);
                        else
                            collection.Add(key, customer);
                        model[key] = customer;
                        keys.Add(key);
                    }
                    else if (roll < 600 && keys.Count > 0)
                    {
                        int key = keys.Pick(random);
                        Customer customer = CreateCustomer(random, key);
                        Record($"{OperationName("Update", useAsync)}({key}, {customer.Name.Length} chars)");
                        if (useAsync)
                            await collection.UpdateAsync(key, customer);
                        else
                            collection.Update(key, customer);
                        model[key] = customer;
                    }
                    else if (roll < 850 && keys.Count > 0)
                    {
                        int key = keys.Pick(random);
                        Record($"{OperationName("Remove", useAsync)}({key})");
                        if (useAsync)
                            await collection.RemoveAsync(key);
                        else
                            collection.Remove(key, out _);
                        model.Remove(key);
                        keys.Remove(key);
                    }
                    else if (roll < 900 && !full)
                    {
                        int count = random.Next(1, 50);
                        Dictionary<int, Customer> bulk = new Dictionary<int, Customer>();
                        for (int b = 0; b < count && keys.Count + bulk.Count < KeySpace; b++)
                        {
                            int key = NewKey(random, keys, bulk);
                            bulk[key] = CreateCustomer(random, key);
                        }
                        Record($"AddBulkAsync({bulk.Count} records)");
                        await collection.AddBulkAsync(bulk);
                        foreach (var kvp in bulk)
                        {
                            model[kvp.Key] = kvp.Value;
                            keys.Add(kvp.Key);
                        }
                    }
                    else if (roll < 940 && keys.Count > 0)
                    {
                        int count = Math.Min(keys.Count, random.Next(1, 50));
                        HashSet<int> removed = new HashSet<int>();
                        while (removed.Count < count)
                            removed.Add(keys.Pick(random));
                        Record($"{OperationName("RemoveBulk", useAsync)}({removed.Count} records)");
                        if (useAsync)
                            await collection.RemoveBulkAsync(removed);
                        else
                            collection.RemoveBulk(removed);
                        foreach (int key in removed)
                        {
                            model.Remove(key);
                            keys.Remove(key);
                        }
                    }
                    else if (roll < 970 && (!compaction || roll < 950))
                    {
                        Record($"{OperationName("Flush", useAsync)}()");
                        if (useAsync)
                            await collection.FlushAsync();
                        else
                            collection.Flush();
                    }
                    else if (roll < 970)
                    {
                        Record($"{OperationName("Compact", useAsync)}()");
                        await CompactWithCrashPoints(name, options, collection, model, random, useAsync);
                    }
                    else if (roll < 998 || !includeClear)
                    {
                        Record($"Reopen() expecting {model.Count} records");
                        await fastDB.CloseAsync();
                        fastDB = new FastDB(databaseName, options);
                        collection = fastDB.GetCollection<int, Customer>();
                        Verify(collection, model);
                    }
                    else
                    {
                        Record("Clear()");
                        collection.Clear();
                        model.Clear();
                        keys.Clear();
                    }
                }

                operation = operations;
                Record($"Final reopen expecting {model.Count} records");
                await fastDB.CloseAsync();
                fastDB = new FastDB(databaseName, options);
                collection = fastDB.GetCollection<int, Customer>();
                Verify(collection, model);
                await fastDB.CloseAsync();
                fastDB = null;

                Directory.Delete(directory, true);
                return null;
            }
            catch (Exception ex)
            {
                string error = $"  {ex.GetType().Name}: {ex.Message}{Environment.NewLine}" +
                    $"  Rerun: {RerunCommand(name, settings)}{Environment.NewLine}" +
                    $"  Last operations:{Environment.NewLine}    " + string.Join(Environment.NewLine + "    ", history);

                // the database may be broken; don't let a stuck close hide the original failure
                try
                {
                    if (fastDB != null && !fastDB.IsClosed)
                    {
                        Task close = fastDB.CloseAsync();
                        if (await Task.WhenAny(close, Task.Delay(CloseAfterFailureTimeout)) != close)
                            error += $"{Environment.NewLine}  (closing the database after the failure also hung)";
                    }
                }
                catch
                {
                    // the original failure is what matters
                }

                return error;
            }
        }

        /// <summary>
        /// Writes a random sequence, then damages the end of the file at random offsets by truncating it or garbling
        /// bytes. Each reopen must load exactly the operations written before the damage, and accept new writes.
        /// </summary>
        private static string RunCrashRecovery(string name, FastDBOptions options, Settings settings, Progress progress)
        {
            // the expected file layout comes from an append-only history, so background compaction must not rearrange it
            options = new FastDBOptions(options) { CompactionEffort = CompactionEffort.Off };

            string databaseName = name + "_Crash";
            string directory = Path.Combine(options.BaseDirectory, databaseName);
            string filePath = Path.Combine(directory, nameof(Customer) + ".db");
            Random random = new Random(settings.Seed);
            List<List<(int Key, Customer Value)>> changes = new List<List<(int, Customer)>>();
            List<long> endOffsets = new List<long>();
            string step = "writing";
            FastDB fastDB = null;

            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);

                fastDB = new FastDB(databaseName, options);
                IFastDBCollection<int, Customer> collection = fastDB.GetCollection<int, Customer>();
                long headerEnd = collection.GetSizeBytes();
                KeySet keys = new KeySet();

                int operations = Math.Min(settings.Operations, CrashOperations);
                for (int operation = 0; operation < operations; operation++)
                {
                    List<(int Key, Customer Value)> change = new List<(int, Customer)>();
                    int roll = random.Next(100);

                    if (roll < 40 || keys.Count == 0)
                    {
                        int key = NewKey(random, keys, null);
                        Customer customer = CreateCustomer(random, key);
                        collection.Add(key, customer);
                        keys.Add(key);
                        change.Add((key, customer));
                    }
                    else if (roll < 70)
                    {
                        int key = keys.Pick(random);
                        Customer customer = CreateCustomer(random, key);
                        collection.Update(key, customer);
                        change.Add((key, customer));
                    }
                    else if (roll < 90)
                    {
                        int key = keys.Pick(random);
                        collection.Remove(key, out _);
                        keys.Remove(key);
                        change.Add((key, null));
                    }
                    else
                    {
                        Dictionary<int, Customer> bulk = new Dictionary<int, Customer>();
                        int count = random.Next(1, 20);
                        for (int b = 0; b < count; b++)
                        {
                            int key = NewKey(random, keys, bulk);
                            bulk[key] = CreateCustomer(random, key);
                        }
                        collection.AddBulkAsync(bulk).AsTask().GetAwaiter().GetResult();
                        foreach (var kvp in bulk)
                        {
                            keys.Add(kvp.Key);
                            change.Add((kvp.Key, kvp.Value));
                        }
                    }

                    // in buffered modes the file only reflects an operation after a flush
                    collection.Flush();
                    changes.Add(change);
                    endOffsets.Add(collection.GetSizeBytes());
                }

                fastDB.Close();
                fastDB = null;
                byte[] original = File.ReadAllBytes(filePath);

                for (int cut = 0; cut < CrashCuts; cut++)
                {
                    progress.Operation++;

                    long offset = random.NextInt64(headerEnd, original.Length + 1);
                    bool garble = random.Next(2) == 0;
                    byte[] damaged;
                    if (garble)
                    {
                        step = $"garbling from byte {offset:N0} of {original.Length:N0}";
                        damaged = (byte[])original.Clone();
                        long garbleEnd = Math.Min(offset + random.Next(1, 64), damaged.Length);
                        for (long i = offset; i < garbleEnd; i++)
                            damaged[i] ^= (byte)random.Next(1, 256);
                    }
                    else
                    {
                        step = $"truncating to {offset:N0} of {original.Length:N0} bytes";
                        damaged = new byte[offset];
                        Array.Copy(original, damaged, offset);
                    }
                    File.WriteAllBytes(filePath, damaged);

                    // only operations that ended before the damage survive
                    int last = endOffsets.FindLastIndex(a => a <= offset);
                    Dictionary<int, Customer> expected = Replay(changes, last);
                    long expectedLength = headerEnd;
                    if (last >= 0)
                        expectedLength = endOffsets[last];

                    fastDB = new FastDB(databaseName, options);
                    collection = fastDB.GetCollection<int, Customer>();
                    Verify(collection, expected);

                    // the damaged block is cut off, and compaction at open can only shrink the file further
                    if (collection.GetSizeBytes() > expectedLength)
                        throw new Exception($"File length after recovery: expected at most {expectedLength:N0}, found {collection.GetSizeBytes():N0}.");

                    Customer extra = CreateCustomer(random, KeySpace + 1);
                    collection.Add(extra.Id, extra);
                    expected[extra.Id] = extra;
                    fastDB.Close();

                    fastDB = new FastDB(databaseName, options);
                    Verify(fastDB.GetCollection<int, Customer>(), expected);
                    fastDB.Close();
                    fastDB = null;
                }

                Directory.Delete(directory, true);
                return null;
            }
            catch (Exception ex)
            {
                try
                {
                    fastDB?.Close();
                }
                catch
                {
                    // the original failure is what matters
                }

                return $"  Crash recovery, {step}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}" +
                    $"  Rerun: {RerunCommand(name, settings)}";
            }
        }

        /// <summary>
        /// Compacts while keeping copies of the file taken where a crash could interrupt compaction. Each copy, and a
        /// copy with a just-written header slot torn, must load to exactly the model. Without FASTDB_TEST_HOOKS there are
        /// no copies, so it only compacts.
        /// </summary>
        private static async Task CompactWithCrashPoints(string name, FastDBOptions options, IFastDBCollection<int, Customer> collection,
            Dictionary<int, Customer> model, Random random, bool useAsync)
        {
#if !FASTDB_TEST_HOOKS
            if (useAsync)
                await collection.CompactAsync();
            else
                collection.Compact();
#else
            // every crash copy must match the model, so nothing may still be waiting to be written
            collection.Flush();

            List<(byte[] File, int Slot)> copies = new List<(byte[], int)>();
            int seen = 0;
            FastDBTestHooks.CrashPoint = (file, slot) =>
            {
                seen++;
                if (copies.Count < CrashCopiesPerCompact)
                    copies.Add((file, slot));
                else
                {
                    int replace = random.Next(seen);
                    if (replace < CrashCopiesPerCompact)
                        copies[replace] = (file, slot);
                }
            };

            try
            {
                if (useAsync)
                    await collection.CompactAsync();
                else
                    collection.Compact();
            }
            finally
            {
                FastDBTestHooks.CrashPoint = null;
            }

            for (int i = 0; i < copies.Count; i++)
            {
                (byte[] file, int slot) = copies[i];
                VerifyCrashCopy(name, options, file, model, $"crash copy {i + 1} of {seen}");

                if (slot >= 0)
                {
                    byte[] torn = (byte[])file.Clone();
                    for (int b = slot; b < slot + 8; b++)
                        torn[b] ^= 0xFF;
                    VerifyCrashCopy(name, options, torn, model, $"crash copy {i + 1} of {seen} with a torn header slot");
                }
            }
#endif
        }

        private static void VerifyCrashCopy(string name, FastDBOptions options, byte[] file, Dictionary<int, Customer> model, string description)
        {
            string databaseName = name + "_CrashPoint";
            string directory = Path.Combine(options.BaseDirectory, databaseName);
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, nameof(Customer) + ".db"), file);

            FastDB copy = new FastDB(databaseName, options);
            try
            {
                Verify(copy.GetCollection<int, Customer>(), model);
            }
            catch (Exception ex)
            {
                throw new Exception($"Compaction {description}: {ex.Message}", ex);
            }
            finally
            {
                copy.Close();
            }
            Directory.Delete(directory, true);
        }

        private static Dictionary<int, Customer> Replay(List<List<(int Key, Customer Value)>> changes, int last)
        {
            Dictionary<int, Customer> state = new Dictionary<int, Customer>();
            for (int i = 0; i <= last; i++)
            {
                foreach ((int key, Customer value) in changes[i])
                {
                    if (value == null)
                        state.Remove(key);
                    else
                        state[key] = value;
                }
            }
            return state;
        }

        /// <summary>
        /// Several threads write overlapping keys at once. After they finish, what reopens must match what was in
        /// memory.
        /// </summary>
        private static async Task<string> RunConcurrent(string name, FastDBOptions options, Settings settings, Progress progress)
        {
            string databaseName = name + "_Concurrent";
            string directory = Path.Combine(options.BaseDirectory, databaseName);
            int operationsPerThread = Math.Max(1, settings.Operations / ConcurrentThreads);
            int round = 0;
            FastDB fastDB = null;

            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);

                for (round = 0; round < ConcurrentRounds; round++)
                {
                    progress.Operation++;

                    fastDB = new FastDB(databaseName, options);
                    IFastDBCollection<int, Customer> collection = fastDB.GetCollection<int, Customer>();

                    Task[] writers = new Task[ConcurrentThreads];
                    for (int t = 0; t < ConcurrentThreads; t++)
                    {
                        Random random = new Random(settings.Seed * 7919 + round * ConcurrentThreads + t);
                        writers[t] = Task.Run(() => WriteConcurrently(collection, random, operationsPerThread, settings.SyncOnly));
                    }
                    await Task.WhenAll(writers);

                    Dictionary<int, Customer> inMemory = new Dictionary<int, Customer>();
                    foreach (KeyValuePair<int, Customer> kvp in collection.AsEnumerable())
                        inMemory[kvp.Key] = kvp.Value;

                    await fastDB.CloseAsync();
                    fastDB = new FastDB(databaseName, options);
                    Verify(fastDB.GetCollection<int, Customer>(), inMemory);
                    await fastDB.CloseAsync();
                    fastDB = null;
                }

                Directory.Delete(directory, true);
                return null;
            }
            catch (Exception ex)
            {
                try
                {
                    fastDB?.Close();
                }
                catch
                {
                    // the original failure is what matters
                }

                return $"  Concurrent writes, round {round + 1}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}" +
                    $"  Rerun: {RerunCommand(name, settings)}";
            }
        }

        private static async Task WriteConcurrently(IFastDBCollection<int, Customer> collection, Random random, int operations, bool syncOnly)
        {
            for (int i = 0; i < operations; i++)
            {
                int key = random.Next(1, ConcurrentKeys + 1);
                bool useAsync = random.Next(4) == 0 && !syncOnly;
                int roll = random.Next(100);

                try
                {
                    if (roll < 40)
                    {
                        Customer customer = CreateCustomer(random, key);
                        if (useAsync)
                            await collection.AddOrUpdateAsync(key, customer);
                        else
                            collection.AddOrUpdate(key, customer);
                    }
                    else if (roll < 60)
                    {
                        Customer customer = CreateCustomer(random, key);
                        if (useAsync)
                            await collection.AddAsync(key, customer);
                        else
                            collection.Add(key, customer);
                    }
                    else if (roll < 80)
                    {
                        Customer customer = CreateCustomer(random, key);
                        if (useAsync)
                            await collection.UpdateAsync(key, customer);
                        else
                            collection.Update(key, customer);
                    }
                    else
                    {
                        if (useAsync)
                            await collection.RemoveAsync(key);
                        else
                            collection.Remove(key, out _);
                    }
                }
                catch (ArgumentException ex) when (ex.GetType() == typeof(ArgumentException))
                {
                    // another thread added the key first
                }
                catch (KeyNotFoundException)
                {
                    // another thread removed the key first
                }
            }
        }

        private static void Verify(IFastDBCollection<int, Customer> collection, Dictionary<int, Customer> model)
        {
            if (collection.Count != model.Count)
                throw new Exception($"Count mismatch after reopen: expected {model.Count}, found {collection.Count}.");

            foreach (var kvp in model)
            {
                if (!collection.TryGet(kvp.Key, out Customer actual))
                    throw new Exception($"Key {kvp.Key} missing after reopen.");
                if (!kvp.Value.Equals(actual))
                    throw new Exception($"Value mismatch after reopen for key {kvp.Key}.");
            }
        }

        private static int NewKey(Random random, KeySet keys, Dictionary<int, Customer> pending)
        {
            while (true)
            {
                int key = random.Next(1, KeySpace + 1);
                if (!keys.Contains(key) && (pending == null || !pending.ContainsKey(key)))
                    return key;
            }
        }

        // mostly small records, roughly one in four is large, so record sizes vary on every update
        private static Customer CreateCustomer(Random random, int id)
        {
            int length;
            if (random.Next(4) == 0)
                length = random.Next(200, 1200);
            else
                length = random.Next(1, 60);
            char[] name = new char[length];
            for (int i = 0; i < length; i++)
                name[i] = (char)('a' + random.Next(26));

            return new Customer()
            {
                Id = id,
                Name = new string(name),
                Telephone = random.Next(1000000, 9999999),
                DateOfBirth = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(random.Next(0, 10000)),
            };
        }

        /// <summary>
        /// Set of keys supporting O(1) add, remove and uniform random selection.
        /// </summary>
        private sealed class KeySet
        {
            private readonly List<int> _Keys = new List<int>();
            private readonly Dictionary<int, int> _Index = new Dictionary<int, int>();

            public int Count => _Keys.Count;

            public bool Contains(int key) => _Index.ContainsKey(key);

            public void Add(int key)
            {
                _Index[key] = _Keys.Count;
                _Keys.Add(key);
            }

            public void Remove(int key)
            {
                int index = _Index[key];
                int last = _Keys[_Keys.Count - 1];
                _Keys[index] = last;
                _Index[last] = index;
                _Keys.RemoveAt(_Keys.Count - 1);
                _Index.Remove(key);
            }

            public int Pick(Random random) => _Keys[random.Next(_Keys.Count)];

            public void Clear()
            {
                _Keys.Clear();
                _Index.Clear();
            }
        }
    }
}
