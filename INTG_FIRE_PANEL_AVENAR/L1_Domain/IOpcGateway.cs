using System;
using System.Collections.Generic;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;

namespace INTG_FIRE_PANEL_AVENAR.Domain
{
    public interface IOpcGateway : IDisposable
    {
        event Action<ItemState[]> StateChanged;

        IReadOnlyList<string> GetAvailableServers();
        bool Connect(string url);
        bool StartMonitoring(Dictionary<string, string> nodeIdMap, int updateRateMs);
        bool WriteCommand(string commandXml);
    }
}
