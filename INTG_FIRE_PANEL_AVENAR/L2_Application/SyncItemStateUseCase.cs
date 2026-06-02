using INTG_FIRE_PANEL_AVENAR.Domain;
using System;

namespace INTG_FIRE_PANEL_AVENAR.Application
{
    public class SyncItemStateUseCase
    {
        private readonly ISecurOsGateway _securOs;

        public SyncItemStateUseCase(ISecurOsGateway securOs)
        {
            _securOs = securOs;
        }

        public void Execute(ItemStateChanged[] changes)
        {
            foreach (var change in changes)
                ProcessChange(change);
        }

        private void ProcessChange(ItemStateChanged change)
        {
            if (!change.IsValid)
                Console.WriteLine($"[PANEL]\t\tDesconectado — ítem: {change.ItemName}");
            else
                ForwardToSecurOs(change);
        }

        private void ForwardToSecurOs(ItemStateChanged change)
        {
            Console.WriteLine($"[PANEL]\t\tCambio: {change.ItemName} - {change.NodeId} = {change.Value}");
            _securOs.SendItemState(change.NodeId, change.Value);
        }
    }
}
