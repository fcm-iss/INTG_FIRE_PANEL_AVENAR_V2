using INTG_OPC_AVENAR_NET4_5.Domain;
using INTG_OPC_AVENAR_NET4_5.Models;
using System;

namespace INTG_OPC_AVENAR_NET4_5.Infrastructure
{
    public class IidkAdapter : ISecurOsGateway
    {
        private readonly SecurOsConnector _connector;

        public event Action<SecurOsCommand> CommandReceived;

        private IidkAdapter(SecurOsConnector connector)
        {
            _connector = connector;
            _connector.MessageReceived += MapAndRaise;
        }

        public static IidkAdapter Connect()
            => new IidkAdapter(SecurOsConnector.Connect());

        private void MapAndRaise(SecurOsMessage msg)
            => CommandReceived?.Invoke(new SecurOsCommand(msg.ObjType, msg.Id, msg.EventName, msg.Params));

        public void SendItemState(string nodeId, object rawOpcValue)
            => _connector.SendState(nodeId, rawOpcValue);

        public void Dispose() => _connector.Dispose();
    }
}
