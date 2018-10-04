using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using MiningCore.Crypto.Hashing.Algorithms;
using MiningCore.Crypto.Hashing.Special;

namespace MiningCore.Crypto
{
    public static class HashFactory
    {
        private static readonly ConcurrentDictionary<string, IHashAlgorithm> algos = new ConcurrentDictionary<string, IHashAlgorithm>();

        public static IHashAlgorithm GetHash(string definition)
        {
            if (algos.TryGetValue(definition, out var result))
                return result;

            result = HashFromDefinition(definition);
            algos.TryAdd(definition, result);

            return result;
        }

        private static IHashAlgorithm HashFromDefinition(string definition)
        {
            var index = definition.IndexOf("-reverse");

            if (index != -1)
            {
                var hashName = definition.Substring(0, index);
                var inner = InstantiateHash(hashName);
                var reverser = new DigestReverser(inner);
                return reverser;
            }

            return InstantiateHash(definition);
        }

        private static IHashAlgorithm InstantiateHash(string name)
        {
            var hashClass = (typeof(Sha256D).Namespace + "." + name).ToLower();
            var hashType = Type.GetType(hashClass, true, false);
            var hashInstance = Activator.CreateInstance(hashType);
            return (IHashAlgorithm) hashInstance;
        }
    }
}
