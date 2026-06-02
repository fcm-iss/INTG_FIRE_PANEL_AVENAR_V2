using System;

namespace INTG_OPC_AVENAR_NET4_5.Domain
{
    public interface ISecurOsGateway : IDisposable
    {
        event Action<SecurOsCommand> CommandReceived;

        void SendItemState(string nodeId, object rawOpcValue);
    }
}
