using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Stellar.Collections.Tests
{
    /// <summary>
    /// Randomized model-based correctness check. The same random sequence of operations is applied to FastDB and to a
    /// reference Dictionary; after every reopen the persisted data must match the reference exactly.
    /// Usage: modelcheck [--seed=N] [--ops=N] [--config=Name] [--no-clear] [--sync-only]
    /// </summary>
    public static class ModelCheck
    {
        private const int KeySpace = 5000;
        private const string Password = "open-sesame";
        private const int HistoryLength = 25;

        // a configuration is reported as hung if no operation completes for this long
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan CloseAfterFailureTimeout = TimeSpan.FromSeconds(10);

        private sealed class Settings
        {
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

            List<(string Name, FastDBOptions Options)> configurations = new List<(string, FastDBOptions)>()
            {
                ("Default", new FastDBOptions() { BaseDirectory = baseDirectory }),
                ("Json", new FastDBOptions() { BaseDirectory = baseDirectory, Serializer = SerializerType.SystemTextJson_JSON }),
                ("Compressed", new FastDBOptions() { BaseDirectory = baseDirectory, IsCompressionEnabled = true }),
                ("Encrypted", new FastDBOptions() { BaseDirectory = baseDirectory, IsEncryptionEnabled = true, EncryptionPassword = Password }),
                ("EncryptedCompressed", new FastDBOptions() { BaseDirectory = baseDirectory, IsEncryptionEnabled = true, IsCompressionEnabled = true, EncryptionPassword = Password }),
                ("WriteBuffered", new FastDBOptions() { BaseDirectory = baseDirectory, BufferMode = BufferModeType.WriteEnabled }),
                ("WriteParallel", new FastDBOptions() { BaseDirectory = baseDirectory, BufferMode = BufferModeType.WriteParallelEnabled, MaxDegreeOfParallelism = 4 }),
                ("WriteParallelEncryptedCompressed", new FastDBOptions() { BaseDirectory = baseDirectory, BufferMode = BufferModeType.WriteParallelEnabled, MaxDegreeOfParallelism = 4, IsEncryptionEnabled = true, IsCompressionEnabled = true, EncryptionPassword = Password }),
            };

            string clearSetting = "on";
            if (!settings.IncludeClear)
                clearSetting = "off";

            string asyncSetting = "on";
            if (settings.SyncOnly)
                asyncSetting = "off";

            Console.WriteLine($"ModelCheck: seed {settings.Seed}, {settings.Operations:N0} operations per configuration, " +
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
                Task<string> run = Task.Run(() => RunConfiguration(name, options, settings, progress));

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

        private static void WriteResult(string name, bool passed, string detail, Stopwatch stopwatch)
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
            string command = $"modelcheck --seed={settings.Seed} --ops={settings.Operations} --config={name}";
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
        /// Runs one configuration. Returns null on success, or a description of the first failure.
        /// </summary>
        private static async Task<string> RunConfiguration(string name, FastDBOptions options, Settings settings, Progress progress)
        {
            string directory = Path.Combine(options.BaseDirectory, name);
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

                fastDB = new FastDB(name, options);
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
                    else if (roll < 970)
                    {
                        Record($"{OperationName("Flush", useAsync)}()");
                        if (useAsync)
                            await collection.FlushAsync();
                        else
                            collection.Flush();
                    }
                    else if (roll < 998 || !includeClear)
                    {
                        Record($"Reopen() expecting {model.Count} records");
                        await fastDB.CloseAsync();
                        fastDB = new FastDB(name, options);
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
                fastDB = new FastDB(name, options);
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
