using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
using System;
using System.Collections.Generic;

namespace INTG_FIRE_PANEL_AVENAR.Application
{
    /// <summary>
    /// Compara dos árboles OPC exportados (el activo y uno recién leído) usando la misma
    /// definición de objetos que se crea en SecurOS: clave objType|objId, nombre = propiedad 101 o itemName.
    /// </summary>
    public class DetectOpcTreeChangesUseCase
    {
        private readonly string _prefix;

        public DetectOpcTreeChangesUseCase(string prefix)
        {
            _prefix = prefix;
        }

        public OpcTreeChanges Execute(string currentTreePath, string refreshedTreePath)
        {
            var current   = LoadIndexedDefinitions(currentTreePath);
            var refreshed = LoadIndexedDefinitions(refreshedTreePath);

            var changes = new OpcTreeChanges(
                FindMissingIn(current, refreshed),
                FindMissingIn(refreshed, current),
                FindRenamed(current, refreshed));

            LogChanges(changes);
            return changes;
        }

        private Dictionary<string, SecurOsObjectDefinition> LoadIndexedDefinitions(string treePath)
        {
            var index = new Dictionary<string, SecurOsObjectDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in OpcTreeReader.ExtractObjectDefinitionsFromTree(treePath, _prefix))
                index[BuildKey(def)] = def;
            return index;
        }

        private static string BuildKey(SecurOsObjectDefinition def) => $"{def.ObjType}|{def.ObjId}";

        private static List<SecurOsObjectDefinition> FindMissingIn(Dictionary<string, SecurOsObjectDefinition> reference,
                                                                   Dictionary<string, SecurOsObjectDefinition> source)
        {
            var missing = new List<SecurOsObjectDefinition>();
            foreach (var kv in source)
                if (!reference.ContainsKey(kv.Key))
                    missing.Add(kv.Value);
            return missing;
        }

        private static List<RenamedObject> FindRenamed(Dictionary<string, SecurOsObjectDefinition> current,
                                                       Dictionary<string, SecurOsObjectDefinition> refreshed)
        {
            var renamed = new List<RenamedObject>();
            foreach (var kv in refreshed)
            {
                SecurOsObjectDefinition before;
                if (!current.TryGetValue(kv.Key, out before)) continue;
                if (string.Equals(before.Name, kv.Value.Name, StringComparison.Ordinal)) continue;
                renamed.Add(new RenamedObject(kv.Value.ObjType, kv.Value.ObjId, before.Name, kv.Value.Name));
            }
            return renamed;
        }

        #region Log

        private static void LogChanges(OpcTreeChanges changes)
        {
            if (!changes.HasChanges)
            {
                Console.WriteLine("[OPC_Diff]\tSin cambios respecto al árbol activo.");
                return;
            }

            Console.WriteLine($"[OPC_Diff]\tNuevos: {changes.Added.Count}  Eliminados: {changes.Removed.Count}  Renombrados: {changes.Renamed.Count}");
            LogDefinitions("+ Nuevo",     changes.Added);
            LogDefinitions("- Eliminado", changes.Removed);
            LogRenamed(changes.Renamed);
        }

        private static void LogDefinitions(string label, List<SecurOsObjectDefinition> definitions)
        {
            foreach (var def in definitions)
                Console.WriteLine($"[OPC_Diff]\t{label}: {def.ObjType}|{def.ObjId} '{def.Name}'");
        }

        private static void LogRenamed(List<RenamedObject> renamed)
        {
            foreach (var r in renamed)
                Console.WriteLine($"[OPC_Diff]\t~ Renombrado: {r.ObjType}|{r.ObjId} '{r.OldName}' -> '{r.NewName}'");
        }

        #endregion
    }
}
