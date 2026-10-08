using INTG_FIRE_PANEL_AVENAR.Application;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace INTG_FIRE_PANEL_AVENAR.Flows
{
    /// <summary>
    /// Modo test: aplica en SecurOS el reporte OPCTree.changes.json con resumen previo, elección de alcance,
    /// simulación y confirmación. El envío y su orden los resuelve ApplyOpcTreeChangesUseCase.
    /// </summary>
    internal class ApplyOpcTreeChangesFlow
    {
        private enum Scope { All, DeleteOnly, CreateOnly, UpdateOnly }

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
            var useCase = new ApplyOpcTreeChangesUseCase(securos, _integrationParentId);
            if (scope == Scope.All || scope == Scope.DeleteOnly) useCase.DeleteRemoved(changes.Removed).Print(securos.DryRun);
            if (scope == Scope.All || scope == Scope.CreateOnly) useCase.CreateAdded(changes.Added).Print(securos.DryRun);
            if (scope == Scope.All || scope == Scope.UpdateOnly) useCase.UpdateRenamed(changes.Renamed).Print(securos.DryRun);
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
    }
}
