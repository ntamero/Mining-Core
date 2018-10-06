using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace MiningCore.Crypto.Hashing.Equihash
{
    public static class EquihashSolverFactory
    {
        private const string HashName = "equihash";

        public static EquihashSolver GetSolver(JObject definition)
        {
            var hash = definition["hash"]?.Value<string>().ToLower();

            if(string.IsNullOrEmpty(hash) || hash != HashName)
                throw new NotSupportedException($"Invalid hash value '{hash}'. Expected '{HashName}'");

            var args = definition["args"]?
                .Select(token => token.Value<object>())
                .ToArray();

            if(args?.Length != 3)
                throw new NotSupportedException($"Invalid hash arguments '{string.Join(", ", args)}'");

            return InstantiateSolver(args);
        }

        private static EquihashSolver InstantiateSolver(object[] args)
        {
            var n = (int) Convert.ChangeType(args[0], typeof(int));
            var k = (int) Convert.ChangeType(args[1], typeof(int));
            var personalization = args[2].ToString();

            switch (n)
            {
                case 200:
                    switch (k)
                    {
                        case 9:
                            return new EquihashSolver_200_9(personalization);
                    }
                    break;

                case 144:
                    switch (k)
                    {
                        case 5:
                            return new EquihashSolver_144_5(personalization);
                    }
                    break;
            }

            throw new NotSupportedException($"Equihash variant {n}_{k} is currently not implemented");
        }
    }
}
