using INTG_FIRE_PANEL_AVENAR.Application;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace INTG_FIRE_PANEL_AVENAR.Flows
{
    /// <summary>
    /// Aplica en SecurOS el reporte OPCTree.changes.json: DELETE de "removed", CREATE de "added"
    /// y UPDATE (nombre) de "renamed". El orden respeta la jerarquía panel → ítem:
    /// primero se elimina (ítems antes que paneles, y antes de crear porque un objid puede volver con otro tipo),
    /// luego se crea (paneles antes que ítems, porque el ítem necesita su padre) y al final se renombra.
    /// </summary>
    internal class ApplyOpcTreeChangesFlow
    {
        private enum Scope { All, DeleteOnly, CreateOnly, UpdateOnly }

        private static readonly char[] IidkDelimiters = { '<', '>', ',' };

        private readonly string _changesJsonPath;
        private readonly string _integrationParentId;

        public ApplyOpcTreeChangesFlow(string changesJsonPath, string integrationParentId)
        {
            _changesJsonPath     = changesJsonPath;
            _integrationParentId = integrationParentId;
        }

        public void Run(SecurOsAdapter securos)
        {
            var changes = LoadChanges();
            if (changes == null || !changes.HasChanges) return;

            Scope scope;
            bool  simulate;
            if (!AskScope(out scope, out simulate)) return;
            if (!simulate && !ConfirmSend(changes, scope)) return;

            securos.DryRun = simulate;
            try
            {
                ApplyChanges(securos, changes, scope);
            }
            finally
            {
                securos.DryRun = false;
            }
        }

        private void ApplyChanges(SecurOsAdapter securos, OpcTreeChanges changes, Scope scope)
        {
            if (scope == Scope.All || scope == Scope.DeleteOnly) DeleteRemoved(securos, changes.Removed);
            if (scope == Scope.All || scope == Scope.CreateOnly) CreateAdded(securos, changes.Added);
            if (scope == Scope.All || scope == Scope.UpdateOnly) UpdateRenamed(securos, changes.Renamed);
        }

        #region Carga y resumen

        private OpcTreeChanges LoadChanges()
        {
            if (!File.Exists(_changesJsonPath))
            {
                Console.WriteLine($"[TEST]\t\tNo se encontró {_changesJsonPath} — ejecute un RESET de la integración para generarlo.");
                return null;
            }

            string createdAt;
            var changes = OpcTreeChangesReader.Read(_changesJsonPath, out createdAt);
            PrintSummary(changes, createdAt);
            return changes;
        }

        private static void PrintSummary(OpcTreeChanges changes, string createdAt)
        {
            Console.WriteLine($"[TEST]\t\tReporte de cambios generado el {createdAt}");
            PrintCountsByType("DELETE_OBJECT (removed)", changes.Removed);
            PrintCountsByType("CREATE_OBJECT (added)",   changes.Added);
            Console.WriteLine($"         UPDATE_OBJECT (renamed): {changes.Renamed.Count}");
            PrintTypeChanges(changes);

            if (!changes.HasChanges)
                Console.WriteLine("[TEST]\t\tEl reporte no tiene cambios.");
        }

        private static void PrintCountsByType(string label, List<SecurOsObjectDefinition> definitions)
        {
            Console.WriteLine($"         {label}: {definitions.Count}");
            foreach (var group in definitions.GroupBy(d => d.ObjType).OrderBy(g => g.Key))
                Console.WriteLine($"             {group.Key,-45} {group.Count(),5}");
        }

        // Un objid que sale con un tipo y entra con otro: por eso se elimina antes de crear.
        private static void PrintTypeChanges(OpcTreeChanges changes)
        {
            var removedById = changes.Removed.GroupBy(d => d.ObjId).ToDictionary(g => g.Key, g => g.First());
            foreach (var added in changes.Added)
            {
                SecurOsObjectDefinition removed;
                if (removedById.TryGetValue(added.ObjId, out removed) && removed.ObjType != added.ObjType)
                    Console.WriteLine($"         Cambio de tipo en objid {added.ObjId}: {removed.ObjType} -> {added.ObjType}");
            }
        }

        #endregion

        #region Menú

        private static bool AskScope(out Scope scope, out bool simulate)
        {
            Console.WriteLine("[TEST]\t\t¿Qué desea aplicar?");
            Console.WriteLine("         1- Todo (DELETE -> CREATE -> UPDATE)");
            Console.WriteLine("         2- Solo DELETE_OBJECT");
            Console.WriteLine("         3- Solo CREATE_OBJECT");
            Console.WriteLine("         4- Solo UPDATE_OBJECT");
            Console.WriteLine("         5- Simular todo (muestra los mensajes sin enviarlos)");
            Console.WriteLine("         0- Cancelar");
            Console.Write("Seleccione una opción: ");

            simulate = false;
            switch (Console.ReadLine()?.Trim())
            {
                case "1": scope = Scope.All;        return true;
                case "2": scope = Scope.DeleteOnly; return true;
                case "3": scope = Scope.CreateOnly; return true;
                case "4": scope = Scope.UpdateOnly; return true;
                case "5": scope = Scope.All; simulate = true; return true;
                default:  scope = Scope.All;        return false;
            }
        }

        private static bool ConfirmSend(OpcTreeChanges changes, Scope scope)
        {
            Console.Write($"[TEST]\t\tSe enviarán {CountCommands(changes, scope)} comandos a SecurOS. ¿Continuar? (S/N): ");
            return string.Equals(Console.ReadLine()?.Trim(), "S", StringComparison.OrdinalIgnoreCase);
        }

        private static int CountCommands(OpcTreeChanges changes, Scope scope)
        {
            switch (scope)
            {
                case Scope.DeleteOnly: return changes.Removed.Count;
                case Scope.CreateOnly: return changes.Added.Count;
                case Scope.UpdateOnly: return changes.Renamed.Count;
                default:               return changes.Removed.Count + changes.Added.Count + changes.Renamed.Count;
            }
        }

        #endregion

        #region Envío

        private static void DeleteRemoved(SecurOsAdapter securos, List<SecurOsObjectDefinition> removed)
        {
            var results = new ReturnCodeTally("DELETE_OBJECT");
            foreach (var def in ItemsFirst(removed))
                results.Record(securos.DeleteObject(def.ObjType, def.ObjId));
            results.Print(securos.DryRun);
        }

        private void CreateAdded(SecurOsAdapter securos, List<SecurOsObjectDefinition> added)
        {
            var results = new ReturnCodeTally("CREATE_OBJECT");
            foreach (var def in PanelsFirst(added))
                results.Record(securos.CreateObject(def.ObjType, def.ObjId, SanitizeName(def.Name), ResolveParentId(def)));
            results.Print(securos.DryRun);
        }

        private static void UpdateRenamed(SecurOsAdapter securos, List<RenamedObject> renamed)
        {
            var results = new ReturnCodeTally("UPDATE_OBJECT");
            foreach (var obj in renamed)
                results.Record(securos.UpdateObject(obj.ObjType, obj.ObjId, SanitizeName(obj.NewName), parentId: null));
            results.Print(securos.DryRun);
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

        #endregion

        /// <summary>Cuenta cuántos comandos devolvieron cada rc de IidkSendMsg.</summary>
        private class ReturnCodeTally
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
                    Console.WriteLine($"[TEST]\t\t{_action}: {_total} mensajes simulados.");
                    return;
                }

                var detail = string.Join(", ", _countByCode.Select(kv => $"rc={kv.Key}: {kv.Value}"));
                Console.WriteLine($"[TEST]\t\t{_action}: {_total} enviados ({(_total == 0 ? "ninguno" : detail)}).");
            }
        }
    }
}
