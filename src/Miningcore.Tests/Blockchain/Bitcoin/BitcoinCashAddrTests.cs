using System;
using NBitcoin;
using Xunit;
using static Miningcore.Blockchain.Bitcoin.BchAddr;
using static Miningcore.Blockchain.Bitcoin.BitcoinUtils;
namespace Miningcore.Tests.Blockchain.Bitcoin
{
    public class BitcoinCashAddrTests : TestBase
    {
        public  BitcoinCashAddrTests()
        {
            // Declaration of the array
            DVTCashAddrs = new string[2]
            {
                "devault:qzhz336k5ws5ytda06s33pg9kvg4wfmu5spduquykj",
                "devault:qzsh9uhrs80hfdk5fy4cj4k2qpmhksxd7qtwn909wm"
            };
             BCHCashAddrs = new string[2]
             {
                 "bitcoincash:qqrxa0h9jqnc7v4wmj9ysetsp3y7w9l36u8gnnjulq",
                 "bitcoincash:qqmxjv4a94t2cv9fzucrldnfgqvhlntvac3xlxnz8u"
             };


        }
        readonly string[] DVTCashAddrs;
        readonly string[] BCHCashAddrs;

        [Fact]
        public void Bitcoin_CashAddr_Should_Match()
        {
            foreach (var addr in DVTCashAddrs)
            {
                IDestination Dest = CashAddrToDestination(addr);
                Console.WriteLine(Dest.ToString());
            }

            foreach (var addr in BCHCashAddrs)
            {
                IDestination Dest = CashAddrToDestination(addr);
                Console.WriteLine(Dest.ToString());
            }
        }

        [Fact]
        public void BitcoinJob_Should_Not_Accept_Invalid_Share()
        {

        }
    }
}
