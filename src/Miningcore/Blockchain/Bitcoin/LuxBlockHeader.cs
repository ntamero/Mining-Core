using System;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace Miningcore.Blockchain.Bitcoin
{
    public class LuxBlockHeader : IBitcoinSerializable
    {
        public LuxBlockHeader(string hex)
            : this(Encoders.Hex.DecodeData(hex))
        {
        }

        public LuxBlockHeader(byte[] bytes)
        {
            this.ReadWrite(new BitcoinStream(bytes));
        }

        public LuxBlockHeader()
        {
            SetNull();
        }

        private uint256 hashMerkleRoot;
        private uint256 hashPrevBlock;
        private uint nBits;
        private uint nNonce;
        private uint nTime;
        private int nVersion;
        private uint256 hashStateRoot; // lux
        private uint256 hashUtxoRoot; // lux
        // header
        private const int CurrentVersion = 4;

        public uint256 HashPrevBlock
        {
            get => hashPrevBlock;
            set => hashPrevBlock = value;
        }

        public Target Bits
        {
            get => nBits;
            set => nBits = value;
        }

        public int Version
        {
            get => nVersion;
            set => nVersion = value;
        }

        public uint Nonce
        {
            get => nNonce;
            set => nNonce = value;
        }

        public uint256 HashMerkleRoot
        {
            get => hashMerkleRoot;
            set => hashMerkleRoot = value;
        }
        public uint256 HashStateRoot
        {
            get => hashStateRoot;
            set => hashStateRoot = value;
        }
        public uint256 HashUtxoRoot
        {
            get => hashUtxoRoot;
            set => hashUtxoRoot = value;
        }
        public bool IsNull => nBits == 0;

        public uint NTime
        {
            get => nTime;
            set => nTime = value;
        }

        public DateTimeOffset BlockTime
        {
            get => Utils.UnixTimeToDateTime(nTime);
            set => nTime = Utils.DateTimeToUnixTime(value);
        }

        #region IBitcoinSerializable Members

        public void ReadWrite(BitcoinStream stream)
        {
            stream.ReadWrite(ref nVersion);
            stream.ReadWrite(ref hashPrevBlock);
            stream.ReadWrite(ref hashMerkleRoot);
            stream.ReadWrite(ref nTime);
            stream.ReadWrite(ref nBits);
            stream.ReadWrite(ref nNonce);
            if ((nVersion & (1 << 30)) != 0) {
                stream.ReadWrite(ref hashStateRoot);       // lux
                stream.ReadWrite(ref hashUtxoRoot);        // lux
            }
        }

        #endregion

        public static LuxBlockHeader Parse(string hex)
        {
            return new LuxBlockHeader(Encoders.Hex.DecodeData(hex));
        }

        internal void SetNull()
        {
            nVersion = CurrentVersion;
            hashPrevBlock = 0;
            hashMerkleRoot = 0;
            nTime = 0;
            nBits = 0;
            nNonce = 0;
            hashStateRoot = 0;
            hashUtxoRoot = 0;
        }
    }
}
