using System;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;

namespace INTG_FIRE_PANEL_AVENAR.Domain
{
    public interface ISecurOsGateway : IDisposable
    {
        event Action<SecurOsCommand> CommandReceived;
        event Action                 ResetRequested;

        void SendItemState(string nodeId, object rawOpcValue);
    }
}
