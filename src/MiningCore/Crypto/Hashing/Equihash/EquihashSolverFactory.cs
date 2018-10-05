using System;
using System.Linq;
using Autofac;
using MiningCore.Crypto.Hashing.Algorithms;
using MiningCore.Crypto.Hashing.Special;
using Newtonsoft.Json.Linq;

namespace MiningCore.Crypto.Hashing.Equihash
{
    public static class EquihashSolverFactory
    {
        private const string HashName = "equihash";

        public static EquihashSolverBase GetSolver(JObject definition)
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

        private static EquihashSolverBase InstantiateSolver(object[] args)
        {
            var n = Convert.ChangeType(args[0], typeof(int));
            var k = Convert.ChangeType(args[1], typeof(int));
            var personalization = (string)args[2];

            var name = $"EquihashSolver_{n}_{k}";
            var hashClass = (typeof(EquihashSolverBase).Namespace + "." + name).ToLower();
            var hashType = Type.GetType(hashClass, true, false);

            var parameters = new []{ new PositionalParameter(0, personalization) };
            var result = Program.Container.Resolve(hashType, parameters);

            return (EquihashSolverBase) result;
        }
    }
}
