using System;

namespace INTG_FIRE_PANEL_AVENAR.Domain
{
    public interface ISecurOsGateway : IDisposable
    {
        event Action<SecurOsCommand> CommandReceived;

        void SendItemState(string nodeId, object rawOpcValue);
    }
}
