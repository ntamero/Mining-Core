/*
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MiningCore.Blockchain.Bitcoin.DaemonResponses;
using MiningCore.Configuration;
using MiningCore.Crypto;
using MiningCore.Extensions;
using MiningCore.Stratum;
using MiningCore.Time;
using MiningCore.Util;
using NBitcoin;
using NBitcoin.DataEncoders;
using Contract = MiningCore.Contracts.Contract;
using Transaction = NBitcoin.Transaction;

namespace MiningCore.Blockchain.Bitcoin
{
    public class BitcoinJob
    {
        private IHashAlgorithm blockHasher;
        private IMasterClock clock;
        private IHashAlgorithm coinbaseHasher;
        private double shareMultiplier;
        private int extraNoncePlaceHolderLength;
        private IHashAlgorithm headerHasher;
        private bool isPoS;

        private BitcoinNetworkType networkType;
        private IDestination poolAddressDestination;
        private PoolConfig poolConfig;
        private BitcoinTemplate coin;
        private readonly HashSet<string> submissions = new HashSet<string>();
        private uint256 blockTargetValue;
        private byte[] coinbaseFinal;
        private string coinbaseFinalHex;
        private byte[] coinbaseInitial;
        private string coinbaseInitialHex;
        private string[] merkleBranchesHex;
        private MerkleTree mt;

        private Network NBitcoinNetworkType
        {
            get
            {
                switch(networkType)
                {
                    case BitcoinNetworkType.Main:
                        return Network.Main;
                    case BitcoinNetworkType.Test:
                        return Network.TestNet;
                    case BitcoinNetworkType.RegTest:
                        return Network.RegTest;

                    default:
                        throw new NotSupportedException("unsupported network type");
                }
            }
        }

        ///////////////////////////////////////////
        // GetJobParams related properties

        private object[] jobParams;
        private string previousBlockHashReversedHex;
        private Money rewardToPool;
        private Transaction txOut;

        // serialization constants
        private static byte[] scriptSigFinalBytes = new Script(Op.GetPushOp(Encoding.UTF8.GetBytes("/MiningCore/"))).ToBytes();

        private static byte[] sha256Empty = Enumerable.Repeat((byte) 0, 32).ToArray();
        private uint txVersion = 1u; // transaction version (currently 1) - see https://en.bitcoin.it/wiki/Transaction

        private static uint txInputCount = 1u;
        private static uint txInPrevOutIndex = (uint) (Math.Pow(2, 32) - 1);
        private static uint txInSequence;
        private static uint txLockTime;

        private void BuildMerkleBranches()
        {
            var transactionHashes = BlockTemplate.Transactions
                .Select(tx => (tx.TxId ?? tx.Hash)
                    .HexToByteArray()
                    .ReverseArray())
                .ToArray();

            mt = new MerkleTree(transactionHashes);

            merkleBranchesHex = mt.Steps
                .Select(x => x.ToHexString())
                .ToArray();
        }

        private void BuildCoinbase()
        {
            // generate script parts
            var sigScriptInitial = GenerateScriptSigInitial();
            var sigScriptInitialBytes = sigScriptInitial.ToBytes();

            var sigScriptLength = (uint) (
                sigScriptInitial.Length +
                extraNoncePlaceHolderLength +
                scriptSigFinalBytes.Length);

            // output transaction
            txOut = coin.HasMasterNodes ?
                CreateMasternodeOutputTransaction() :
                CreateOutputTransaction();

            // build coinbase initial
            using(var stream = new MemoryStream())
            {
                var bs = new BitcoinStream(stream, true);

                // version
                bs.ReadWrite(ref txVersion);

                // timestamp for POS coins
                if (isPoS)
                {
                    var timestamp = BlockTemplate.CurTime;
                    bs.ReadWrite(ref timestamp);
                }

                // serialize (simulated) input transaction
                bs.ReadWriteAsVarInt(ref txInputCount);
                bs.ReadWrite(ref sha256Empty);
                bs.ReadWrite(ref txInPrevOutIndex);

                // signature script initial part
                bs.ReadWriteAsVarInt(ref sigScriptLength);
                bs.ReadWrite(ref sigScriptInitialBytes);

                // done
                coinbaseInitial = stream.ToArray();
                coinbaseInitialHex = coinbaseInitial.ToHexString();
            }

            // build coinbase final
            using(var stream = new MemoryStream())
            {
                var bs = new BitcoinStream(stream, true);

                // signature script final part
                bs.ReadWrite(ref scriptSigFinalBytes);

                // tx in sequence
                bs.ReadWrite(ref txInSequence);

                // serialize output transaction
                var txOutBytes = SerializeOutputTransaction(txOut);
                bs.ReadWrite(ref txOutBytes);

                // misc
                bs.ReadWrite(ref txLockTime);

                // done
                coinbaseFinal = stream.ToArray();
                coinbaseFinalHex = coinbaseFinal.ToHexString();
            }
        }

        private byte[] SerializeOutputTransaction(Transaction tx)
        {
            var withDefaultWitnessCommitment = !string.IsNullOrEmpty(BlockTemplate.DefaultWitnessCommitment);

            var outputCount = (uint) tx.Outputs.Count;
            if (withDefaultWitnessCommitment)
                outputCount++;

            using(var stream = new MemoryStream())
            {
                var bs = new BitcoinStream(stream, true);

                // write output count
                bs.ReadWriteAsVarInt(ref outputCount);

                long amount;
                byte[] raw;
                uint rawLength;

                // serialize witness (segwit)
                if (withDefaultWitnessCommitment)
                {
                    amount = 0;
                    raw = BlockTemplate.DefaultWitnessCommitment.HexToByteArray();
                    rawLength = (uint) raw.Length;

                    bs.ReadWrite(ref amount);
                    bs.ReadWriteAsVarInt(ref rawLength);
                    bs.ReadWrite(ref raw);
                }

                // serialize outputs
                foreach(var output in tx.Outputs)
                {
                    amount = output.Value.Satoshi;
                    var outScript = output.ScriptPubKey;
                    raw = outScript.ToBytes(true);
                    rawLength = (uint) raw.Length;

                    bs.ReadWrite(ref amount);
                    bs.ReadWriteAsVarInt(ref rawLength);
                    bs.ReadWrite(ref raw);
                }

                return stream.ToArray();
            }
        }

        private Script GenerateScriptSigInitial()
        {
            var now = ((DateTimeOffset) clock.Now).ToUnixTimeSeconds();

            // script ops
            var ops = new List<Op>();

            // push block height
            ops.Add(Op.GetPushOp(BlockTemplate.Height));

            // optionally push aux-flags
            if (!string.IsNullOrEmpty(BlockTemplate.CoinbaseAux?.Flags))
                ops.Add(Op.GetPushOp(BlockTemplate.CoinbaseAux.Flags.HexToByteArray()));

            // push timestamp
            ops.Add(Op.GetPushOp(now));

            // push placeholder
            ops.Add(Op.GetPushOp((uint) 0));

            return new Script(ops);
        }

        private Transaction CreateOutputTransaction()
        {
            rewardToPool = new Money(BlockTemplate.CoinbaseValue * coin.BlockrewardMultiplier, MoneyUnit.Satoshi);

            var tx = Transaction.Create(NBitcoinNetworkType);

            tx.Outputs.Insert(0, new TxOut(rewardToPool, poolAddressDestination)
            {
                Value = rewardToPool
            });

            return tx;
        }

        private bool RegisterSubmit(string extraNonce1, string extraNonce2, string nTime, string nonce)
        {
            var key = new StringBuilder()
                .Append(extraNonce1)
                .Append(extraNonce2.ToLower()) // lowercase as we don't want to accept case-sensitive values as valid.
                .Append(nTime)
                .Append(nonce.ToLower()) // lowercase as we don't want to accept case-sensitive values as valid.
                .ToString();

            lock(submissions)
            {
                if (submissions.Contains(key))
                    return false;

                submissions.Add(key);
                return true;
            }
        }

        private byte[] SerializeHeader(Span<byte> coinbaseHash, uint nTime, uint nonce)
        {
            // build merkle-root
            var merkleRoot = mt.WithFirst(coinbaseHash.ToArray());

#pragma warning disable 618
            var blockHeader = new BlockHeader
#pragma warning restore 618
            {
                Version = (int) BlockTemplate.Version,
                Bits = new Target(Encoders.Hex.DecodeData(BlockTemplate.Bits)),
                HashPrevBlock = uint256.Parse(BlockTemplate.PreviousBlockhash),
                HashMerkleRoot = new uint256(merkleRoot),
                BlockTime = DateTimeOffset.FromUnixTimeSeconds(nTime),
                Nonce = nonce
            };

            return blockHeader.ToBytes();
        }

        private (Share Share, string BlockHex) ProcessShareInternal(StratumClient worker, string extraNonce2, uint nTime, uint nonce)
        {
            var context = worker.ContextAs<BitcoinWorkerContext>();
            var extraNonce1 = context.ExtraNonce1;

            // build coinbase
            var coinbase = SerializeCoinbase(extraNonce1, extraNonce2);
            Span<byte> coinbaseHash = stackalloc byte[32];
            coinbaseHasher.Digest(coinbase, coinbaseHash);

            // hash block-header
            var headerBytes = SerializeHeader(coinbaseHash, nTime, nonce);
            Span<byte> headerHash = stackalloc byte[32];
            headerHasher.Digest(headerBytes, headerHash, (ulong) nTime);
            var headerValue = new uint256(headerHash);

            // calc share-diff
            var shareDiff = (double) new BigRational(BitcoinConstants.Diff1, headerHash.ToBigInteger()) * shareMultiplier;
            var stratumDifficulty = context.Difficulty;
            var ratio = shareDiff / stratumDifficulty;

            // check if the share meets the much harder block difficulty (block candidate)
            var isBlockCandidate = headerValue <= blockTargetValue;

            // test if share meets at least workers current difficulty
            if (!isBlockCandidate && ratio < 0.99)
            {
                // check if share matched the previous difficulty from before a vardiff retarget
                if (context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
                {
                    ratio = shareDiff / context.PreviousDifficulty.Value;

                    if (ratio < 0.99)
                        throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");

                    // use previous difficulty
                    stratumDifficulty = context.PreviousDifficulty.Value;
                }

                else
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
            }

            var result = new Share
            {
                BlockHeight = BlockTemplate.Height,
                NetworkDifficulty = Difficulty * shareMultiplier,
                Difficulty = stratumDifficulty,
            };

            if (isBlockCandidate)
            {
                result.IsBlockCandidate = true;
                result.BlockReward = rewardToPool.ToDecimal(MoneyUnit.BTC);

                Span<byte> blockHash = stackalloc byte[32];
                blockHasher.Digest(headerBytes, blockHash, nTime);
                result.BlockHash = blockHash.ToHexString();

                var blockBytes = SerializeBlock(headerBytes, coinbase);
                var blockHex = blockBytes.ToHexString();

                return (result, blockHex);
            }

            return (result, null);
        }

        private byte[] SerializeCoinbase(string extraNonce1, string extraNonce2)
        {
            var extraNonce1Bytes = extraNonce1.HexToByteArray();
            var extraNonce2Bytes = extraNonce2.HexToByteArray();

            using(var stream = new MemoryStream())
            {
                stream.Write(coinbaseInitial);
                stream.Write(extraNonce1Bytes);
                stream.Write(extraNonce2Bytes);
                stream.Write(coinbaseFinal);

                return stream.ToArray();
            }
        }

        private byte[] SerializeBlock(byte[] header, byte[] coinbase)
        {
            var transactionCount = (uint) BlockTemplate.Transactions.Length + 1; // +1 for prepended coinbase tx
            var rawTransactionBuffer = BuildRawTransactionBuffer();

            using(var stream = new MemoryStream())
            {
                var bs = new BitcoinStream(stream, true);

                bs.ReadWrite(ref header);
                bs.ReadWriteAsVarInt(ref transactionCount);
                bs.ReadWrite(ref coinbase);
                bs.ReadWrite(ref rawTransactionBuffer);

                // POS coins require a zero byte appended to block which the daemon replaces with the signature
                if (isPoS)
                    bs.ReadWrite((byte) 0);

                return stream.ToArray();
            }
        }

        private byte[] BuildRawTransactionBuffer()
        {
            using(var stream = new MemoryStream())
            {
                foreach(var tx in BlockTemplate.Transactions)
                {
                    var txRaw = tx.Data.HexToByteArray();
                    stream.Write(txRaw);
                }

                return stream.ToArray();
            }
        }

        #region Masternodes

        private MasterNodeBlockTemplateExtra masterNodeParameters;

        private Transaction CreateMasternodeOutputTransaction()
        {
            var blockReward = new Money(BlockTemplate.CoinbaseValue * coin.BlockrewardMultiplier, MoneyUnit.Satoshi);
            rewardToPool = new Money(BlockTemplate.CoinbaseValue, MoneyUnit.Satoshi);

            var tx = Transaction.Create(NBitcoinNetworkType);

            // outputs
            rewardToPool = CreateMasternodeOutputs(tx, blockReward);

            // Finally distribute remaining funds to pool
            tx.Outputs.Insert(0, new TxOut(rewardToPool, poolAddressDestination)
            {
                Value = rewardToPool
            });

            return tx;
        }

        private Money CreateMasternodeOutputs(Transaction tx, Money reward)
        {
            if (masterNodeParameters.Masternode != null && masterNodeParameters.SuperBlocks != null)
            {
                if (!string.IsNullOrEmpty(masterNodeParameters.Masternode.Payee))
                {
                    var payeeAddress = BitcoinUtils.AddressToDestination(masterNodeParameters.Masternode.Payee);
                    var payeeReward = masterNodeParameters.Masternode.Amount;

                    reward -= payeeReward;
                    rewardToPool -= payeeReward;

                    tx.AddOutput(payeeReward, payeeAddress);
                }

                else if (masterNodeParameters.SuperBlocks.Length > 0)
                {
                    foreach (var superBlock in masterNodeParameters.SuperBlocks)
                    {
                        var payeeAddress = BitcoinUtils.AddressToDestination(superBlock.Payee);
                        var payeeReward = superBlock.Amount;

                        reward -= payeeReward;
                        rewardToPool -= payeeReward;

                        tx.AddOutput(payeeReward, payeeAddress);
                    }
                }
            }

            if (!string.IsNullOrEmpty(masterNodeParameters.Payee))
            {
                var payeeAddress = BitcoinUtils.AddressToDestination(masterNodeParameters.Payee);
                var payeeReward = masterNodeParameters.PayeeAmount ?? (reward / 5);

                reward -= payeeReward;
                rewardToPool -= payeeReward;

                tx.AddOutput(payeeReward, payeeAddress);
            }

            return reward;
        }

        #endregion // Masternodes

        #region API-Surface

        public BlockTemplate BlockTemplate { get; private set; }
        public double Difficulty { get; private set; }

        public string JobId { get; private set; }

        public void Init(BlockTemplate blockTemplate, string jobId,
            PoolConfig poolConfig, ClusterConfig clusterConfig, IMasterClock clock,
            IDestination poolAddressDestination, BitcoinNetworkType networkType,
            bool isPoS, double shareMultiplier, IHashAlgorithm coinbaseHasher,
            IHashAlgorithm headerHasher, IHashAlgorithm blockHasher)
        {
            Contract.RequiresNonNull(blockTemplate, nameof(blockTemplate));
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));
            Contract.RequiresNonNull(clusterConfig, nameof(clusterConfig));
            Contract.RequiresNonNull(clock, nameof(clock));
            Contract.RequiresNonNull(poolAddressDestination, nameof(poolAddressDestination));
            Contract.RequiresNonNull(coinbaseHasher, nameof(coinbaseHasher));
            Contract.RequiresNonNull(headerHasher, nameof(headerHasher));
            Contract.RequiresNonNull(blockHasher, nameof(blockHasher));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId), $"{nameof(jobId)} must not be empty");

            this.poolConfig = poolConfig;
            coin = poolConfig.Template.As<BitcoinTemplate>();
            this.clock = clock;
            this.poolAddressDestination = poolAddressDestination;
            this.networkType = networkType;
            BlockTemplate = blockTemplate;
            JobId = jobId;
            Difficulty = new Target(new NBitcoin.BouncyCastle.Math.BigInteger(BlockTemplate.Target, 16)).Difficulty;
            extraNoncePlaceHolderLength = BitcoinConstants.ExtranoncePlaceHolderLength;
            this.isPoS = isPoS;
            this.shareMultiplier = shareMultiplier;

            if (coin.HasMasterNodes)
                masterNodeParameters = BlockTemplate.Extra.SafeExtensionDataAs<MasterNodeBlockTemplateExtra>();

            this.coinbaseHasher = coinbaseHasher;
            this.headerHasher = headerHasher;
            this.blockHasher = blockHasher;

            if (!string.IsNullOrEmpty(BlockTemplate.Target))
                blockTargetValue = new uint256(BlockTemplate.Target);
            else
            {
                var tmp = new Target(BlockTemplate.Bits.HexToByteArray());
                blockTargetValue = tmp.ToUInt256();
            }

            previousBlockHashReversedHex = BlockTemplate.PreviousBlockhash
                .HexToByteArray()
                .ReverseByteOrder()
                .ToHexString();

            BuildMerkleBranches();
            BuildCoinbase();

            jobParams = new object[]
            {
                JobId,
                previousBlockHashReversedHex,
                coinbaseInitialHex,
                coinbaseFinalHex,
                merkleBranchesHex,
                BlockTemplate.Version.ToStringHex8(),
                BlockTemplate.Bits,
                BlockTemplate.CurTime.ToStringHex8(),
                false
            };
        }

        public object GetJobParams(bool isNew)
        {
            jobParams[jobParams.Length - 1] = isNew;
            return jobParams;
        }

        public (Share Share, string BlockHex) ProcessShare(StratumClient worker,
            string extraNonce2, string nTime, string nonce)
        {
            Contract.RequiresNonNull(worker, nameof(worker));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(extraNonce2), $"{nameof(extraNonce2)} must not be empty");
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nTime), $"{nameof(nTime)} must not be empty");
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nonce), $"{nameof(nonce)} must not be empty");

            var context = worker.ContextAs<BitcoinWorkerContext>();

            // validate nTime
            if (nTime.Length != 8)
                throw new StratumException(StratumError.Other, "incorrect size of ntime");

            var nTimeInt = uint.Parse(nTime, NumberStyles.HexNumber);
            if (nTimeInt < BlockTemplate.CurTime || nTimeInt > ((DateTimeOffset) clock.Now).ToUnixTimeSeconds() + 7200)
                throw new StratumException(StratumError.Other, "ntime out of range");

            // validate nonce
            if (nonce.Length != 8)
                throw new StratumException(StratumError.Other, "incorrect size of nonce");

            var nonceInt = uint.Parse(nonce, NumberStyles.HexNumber);

            // dupe check
            if (!RegisterSubmit(context.ExtraNonce1, extraNonce2, nTime, nonce))
                throw new StratumException(StratumError.DuplicateShare, "duplicate share");

            return ProcessShareInternal(worker, extraNonce2, nTimeInt, nonceInt);
        }

        #endregion // API-Surface
    }
}
