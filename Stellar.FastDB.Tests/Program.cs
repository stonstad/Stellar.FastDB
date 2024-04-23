using System.Threading.Tasks;

namespace Stellar.Collections.Tests
{
    internal class Program
    {
        private static async Task Main(string[] args)
        {
            int runs = 1;
            int n = 5000;

            for (int i = 0; i < runs; i++)
                await FastDBFeatures.Test(n);
        }
    }
}
