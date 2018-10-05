using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using MiningCore.Crypto.Hashing.Algorithms;
using Newtonsoft.Json.Linq;

namespace MiningCore.Crypto
{
    public static class HashAlgorithmFactory
    {
        private static readonly ConcurrentDictionary<string, IHashAlgorithm> cache = new ConcurrentDictionary<string, IHashAlgorithm>();

        public static IHashAlgorithm GetHash(IComponentContext ctx, JObject definition)
        {
            var hash = definition["hash"]?.Value<string>().ToLower();

            if(string.IsNullOrEmpty(hash))
                throw new NotSupportedException("$Invalid or empty hash value {hash}");

            var args = definition["args"]?
                .Select(token => token.Type == JTokenType.Object ? GetHash(ctx, (JObject)token) : token.Value<object>())
                .ToArray();

            return InstantiateHash(ctx, hash, args);
        }

        private static IHashAlgorithm InstantiateHash(IComponentContext ctx, string name, object[] args)
        {
            // special handling for DigestReverser
            if (name == "reverse")
                name = nameof(DigestReverser);

            // check cache if possible
            var allowCache = args == null || args.Length == 0;
            if (allowCache && cache.TryGetValue(name, out var result))
                return result;

            var hashClass = (typeof(Sha256D).Namespace + "." + name).ToLower();
            var hashType = typeof(Sha256D).Assembly.GetType(hashClass, true, true);
            var parameters = args?.Select((x, i) => new PositionalParameter(i, x)).ToArray();

            // create it (we'll let Autofac do the heavy lifting)
            result = (IHashAlgorithm) (parameters != null && parameters.Length > 0 ?
                ctx.Resolve(hashType, parameters) :
                ctx.Resolve(hashType));

            if(allowCache)
                cache.TryAdd(name, result);

            return result;
        }
    }
}
