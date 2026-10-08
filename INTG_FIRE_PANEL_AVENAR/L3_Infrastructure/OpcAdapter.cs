using INTG_OPC;
using INTG_FIRE_PANEL_AVENAR.Application;
using INTG_FIRE_PANEL_AVENAR.Domain;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
using Opc.Da;
using System;
using System.Collections.Generic;
using System.Configuration;

namespace INTG_FIRE_PANEL_AVENAR.Infrastructure
{
    public class OpcAdapter : IOpcGateway
    {
        private readonly OpcDaClient _opcDaClient;

        public event Action<ItemState[]> StateChanged;

        public OpcAdapter()
        {
            _opcDaClient = new OpcDaClient();
            _opcDaClient.PanelChanged += MapAndRaise;
        }

        private void MapAndRaise(ItemValueResult[] results)
        {
            var items = new ItemState[results.Length];
            for (int i = 0; i < results.Length; i++)
            {
                var r = results[i];
                items[i] = new ItemState(
                    r.ItemName,
                    r.ClientHandle?.ToString(),
                    r.Value,
                    r.Quality.QualityBits != qualityBits.bad);
            }
            StateChanged?.Invoke(items);
        }

        internal SecurOsFileExporter BuildSecurOsFileExporter(string prefix, string serverUrl)
        {
            var idUinP = ConfigurationManager.AppSettings["idUinP"] ?? "";
            var tree   = _opcDaClient.BuildTree(prefix, serverUrl, idUinP);
            return new SecurOsFileExporter(tree);
        }

        public IReadOnlyList<string> GetAvailableServers()                              => _opcDaClient.GetAvailableServers(ResolveOpcHost());
        public bool Connect(string url)                                                  => _opcDaClient.Connect(url);

        private static string ResolveOpcHost() => ConfigurationManager.AppSettings["opc_host"] ?? "localhost";
        public bool StartMonitoring(Dictionary<string, string> nodeIdMap, int updateRateMs) => _opcDaClient.StartIntegrationMonitoring(nodeIdMap, updateRateMs);
        public bool RestartMonitoring(Dictionary<string, string> nodeIdMap, int updateRateMs) => _opcDaClient.RestartIntegrationMonitoring(nodeIdMap, updateRateMs);
        public bool WriteCommand(string commandXml)                                      =>_opcDaClient.WriteCommand(commandXml);
        public void Dispose()                                                            => _opcDaClient.Dispose();
    }
}
