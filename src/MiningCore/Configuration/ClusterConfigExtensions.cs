using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using MiningCore.Crypto;
using MiningCore.Crypto.Hashing.Special;
using Newtonsoft.Json;

namespace MiningCore.Configuration
{
    public abstract partial class CoinTemplate
    {
        protected CoinTemplate()
        {
            algorithmValue = new Lazy<string>(GetAlgorithmName);
        }

        public T As<T>() where T : CoinTemplate
        {
            return (T) this;
        }

        private readonly Lazy<string> algorithmValue;
        public string Algorithm => algorithmValue.Value;

        protected abstract string GetAlgorithmName();
    }

    public partial class BitcoinTemplate
    {
        #region Overrides of CoinDefinition

        protected override string GetAlgorithmName()
        {
            var hash = HashAlgorithmFactory.GetHash(HeaderHasher);

            if (hash.GetType() == typeof(DigestReverser))
                return ((DigestReverser)hash).Upstream.GetType().Name.ToLower();

            return hash.GetType().Name.ToLower();
        }

        #endregion
    }

    public partial class EquihashCoinTemplate
    {
        public partial class EquihashNetworkDefinition
        {
            public EquihashNetworkDefinition()
            {
                diff1Value = new Lazy<NBitcoin.BouncyCastle.Math.BigInteger>(() =>
                {
                    if(string.IsNullOrEmpty(Diff1))
                        throw new InvalidOperationException("Diff1 has not yet been initialized");

                    return new NBitcoin.BouncyCastle.Math.BigInteger(Diff1, 16);
                });

                diff1BValue = new Lazy<BigInteger>(() =>
                {
                    if (string.IsNullOrEmpty(Diff1))
                        throw new InvalidOperationException("Diff1 has not yet been initialized");

                    return BigInteger.Parse(Diff1, NumberStyles.HexNumber);
                });
            }

            private readonly Lazy<NBitcoin.BouncyCastle.Math.BigInteger> diff1Value;
            private readonly Lazy<BigInteger> diff1BValue;

            [JsonIgnore]
            public NBitcoin.BouncyCastle.Math.BigInteger Diff1Value => diff1Value.Value;

            [JsonIgnore]
            public BigInteger Diff1BValue => diff1BValue.Value;

            [JsonIgnore]
            public ulong FoundersRewardSubsidySlowStartShift => FoundersRewardSubsidySlowStartInterval / 2;

            [JsonIgnore]
            public ulong LastFoundersRewardBlockHeight => FoundersRewardSubsidyHalvingInterval + FoundersRewardSubsidySlowStartShift - 1;
        }

        #region Overrides of CoinDefinition

        protected override string GetAlgorithmName()
        {
            // TODO: return variant
            return "Equihash";
        }

        #endregion
    }

    public partial class CryptonoteCoinTemplate
    {
        #region Overrides of CoinDefinition

        protected override string GetAlgorithmName()
        {
            switch (Hash)
            {
                case CryptonightHashType.Normal:
                    return "Cryptonight";
                case CryptonightHashType.Lite:
                    return "Cryptonight-Lite";
                case CryptonightHashType.Heavy:
                    return "Cryptonight-Heavy";
            }

            throw new NotSupportedException("Invalid hash type");
        }

        #endregion
    }

    public partial class EthereumCoinTemplate
    {
        #region Overrides of CoinDefinition

        protected override string GetAlgorithmName()
        {
            return "Ethhash";
        }

        #endregion
    }

    public partial class PoolConfig
    {
        [JsonIgnore]
        public CoinTemplate CoinTemplate { get; set; }
    }
}
