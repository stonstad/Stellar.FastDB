using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Stellar.Collections.Tests
{
    /// <summary>
    /// Times opening a collection: creates N records, closes, then reports the fastest of three opens.
    /// Usage: loadcheck [--records=N] [--config=Name]
    /// </summary>
    public static class LoadCheck
    {
        private const int Opens = 3;

        public static async Task<int> Run(string[] args)
        {
            int records = 100_000;
            string configurationFilter = null;

            foreach (string arg in args)
            {
                if (arg.StartsWith("--records="))
                    records = int.Parse(arg.Substring("--records=".Length));
                else if (arg.StartsWith("--config="))
                    configurationFilter = arg.Substring("--config=".Length);
            }

            string baseDirectory = Path.Combine(Path.GetTempPath(), "FastDB_LoadCheck");
            Dictionary<int, Customer> data = TestData.CreateCustomers(records).ToDictionary(a => a.Id, a => a);

            Console.WriteLine($"loadcheck: {records:N0} records, fastest of {Opens} opens");
            Console.WriteLine(new string('=', 70));

            int ran = 0;
            int failures = 0;
            foreach ((string name, FastDBOptions options) in ModelCheck.Configurations(baseDirectory))
            {
                if (configurationFilter != null && !string.Equals(configurationFilter, name, StringComparison.OrdinalIgnoreCase))
                    continue;

                ran++;
                string databaseName = name + "_Load";
                string directory = Path.Combine(baseDirectory, databaseName);

                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);

                    FastDB fastDB = new FastDB(databaseName, options);
                    await fastDB.GetCollection<int, Customer>().AddBulkAsync(data);
                    await fastDB.CloseAsync();
                    long fileBytes = new FileInfo(Path.Combine(directory, nameof(Customer) + ".db")).Length;

                    long fastest = long.MaxValue;
                    for (int open = 0; open < Opens; open++)
                    {
                        // start each open from a clean heap, so the time doesn't depend on what came before it
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();

                        Stopwatch stopwatch = Stopwatch.StartNew();
                        fastDB = new FastDB(databaseName, options);
                        IFastDBCollection<int, Customer> collection = fastDB.GetCollection<int, Customer>();
                        stopwatch.Stop();

                        if (collection.Count != records)
                            throw new Exception($"Expected {records:N0} records after open, found {collection.Count:N0}.");
                        await fastDB.CloseAsync();
                        fastest = Math.Min(fastest, stopwatch.ElapsedTicks);
                    }

                    double seconds = fastest / (double)Stopwatch.Frequency;
                    Console.WriteLine($"{name,-34} {seconds * 1000,8:N1} ms {records / seconds,12:N0} records/s {fileBytes / 1024,9:N0} KB");
                    Directory.Delete(directory, true);
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (ran == 0)
            {
                Console.WriteLine($"No configuration named '{configurationFilter}'.");
                return 1;
            }

            if (failures == 0)
                return 0;
            return 1;
        }
    }
}
