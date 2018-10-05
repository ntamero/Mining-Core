using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MiningCore.Configuration;

namespace MiningCore.Tests
{
    public abstract class TestBase
    {
        protected Dictionary<string, CoinTemplate> coinTemplates;

        protected TestBase()
        {
            ModuleInitializer.Initialize();
        }
    }
}
