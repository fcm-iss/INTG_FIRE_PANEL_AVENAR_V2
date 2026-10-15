using System;
using System.Collections.Generic;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;

namespace INTG_FIRE_PANEL_AVENAR.Domain
{
    public interface ISecurOsGateway : IDisposable
    {
        event Action<SecurOsCommand> CommandReceived;
        event Action                 ResetRequested;

        void SendItemState(string nodeId, object rawOpcValue);

        // Respuesta al INTEGRATION RESET: "<tipo raíz>|<id del RESET>|RESET|{"version":1,"objects":[...]}".
        void SendResetResponse(List<SecurOsObjectDefinition> objects);

        // Comandos CORE sobre objetos; devuelven el rc de IidkSendMsg.
        int CreateObject(string objType, string objId, string name, string parentId);
        int UpdateObject(string objType, string objId, string name, string parentId);
        int DeleteObject(string objType, string objId);
    }
}
