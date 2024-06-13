using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Stellar.Collections.Tests
{
    public static class FastDBFeatures
    {
        public static async Task Test(int n)
        {
            string fileName = Path.Combine(new FastDBOptions().DirectoryPath, "Customer.db");
            if (File.Exists(fileName))
                File.Delete(fileName);

            // test: default
            FastDBOptions options = new FastDBOptions();
            await TestFeatures("Default", options, TestData.CreateCustomers(n));

            // test: optional serialization contract
            options = new FastDBOptions() { Serializer = SerializerType.MessagePack_Contract };
            await TestContractSerialization("Serialization Contract", options, TestData.CreateCustomersWithSerializationContract(n));

            // test: write buffering
            options = new FastDBOptions()
            {
                BufferMode = BufferModeType.WriteEnabled
            };
            await TestFeatures("Write Buffering", options, TestData.CreateCustomers(n));

            // test: encryption
            options = new FastDBOptions()
            {
                IsEncryptionEnabled = true,
                EncryptionPassword = "open-sesame",
            };
            await TestFeatures("Encrypted", options, TestData.CreateCustomers(n));

            // test: parallel transformation OFF
            options = new FastDBOptions()
            {
                IsEncryptionEnabled = true,
                IsCompressionEnabled = true,
                EncryptionPassword = "open-sesame",
            };
            await TestLargeRecord("Parallel Transformation Off (Large Record)", options, TestData.CreateCustomersLongText(n));

            // test: parallel transformation ON
            options = new FastDBOptions()
            {
                BufferMode = BufferModeType.WriteParallelEnabled,
                MaxDegreeOfParallelism = 8,
                IsEncryptionEnabled = true,
                IsCompressionEnabled = true,
                EncryptionPassword = "open-sesame",
            };
            await TestLargeRecord("Parallel Transformation On (Large Record)", options, TestData.CreateCustomersLongText(n));
        }

        public static async Task TestFeatures(string test, FastDBOptions options, List<Customer> testData)
        {
            Stopwatch stopwatch = new Stopwatch();
            Random random = new Random(TestData.Seed1);
            int count;

            TestOutput.WriteTestHeader("FastDB", test, testData.Count);

            // create database
            FastDB fastDB = new FastDB(options);
            var customers = fastDB.GetCollection<int, Customer>();

            // bulk
            stopwatch.Restart();
            await customers.AddBulkAsync(testData.ToDictionary(a => a.Id, a => a));
            await customers.FlushAsync();
            stopwatch.Stop();
            TestOutput.WriteThroughputResult("AddBulk", testData.Count, stopwatch);

            // remove bulk
            stopwatch.Restart();
            await customers.RemoveBulkAsync(testData.Select(a => a.Id));
            await customers.FlushAsync();
            stopwatch.Stop();
            TestOutput.WriteThroughputResult("RemoveBulk", testData.Count, stopwatch);

            // add
            stopwatch.Restart();
            foreach (Customer customer in testData)
                customers.Add(customer.Id, customer);
            stopwatch.Stop();
            TestOutput.WriteThroughputResult("Add", testData.Count, stopwatch);

            // remove
            stopwatch.Restart();
            foreach (Customer customer in testData)
                customers.Remove(customer.Id, out Customer removedCustomer);
            stopwatch.Stop();
            TestOutput.WriteThroughputResult("Remove", testData.Count, stopwatch);

            // add
            foreach (Customer customer in testData)
                customers.Add(customer.Id, customer);

            // upsert
            stopwatch.Restart();
            foreach (Customer customer in customers)
            {
                customer.Telephone = random.Next(1000000, 9999999);
                customers.AddOrUpdate(customer.Id, customer);
            }
            stopwatch.Stop();
            TestOutput.WriteThroughputResult("Update", testData.Count, stopwatch);

            // query
            stopwatch.Restart();
            count = customers.Where(a => a.Name.StartsWith("John") && a.Telephone > 5555555).Count();
            stopwatch.Stop();
            TestOutput.WriteThroughputResult("Query", testData.Count, stopwatch, detail: $"{count.ToString("N0")} matches");

            // close
            stopwatch.Restart();
            await fastDB.CloseAsync();
            stopwatch.Stop();
            TestOutput.WriteTimingResult("Close", stopwatch.ElapsedMilliseconds);

            // open
            fastDB = new FastDB(options);
            stopwatch = Stopwatch.StartNew();
            var allCustomers = fastDB.GetCollection<int, Customer>();
            stopwatch.Stop();
            TestOutput.WriteThroughputResult("Open", count, stopwatch, detail: $"{allCustomers.Count.ToString("N0")} records");

            // iterate and confirm data integrity
            stopwatch = Stopwatch.StartNew();
            count = 0;
            foreach (Customer customer in testData)
            {
                count++;
                if (!customer.Equals(allCustomers[customer.Id]))
                    throw new Exception("Data mismatch encountered");
            }
            stopwatch.Stop();
            TestOutput.WriteThroughputResult("Iteration", count, stopwatch, detail: $"{count.ToString("N0")} records");

            // file size
            long fileSizeBytes = fastDB.GetFileSizeBytes();
            TestOutput.WriteFileSizeBytes("File Size", fileSizeBytes);

            fastDB.Delete();

            Console.WriteLine();
        }

        public static async Task TestContractSerialization(string test, FastDBOptions options, List<CustomerWithContract> testData)
        {
            Stopwatch stopwatch = new Stopwatch();

            TestOutput.WriteTestHeader("FastDB", test, testData.Count);

            // create database
            FastDB FastDB = new FastDB(options);
            var customers = FastDB.GetCollection<int, CustomerWithContract>();

            // add
            stopwatch.Restart();
            foreach (CustomerWithContract customer in testData)
                customers.Add(customer.Id, customer);
            stopwatch.Stop();
            TestOutput.WriteThroughputResult("Add", testData.Count, stopwatch);

            // file size
            long fileSizeBytes = FastDB.GetFileSizeBytes();
            TestOutput.WriteFileSizeBytes("File Size", fileSizeBytes);

            FastDB.Delete();

            Console.WriteLine();

            await Task.CompletedTask;
        }

        public static async Task TestLargeRecord(string test, FastDBOptions options, List<Customer> testData)
        {
            Stopwatch stopwatch = new Stopwatch();

            TestOutput.WriteTestHeader("FastDB", test, testData.Count);

            // create database
            FastDB FastDB = new FastDB(options);
            var customers = FastDB.GetCollection<int, Customer>();

            // add
            stopwatch.Restart();
            foreach (Customer customer in testData)
                customers.Add(customer.Id, customer);
            await customers.FlushAsync();
            stopwatch.Stop();
            TestOutput.WriteThroughputResult("Add", testData.Count, stopwatch);

            // file size
            long fileSizeBytes = FastDB.GetFileSizeBytes();
            TestOutput.WriteFileSizeBytes("File Size", fileSizeBytes);

            FastDB.Delete();

            Console.WriteLine();
        }
    }
}
