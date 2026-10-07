using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace INTG_FIRE_PANEL_AVENAR.Application
{
    /// <summary>
    /// Lee los tipos de objeto de un .integration.json: cada entrada de "structure" es un tipo panel
    /// (cuelga de "parent", p.ej. INTEGRATION) y sus "children" son los tipos que cuelgan de ese panel.
    /// </summary>
    static class IntegrationStructureReader
    {
        public static List<SecurOsObjectType> ReadObjectTypes(string integrationJsonPath)
        {
            var root  = ReadRoot(integrationJsonPath);
            var types = new List<SecurOsObjectType>();

            foreach (var panel in ReadEntries(root, "structure"))
            {
                var panelType = OpcTreeReader.ReadStringField(panel, "type");
                types.Add(new SecurOsObjectType(panelType, OpcTreeReader.ReadStringField(panel, "parent"), isPanel: true));
                AddChildTypes(panel, panelType, types);
            }
            return types;
        }

        private static void AddChildTypes(Dictionary<string, object> panel, string panelType, List<SecurOsObjectType> types)
        {
            foreach (var child in ReadEntries(panel, "children"))
                types.Add(new SecurOsObjectType(OpcTreeReader.ReadStringField(child, "type"), panelType, isPanel: false));
        }

        private static Dictionary<string, object> ReadRoot(string path)
        {
            var json = File.ReadAllText(path, new UTF8Encoding(false));
            return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        }

        private static IEnumerable<Dictionary<string, object>> ReadEntries(Dictionary<string, object> node, string key)
        {
            object value;
            if (!node.TryGetValue(key, out value) || !(value is IEnumerable entries)) yield break;

            foreach (var entry in entries)
                if (entry is Dictionary<string, object> dict)
                    yield return dict;
        }
    }
}
