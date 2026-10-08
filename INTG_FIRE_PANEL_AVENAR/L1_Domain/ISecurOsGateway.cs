using System;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;

namespace INTG_FIRE_PANEL_AVENAR.Domain
{
    public interface ISecurOsGateway : IDisposable
    {
        event Action<SecurOsCommand> CommandReceived;
        event Action                 ResetRequested;

        void SendItemState(string nodeId, object rawOpcValue);

        // Comandos CORE sobre objetos; devuelven el rc de IidkSendMsg.
        int CreateObject(string objType, string objId, string name, string parentId);
        int UpdateObject(string objType, string objId, string name, string parentId);
        int DeleteObject(string objType, string objId);
    }
}
