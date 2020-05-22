using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Miningcore.Blockchain.Bitcoin.DaemonResponses
{
    public class ScRefund
    {
        public string Payee { get; set; }
        public string Script { get; set; }
        public long Amount { get; set; }
    }
    public class ScRefundBlockTemplateExtra : PayeeBlockTemplateExtra
    {
        public JToken ScRefund { get; set; }

    }
}
