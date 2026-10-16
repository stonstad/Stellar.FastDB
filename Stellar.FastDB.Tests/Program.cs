using System;
using System.Threading.Tasks;

namespace Stellar.Collections.Tests
{
    internal class Program
    {
        private static async Task<int> Main(string[] args)
        {
            // randomized correctness checks:
            // modelcheck|crashcheck|concurrentcheck|compactioncheck [--seed=N] [--ops=N] [--config=Name] [--no-clear] [--sync-only]
            if (args.Length > 0 && Array.IndexOf(ModelCheck.Checks, args[0]) >= 0)
                return await ModelCheck.Run(args);

            // load timing: loadcheck [--records=N] [--config=Name]
            if (args.Length > 0 && args[0] == "loadcheck")
                return await LoadCheck.Run(args);

            // failure reporting: failurecheck [--config=Name]
            if (args.Length > 0 && args[0] == "failurecheck")
                return await FailureCheck.Run(args);

            int runs = 1;
            int n = 5000;

            for (int i = 0; i < runs; i++)
                await FastDBFeatures.Test(n);

            return 0;
        }
    }
}
