using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.Collections.Tests
{
    /// <summary>
    /// Checks how failures are reported, per configuration, by making them happen on demand:
    /// <list type="bullet">
    /// <item>Write failures (FastDBTestHooks.FailWrite): every write in a failed commit reports the failure, every other
    /// write succeeds, and what reopens is exactly the writes that succeeded. Covers one writer, several sync and async
    /// writers at once, a bulk add that fails after its first block is written, and buffered writes reported by
    /// Flush().</item>
    /// <item>Serialization failures (a record whose property throws): the call reports the failure for the right record,
    /// and nothing from it reaches memory or the file. Covers single adds, and bulk adds serialized on one thread and on
    /// all cores.</item>
    /// </list>
    /// Each runs with the failure behavior set to Exception and to ReturnFalse.
    /// Usage: failurecheck [--config=Name]
    /// </summary>
    public static class FailureCheck
    {
        private const string SimulatedWriteFailure = "Simulated write failure.";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        public static async Task<int> Run(string[] args)
        {
            string configurationFilter = null;
            foreach (string arg in args)
            {
                if (arg.StartsWith("--config="))
                    configurationFilter = arg.Substring("--config=".Length);
            }

            string baseDirectory = Path.Combine(Path.GetTempPath(), "FastDB_FailureCheck");
            Console.WriteLine("failurecheck: write and serialization failures, made on demand");
#if !FASTDB_TEST_HOOKS
            Console.WriteLine("Write failures skipped: they need FASTDB_TEST_HOOKS, which only Debug builds define.");
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
                Task<string> run = Task.Run(() => RunConfiguration(name, options, baseDirectory));
                if (await Task.WhenAny(run, Task.Delay(Timeout)) != run)
                {
                    ModelCheck.WriteResult(name, false, $"  HUNG: not finished after {Timeout.TotalSeconds:N0} s.", stopwatch);

                    // a hung configuration leaves background work running, so exit hard
                    Environment.Exit(2);
                }

                string error;
                try
                {
                    error = await run;
                }
                catch (Exception ex)
                {
                    error = $"  {ex}";
                }
#if FASTDB_TEST_HOOKS
                finally
                {
                    FastDBTestHooks.FailWrite = null;
                }
#endif
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

        private static async Task<string> RunConfiguration(string name, FastDBOptions options, string baseDirectory)
        {
            foreach (ErrorBehaviorType behavior in new[] { ErrorBehaviorType.Exception, ErrorBehaviorType.ReturnFalse })
            {
                string error;
#if FASTDB_TEST_HOOKS
                error = await CheckWriteFailures(name, options, behavior, baseDirectory);
                if (error != null)
                    return $"  write failures, {behavior}: {error}";
#endif

                error = await CheckSerializationFailures(name, options, behavior, baseDirectory);
                if (error != null)
                    return $"  serialization failures, {behavior}: {error}";
            }
            return null;
        }

#if FASTDB_TEST_HOOKS
        private static async Task<string> CheckWriteFailures(string name, FastDBOptions options, ErrorBehaviorType behavior, string baseDirectory)
        {
            options = new FastDBOptions(options) { StorageFailureBehavior = behavior };
            string databaseName = $"{name}_Write_{behavior}";
            string directory = Path.Combine(baseDirectory, databaseName);
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);

            // what must reload: the writes that reported success, and buffered writes that may or may not have been in
            // the failed commit
            Dictionary<int, Customer> expected = new Dictionary<int, Customer>();
            Dictionary<int, Customer> uncertain = new Dictionary<int, Customer>();

            FastDB fastDB = new FastDB(databaseName, options);
            try
            {
                IFastDBCollection<int, Customer> collection = fastDB.GetCollection<int, Customer>();
                string error;
                if (options.Durability == DurabilityMode.Buffered)
                    error = BufferedWriteFailures(collection, behavior, expected, uncertain);
                else
                    error = await WriteFailures(collection, behavior, expected);
                if (error != null)
                    return error;
            }
            finally
            {
                FastDBTestHooks.FailWrite = null;
                await fastDB.CloseAsync();
            }

            string reloadError = await CompareAfterReopen(databaseName, options, expected, uncertain);
            if (reloadError != null)
                return reloadError;

            Directory.Delete(directory, true);
            return null;
        }

        private static async Task<string> WriteFailures(IFastDBCollection<int, Customer> collection, ErrorBehaviorType behavior, Dictionary<int, Customer> expected)
        {
            // one writer: every add is its own commit, so the 50th add is the one that fails
            int writes = 0;
            FastDBTestHooks.FailWrite = () => Interlocked.Increment(ref writes) == 50;
            for (int key = 1; key <= 100; key++)
            {
                Customer customer = CreateCustomer(key, 20);
                bool? succeeded = await Outcome(() => new ValueTask<bool>(collection.Add(key, customer)), behavior);
                if (succeeded == null)
                    return $"Add({key}) returned false, but StorageFailureBehavior is Exception.";
                if (succeeded.Value != (key != 50))
                    return $"Add({key}) reported {Describe(succeeded.Value)}; only Add(50) should fail.";
                if (succeeded.Value)
                    expected[key] = customer;
            }

            // several writers at once, half sync and half async: three commits fail, and every writer whose record was
            // in one of them must report it, which the reload confirms
            int failedCommits = 0;
            writes = 0;
            FastDBTestHooks.FailWrite = () =>
            {
                int write = Interlocked.Increment(ref writes);
                if (write != 5 && write != 20 && write != 35)
                    return false;
                Interlocked.Increment(ref failedCommits);
                return true;
            };

            ConcurrentDictionary<int, (Customer Customer, bool Succeeded)> outcomes = new ConcurrentDictionary<int, (Customer, bool)>();
            Task[] writers = new Task[8];
            for (int w = 0; w < writers.Length; w++)
            {
                int writer = w;
                writers[w] = Task.Run(async () =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        int key = 1000 + writer * 100 + i;
                        Customer customer = CreateCustomer(key, 20);
                        bool? succeeded;
                        if (writer % 2 == 0)
                            succeeded = await Outcome(() => new ValueTask<bool>(collection.Add(key, customer)), behavior);
                        else
                            succeeded = await Outcome(() => collection.AddAsync(key, customer), behavior);
                        if (succeeded == null)
                            throw new InvalidOperationException($"Add({key}) returned false, but StorageFailureBehavior is Exception.");
                        outcomes[key] = (customer, succeeded.Value);
                    }
                });
            }
            await Task.WhenAll(writers);
            FastDBTestHooks.FailWrite = null;

            int failedWrites = outcomes.Values.Count(a => !a.Succeeded);
            if (failedCommits == 0)
                return "None of the concurrent writers' commits failed; the check needs at least one.";
            if (failedWrites < failedCommits)
                return $"{failedCommits} commits failed, but only {failedWrites} writes reported a failure.";
            foreach (KeyValuePair<int, (Customer Customer, bool Succeeded)> outcome in outcomes)
            {
                if (outcome.Value.Succeeded)
                    expected[outcome.Key] = outcome.Value.Customer;
            }

            // a bulk add bigger than one block: its second block fails after the first was written, so the file must be
            // cut back and neither block may reload
            writes = 0;
            FastDBTestHooks.FailWrite = () => Interlocked.Increment(ref writes) == 2;
            Dictionary<int, Customer> bulk = new Dictionary<int, Customer>();
            for (int key = 2000; key < 2040; key++)
                bulk[key] = CreateCustomer(key, 50_000);
            bool? bulkSucceeded = await Outcome(() => collection.AddBulkAsync(bulk), behavior);
            FastDBTestHooks.FailWrite = null;
            if (bulkSucceeded == null)
                return "AddBulkAsync returned false, but StorageFailureBehavior is Exception.";
            if (writes < 2)
                return $"The bulk add was written as {writes} block(s); the check needs at least two.";
            if (bulkSucceeded.Value)
                return "AddBulkAsync reported success, but its second block failed.";

            // writes work again once the failures stop
            for (int key = 3000; key < 3010; key++)
            {
                Customer customer = CreateCustomer(key, 20);
                if (!collection.Add(key, customer))
                    return $"Add({key}) failed after the simulated failures stopped.";
                expected[key] = customer;
            }
            return null;
        }

        // buffered writes return at once, so a failed commit is reported by the next Flush(); which writes it held isn't
        // known, so those may or may not reload
        private static string BufferedWriteFailures(IFastDBCollection<int, Customer> collection, ErrorBehaviorType behavior,
            Dictionary<int, Customer> expected, Dictionary<int, Customer> uncertain)
        {
            int writes = 0;
            FastDBTestHooks.FailWrite = () => Interlocked.Increment(ref writes) == 1;
            for (int key = 1; key <= 50; key++)
            {
                Customer customer = CreateCustomer(key, 20);
                if (!collection.Add(key, customer))
                    return $"Buffered Add({key}) returned false.";
                uncertain[key] = customer;
            }

            bool reported = false;
            try
            {
                collection.Flush();
            }
            catch (IOException ex) when (ex.InnerException?.Message == SimulatedWriteFailure)
            {
                reported = true;
            }
            FastDBTestHooks.FailWrite = null;

            if (writes == 0)
                return "Flush() wrote nothing, so no commit failed.";
            if (behavior == ErrorBehaviorType.Exception && !reported)
                return "Flush() didn't report the failed buffered commit.";
            if (behavior == ErrorBehaviorType.ReturnFalse && reported)
                return "Flush() threw, but StorageFailureBehavior is ReturnFalse.";

            // written after the failure was reported, so these must all reload
            for (int key = 51; key <= 100; key++)
            {
                Customer customer = CreateCustomer(key, 20);
                if (!collection.Add(key, customer))
                    return $"Buffered Add({key}) returned false.";
                expected[key] = customer;
            }
            return null;
        }
#endif

        private static async Task<string> CheckSerializationFailures(string name, FastDBOptions options, ErrorBehaviorType behavior, string baseDirectory)
        {
            options = new FastDBOptions(options) { SerializationFailureBehavior = behavior };
            string databaseName = $"{name}_Serialization_{behavior}";
            string directory = Path.Combine(baseDirectory, databaseName);
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);

            Dictionary<int, Fragile> expected = new Dictionary<int, Fragile>();
            FastDB fastDB = new FastDB(databaseName, options);
            try
            {
                IFastDBCollection<int, Fragile> collection = fastDB.GetCollection<int, Fragile>();

                Dictionary<int, Fragile> good = CreateFragiles(1, 50);
                if (!await collection.AddBulkAsync(good))
                    return "AddBulkAsync of records that serialize returned false.";
                foreach (KeyValuePair<int, Fragile> pair in good)
                    expected[pair.Key] = pair.Value;

                Fragile poisoned = new Fragile() { Id = 1000, Text = "poisoned", Poisoned = true };
                string error = await ExpectSerializationFailure("Add", collection, behavior, 1000, new[] { 1000 },
                    () => new ValueTask<bool>(collection.Add(1000, poisoned)));
                if (error != null)
                    return error;

                poisoned = new Fragile() { Id = 1001, Text = "poisoned", Poisoned = true };
                error = await ExpectSerializationFailure("AddAsync", collection, behavior, 1001, new[] { 1001 },
                    () => collection.AddAsync(1001, poisoned));
                if (error != null)
                    return error;

                // 10 records serialize on one thread; 3,000 serialize on all cores, and the second poisoned record is in a
                // later chunk than the first, so the failure reported must be the first one's
                error = await ExpectBulkFailure("AddBulkAsync on one thread", collection, behavior, 2000, 10, new[] { 2005 });
                if (error != null)
                    return error;
                error = await ExpectBulkFailure("AddBulkAsync on all cores", collection, behavior, 3000, 3000, new[] { 4500, 5900 });
                if (error != null)
                    return error;

                // writes work again afterwards, on all cores and on one thread
                good = CreateFragiles(11000, 3000);
                if (!await collection.AddBulkAsync(good))
                    return "AddBulkAsync on all cores of records that serialize returned false after the failures.";
                foreach (KeyValuePair<int, Fragile> pair in good)
                    expected[pair.Key] = pair.Value;
                for (int key = 20000; key < 20010; key++)
                {
                    Fragile fragile = new Fragile() { Id = key, Text = $"text {key}" };
                    if (!collection.AddOrUpdate(key, fragile))
                        return $"AddOrUpdate({key}) returned false after the failures.";
                    expected[key] = fragile;
                }
            }
            finally
            {
                await fastDB.CloseAsync();
            }

            string reloadError = await CompareAfterReopen(databaseName, options, expected, new Dictionary<int, Fragile>());
            if (reloadError != null)
                return reloadError;

            Directory.Delete(directory, true);
            return null;
        }

        private static Task<string> ExpectBulkFailure(string operation, IFastDBCollection<int, Fragile> collection, ErrorBehaviorType behavior,
            int firstKey, int count, int[] poisonedKeys)
        {
            Dictionary<int, Fragile> bulk = CreateFragiles(firstKey, count);
            foreach (int key in poisonedKeys)
                bulk[key].Poisoned = true;

            return ExpectSerializationFailure(operation, collection, behavior, poisonedKeys[0], bulk.Keys.ToArray(), () => collection.AddBulkAsync(bulk));
        }

        // the write must report the failure of the record with failedKey, and leave none of keys in memory
        private static async Task<string> ExpectSerializationFailure(string operation, IFastDBCollection<int, Fragile> collection,
            ErrorBehaviorType behavior, int failedKey, int[] keys, Func<ValueTask<bool>> write)
        {
            try
            {
                bool result = await write();
                if (behavior == ErrorBehaviorType.Exception)
                    return $"{operation} returned {result}, but a record failed to serialize and SerializationFailureBehavior is Exception.";
                if (result)
                    return $"{operation} returned true, but a record failed to serialize.";
            }
            catch (Exception ex) when (behavior == ErrorBehaviorType.Exception)
            {
                if (!Mentions(ex, Fragile.PoisonMessage(failedKey)))
                    return $"{operation} should report record {failedKey}, but threw {ex.GetType().Name}: {ex.Message}";
            }

            foreach (int key in keys)
            {
                if (collection.TryGet(key, out _))
                    return $"{operation} failed to serialize a record, but key {key} is in memory.";
            }
            return null;
        }

        private static async Task<string> CompareAfterReopen<T>(string databaseName, FastDBOptions options, Dictionary<int, T> expected,
            Dictionary<int, T> uncertain) where T : class, IEquatable<T>
        {
            FastDB fastDB = new FastDB(databaseName, options);
            try
            {
                IFastDBCollection<int, T> collection = fastDB.GetCollection<int, T>();
                foreach (KeyValuePair<int, T> pair in expected)
                {
                    if (!collection.TryGet(pair.Key, out T value))
                        return $"key {pair.Key} reported success but didn't reload.";
                    if (!pair.Value.Equals(value))
                        return $"key {pair.Key} reloaded with a different value.";
                }

                int uncertainMissing = 0;
                foreach (KeyValuePair<int, T> pair in uncertain)
                {
                    if (!collection.TryGet(pair.Key, out T value))
                        uncertainMissing++;
                    else if (!pair.Value.Equals(value))
                        return $"key {pair.Key} reloaded with a different value.";
                }
                if (uncertain.Count > 0 && uncertainMissing == 0)
                    return "Every buffered write reloaded, including the failed commit's.";

                int extra = collection.Count - expected.Count - (uncertain.Count - uncertainMissing);
                if (extra != 0)
                    return $"{extra} record(s) reloaded that weren't reported written.";
            }
            finally
            {
                await fastDB.CloseAsync();
            }
            return null;
        }

        // true when the write reported success, false when it reported the simulated failure the way behavior says, and
        // null when it returned false although failures should throw
        private static async Task<bool?> Outcome(Func<ValueTask<bool>> write, ErrorBehaviorType behavior)
        {
            try
            {
                if (await write())
                    return true;
                if (behavior == ErrorBehaviorType.ReturnFalse)
                    return false;
                return null;
            }
            catch (IOException ex) when (behavior == ErrorBehaviorType.Exception && ex.InnerException?.Message == SimulatedWriteFailure)
            {
                return false;
            }
        }

        private static bool Mentions(Exception exception, string text)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current.Message.Contains(text))
                    return true;
            }
            return false;
        }

        private static string Describe(bool succeeded)
        {
            if (succeeded)
                return "success";
            return "failure";
        }

        // random letters, so compression can't shrink a long name much
        private static Customer CreateCustomer(int id, int nameLength)
        {
            char[] name = new char[nameLength];
            for (int i = 0; i < name.Length; i++)
                name[i] = (char)('a' + Random.Shared.Next(26));

            return new Customer()
            {
                Id = id,
                Name = new string(name),
                Telephone = id,
                DateOfBirth = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(id % 10000),
            };
        }

        private static Dictionary<int, Fragile> CreateFragiles(int firstKey, int count)
        {
            Dictionary<int, Fragile> fragiles = new Dictionary<int, Fragile>();
            for (int key = firstKey; key < firstKey + count; key++)
                fragiles[key] = new Fragile() { Id = key, Text = $"text {key}" };
            return fragiles;
        }
    }

    /// <summary>
    /// A record that throws while being serialized when Poisoned is set, to check how serialization failures are reported.
    /// </summary>
    public sealed class Fragile : IEquatable<Fragile>
    {
        private string _Text;

        public int Id { get; set; }
        public bool Poisoned { get; set; }

        public string Text
        {
            get
            {
                if (Poisoned)
                    throw new InvalidOperationException(PoisonMessage(Id));
                return _Text;
            }
            set
            {
                _Text = value;
            }
        }

        public static string PoisonMessage(int id)
        {
            return $"Record {id} is poisoned.";
        }

        public bool Equals(Fragile other)
        {
            return other != null && Id == other.Id && Poisoned == other.Poisoned && _Text == other._Text;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as Fragile);
        }

        public override int GetHashCode()
        {
            return Id;
        }
    }
}
