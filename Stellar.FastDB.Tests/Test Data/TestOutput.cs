using System;
using System.Diagnostics;

namespace Stellar.Collections.Tests
{
    public static class TestOutput
    {
        public static void WriteTestHeader(string test, string variation, int samples)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Write($"[{test}]: ");
            Console.Write($"{variation}, ");
            Console.Write($"{samples.ToString("N0")} records");
            Console.WriteLine();
            Console.WriteLine(new string('=', 70));
            Console.ResetColor();
        }

        public static void WriteThroughputResult(string test, int recordCount, Stopwatch stopwatch, string detail = null)
        {
            double throughput = recordCount / stopwatch.Elapsed.TotalSeconds;
            Console.WriteLine($"{test,-16}: {stopwatch.ElapsedMilliseconds ,4} ms - {((int)throughput).ToString("N0"),11} records/sec   {detail,6}");
        }

        public static void WriteTimingResult(string test, float valueMS, string detail = null)
        {
            Console.WriteLine($"{test,-16}: {valueMS,4} ms - {detail,5}");
        }

        public static void WriteFileSizeBytes(string test, long fileSizeBytes)
        {
            Console.WriteLine($"{test,-16}: {fileSizeBytes / 1024,4} kb -");
        }
    }
}
