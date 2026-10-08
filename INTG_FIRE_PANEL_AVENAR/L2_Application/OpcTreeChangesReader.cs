using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
using System;
using System.Collections;
using System.Collections.Generic;

namespace INTG_FIRE_PANEL_AVENAR.Application
{
    /// <summary>
    /// Lee el reporte OPCTree.changes.json que genera la relectura del árbol OPC (RESET):
    /// "added" y "removed" son definiciones de objetos SecurOS y "renamed" los cambios de nombre.
    /// </summary>
    static class OpcTreeChangesReader
    {
        public static OpcTreeChanges Read(string changesJsonPath, out string createdAt)
        {
            var root = OpcTreeReader.DeserializeJsonRootNode(OpcTreeReader.ReadJsonFileContent(changesJsonPath));
            createdAt = OpcTreeReader.ReadStringField(root, "createdAt");

            return new OpcTreeChanges(
                ReadDefinitions(root, "added"),
                ReadDefinitions(root, "removed"),
                ReadRenamed(root));
        }

        private static List<SecurOsObjectDefinition> ReadDefinitions(Dictionary<string, object> root, string key)
        {
            var definitions = new List<SecurOsObjectDefinition>();
            foreach (var entry in ReadEntries(root, key))
                definitions.Add(new SecurOsObjectDefinition(
                    OpcTreeReader.ReadStringField(entry, "ObjType"),
                    OpcTreeReader.ReadStringField(entry, "ObjId"),
                    OpcTreeReader.ReadStringField(entry, "Name"),
                    OpcTreeReader.ReadStringField(entry, "ParentId"),
                    ReadBool(entry, "IsPanel")));
            return definitions;
        }

        private static List<RenamedObject> ReadRenamed(Dictionary<string, object> root)
        {
            var renamed = new List<RenamedObject>();
            foreach (var entry in ReadEntries(root, "renamed"))
                renamed.Add(new RenamedObject(
                    OpcTreeReader.ReadStringField(entry, "ObjType"),
                    OpcTreeReader.ReadStringField(entry, "ObjId"),
                    OpcTreeReader.ReadStringField(entry, "OldName"),
                    OpcTreeReader.ReadStringField(entry, "NewName")));
            return renamed;
        }

        private static IEnumerable<Dictionary<string, object>> ReadEntries(Dictionary<string, object> root, string key)
        {
            object value;
            if (!root.TryGetValue(key, out value) || !(value is IEnumerable entries)) yield break;

            foreach (var entry in entries)
                if (entry is Dictionary<string, object> dict)
                    yield return dict;
        }

        private static bool ReadBool(Dictionary<string, object> entry, string key)
        {
            object value;
            return entry.TryGetValue(key, out value) && Convert.ToBoolean(value);
        }
    }
}
