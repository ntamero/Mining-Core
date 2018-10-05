using System.Collections.Generic;
using System.IO;
using System.Linq;
using MiningCore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NLog;

namespace MiningCore.Configuration
{
    public static class CoinTemplateLoader
    {
        private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

        private static IEnumerable<KeyValuePair<string, CoinTemplate>> LoadTemplates(string filename, JsonSerializer serializer)
        {
            using (var jreader = new JsonTextReader(File.OpenText(filename)))
            {
                var jo = serializer.Deserialize<JObject>(jreader);

                foreach (var o in jo)
                {
                    if (o.Value.Type != JTokenType.Object)
                        logger.ThrowLogPoolStartupException("Invalid coin definition file contents: dictionary of coin definitions expected");

                    var value = o.Value[nameof(CoinTemplate.Family).ToLower()];
                    if (value == null)
                        logger.ThrowLogPoolStartupException("Invalid coin definition file contents: missing 'family' property");

                    var family = value.ToObject<CoinFamily>();
                    var result = (CoinTemplate)o.Value.ToObject(CoinTemplate.Families[family]);

                    // Patch explorer links
                    if ((result.ExplorerBlockLinks == null || result.ExplorerBlockLinks.Count == 0) &&
                        !string.IsNullOrEmpty(result.ExplorerBlockLink))
                    {
                        result.ExplorerBlockLinks = new Dictionary<string, string>
                        {
                            {"block", result.ExplorerBlockLink}
                        };
                    }

                    yield return KeyValuePair.Create(o.Key, result);
                }
            }
        }

        public static Dictionary<string, CoinTemplate> Load(string[] coinDefs)
        {
            var serializer = new JsonSerializer
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
            };

            var result = new Dictionary<string, CoinTemplate>();

            foreach (var filename in coinDefs)
            {
                var definitions = LoadTemplates(filename, serializer).ToArray();

                foreach (var definition in definitions)
                {
                    var coinId = definition.Key;

                    if (result.ContainsKey(coinId))
                        logger.ThrowLogPoolStartupException($"Duplicate definition of coin '{coinId}' in file {filename}");

                    result[coinId] = definition.Value;
                }
            }

            return result;
        }
    }
}
