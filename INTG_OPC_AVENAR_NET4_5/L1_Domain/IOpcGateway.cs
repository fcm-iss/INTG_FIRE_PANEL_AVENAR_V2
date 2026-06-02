using System;
using System.Collections.Generic;

namespace INTG_OPC_AVENAR_NET4_5.Domain
{
    public interface IOpcGateway : IDisposable
    {
        event Action<ItemStateChanged[]> StateChanged;

        IReadOnlyList<string> GetAvailableServers();
        bool Connect(string url);
        bool StartMonitoring(Dictionary<string, string> nodeIdMap, int updateRateMs);
        bool WriteCommand(string commandXml);
    }
}
