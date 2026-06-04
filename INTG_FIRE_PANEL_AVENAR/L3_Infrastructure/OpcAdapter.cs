using INTG_OPC;
using INTG_FIRE_PANEL_AVENAR.Domain;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
using ISS.OPCMonitor;
using Opc.Da;
using System;
using System.Collections.Generic;

namespace INTG_FIRE_PANEL_AVENAR.Infrastructure
{
    public class OpcAdapter : IOpcGateway
    {
        private readonly OpcClient _client;

        public event Action<ItemStateChanged[]> StateChanged;

        public OpcAdapter()
        {
            _client = new OpcClient();
            _client.PanelChanged += MapAndRaise;
        }

        private void MapAndRaise(ItemValueResult[] results)
        {
            var items = new ItemStateChanged[results.Length];
            for (int i = 0; i < results.Length; i++)
            {
                var r = results[i];
                items[i] = new ItemStateChanged(
                    r.ItemName,
                    r.ClientHandle?.ToString(),
                    r.Value,
                    r.Quality.QualityBits != qualityBits.bad);
            }
            StateChanged?.Invoke(items);
        }

        internal OpcIntegrationExporter CreateExporter(string prefix, string serverUrl)
            => new OpcIntegrationExporter(_client.Server, prefix, serverUrl);

        public IReadOnlyList<string> GetAvailableServers()                              => _client.GetAvailableServers();
        public bool Connect(string url)                                                  => _client.Connect(url);
        public bool StartMonitoring(Dictionary<string, string> nodeIdMap, int updateRateMs) => _client.StartIntegrationMonitoring(nodeIdMap, updateRateMs);
        public bool WriteCommand(string commandXml)                                      => _client.WriteCommand(commandXml);
        public void Dispose()                                                            => _client.Dispose();
    }
}
