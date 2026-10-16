using System.Threading.Tasks;

namespace Stellar.Collections.Tests
{
    internal class Program
    {
        private static async Task<int> Main(string[] args)
        {
            // randomized correctness check: modelcheck [--seed=N] [--ops=N] [--config=Name] [--no-clear] [--sync-only]
            if (args.Length > 0 && args[0] == "modelcheck")
                return await ModelCheck.Run(args);

            int runs = 1;
            int n = 5000;

            for (int i = 0; i < runs; i++)
                await FastDBFeatures.Test(n);

            return 0;
        }
    }
}
