using System;
using System.Linq;
using System.Threading;
using MiningCore.Extensions;

namespace MiningCore.Blockchain.Equihash
{
    public class EquihashExtraNonceProvider : ExtraNonceProviderBase
    {
        public EquihashExtraNonceProvider() : base(3)
        {
        }
    }
}
