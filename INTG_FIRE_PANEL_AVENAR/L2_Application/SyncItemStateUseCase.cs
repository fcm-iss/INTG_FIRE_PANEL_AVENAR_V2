using INTG_FIRE_PANEL_AVENAR.Domain;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
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

        public void Execute(ItemState[] changes)
        {
            foreach (var change in changes)
                ProcessChange(change);
        }

        private void ProcessChange(ItemState change)
        {
            if (!change.IsValid)
                Console.WriteLine($"[PANEL]\t\tDesconectado — ítem: {change.ItemName}");
            else
                ForwardToSecurOs(change);
        }

        private void ForwardToSecurOs(ItemState change)
        {
            Console.WriteLine($"[PANEL]\t\tCambio: {change.ItemName} - {change.NodeId} = {change.Value}");
            _securOs.SendItemState(change.NodeId, change.Value);
        }
    }
}
