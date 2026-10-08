using INTG_FIRE_PANEL_AVENAR.Domain;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace INTG_FIRE_PANEL_AVENAR.Application
{
    /// <summary>
    /// Aplica en SecurOS los cambios del árbol OPC: DELETE de "removed", CREATE de "added" y UPDATE (nombre)
    /// de "renamed". El orden respeta la jerarquía panel → ítem: primero se elimina (ítems antes que paneles,
    /// y antes de crear porque un objid puede volver con otro tipo), luego se crea (paneles antes que ítems,
    /// porque el ítem necesita su padre) y al final se renombra.
    /// </summary>
    public class ApplyOpcTreeChangesUseCase
    {
        private static readonly char[] IidkDelimiters = { '<', '>', ',' };

        private readonly ISecurOsGateway _securOs;
        private readonly string          _integrationParentId;

        public ApplyOpcTreeChangesUseCase(ISecurOsGateway securOs, string integrationParentId)
        {
            _securOs             = securOs;
            _integrationParentId = integrationParentId;
        }

        public void Execute(OpcTreeChanges changes)
        {
            DeleteRemoved(changes.Removed).Print(simulated: false);
            CreateAdded(changes.Added).Print(simulated: false);
            UpdateRenamed(changes.Renamed).Print(simulated: false);
        }

        public ReturnCodeTally DeleteRemoved(List<SecurOsObjectDefinition> removed)
        {
            var results = new ReturnCodeTally("DELETE_OBJECT");
            foreach (var def in ItemsFirst(removed))
                results.Record(_securOs.DeleteObject(def.ObjType, def.ObjId));
            return results;
        }

        public ReturnCodeTally CreateAdded(List<SecurOsObjectDefinition> added)
        {
            var results = new ReturnCodeTally("CREATE_OBJECT");
            foreach (var def in PanelsFirst(added))
                results.Record(_securOs.CreateObject(def.ObjType, def.ObjId, SanitizeName(def.Name), ResolveParentId(def)));
            return results;
        }

        public ReturnCodeTally UpdateRenamed(List<RenamedObject> renamed)
        {
            var results = new ReturnCodeTally("UPDATE_OBJECT");
            foreach (var obj in renamed)
                results.Record(_securOs.UpdateObject(obj.ObjType, obj.ObjId, SanitizeName(obj.NewName), parentId: null));
            return results;
        }

        private static IEnumerable<SecurOsObjectDefinition> ItemsFirst(List<SecurOsObjectDefinition> definitions)
            => definitions.Where(d => !d.IsPanel).Concat(definitions.Where(d => d.IsPanel));

        private static IEnumerable<SecurOsObjectDefinition> PanelsFirst(List<SecurOsObjectDefinition> definitions)
            => definitions.Where(d => d.IsPanel).Concat(definitions.Where(d => !d.IsPanel));

        // Un panel cuelga del punto de integración (idUinP); un ítem, del objid de su panel.
        private string ResolveParentId(SecurOsObjectDefinition def)
            => def.IsPanel ? _integrationParentId : def.ParentId;

        // '<', '>' y ',' delimitan los parámetros del mensaje IIDK: se reemplazan por espacio.
        private static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var parts = name.Split(IidkDelimiters);
            return string.Join(" ", parts.Select(p => p.Trim()).Where(p => p.Length > 0));
        }
    }

    /// <summary>Cuenta cuántos comandos de una acción devolvieron cada rc de IidkSendMsg.</summary>
    public class ReturnCodeTally
    {
        private readonly string                     _action;
        private readonly SortedDictionary<int, int> _countByCode = new SortedDictionary<int, int>();
        private int                                 _total;

        public ReturnCodeTally(string action)
        {
            _action = action;
        }

        public void Record(int returnCode)
        {
            int count;
            _countByCode.TryGetValue(returnCode, out count);
            _countByCode[returnCode] = count + 1;
            _total++;
        }

        public void Print(bool simulated)
        {
            if (simulated)
            {
                Console.WriteLine($"[SecurOS]\t{_action}: {_total} mensajes simulados.");
                return;
            }

            var detail = string.Join(", ", _countByCode.Select(kv => $"rc={kv.Key}: {kv.Value}"));
            Console.WriteLine($"[SecurOS]\t{_action}: {_total} enviados ({(_total == 0 ? "ninguno" : detail)}).");
        }
    }
}
