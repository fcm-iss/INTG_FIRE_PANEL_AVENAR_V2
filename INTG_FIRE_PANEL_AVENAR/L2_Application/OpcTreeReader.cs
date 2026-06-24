using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace INTG_FIRE_PANEL_AVENAR.Application
{
    static class OpcTreeReader
    {
        #region Mapa ItemName - Id para Suscripcion

        public static Dictionary<string, string> BuildNodeIdMapFromTreeJson(string jsonFilePath)
        {
            var json     = ReadJsonFileContent(jsonFilePath);
            var rootNode = DeserializeJsonRootNode(json);
            var map      = CollectItemIdPairsFromNode(rootNode);
            Console.WriteLine($"[OPC_Exporter]\t\tMapa ItemName->id extraido del JSON: {map.Count} items.");
            return map;
        }

        private static Dictionary<string, string> CollectItemIdPairsFromNode(Dictionary<string, object> rootNode)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            TraverseNode(rootNode, map);
            return map;
        }

        private static void TraverseNode(Dictionary<string, object> node, Dictionary<string, string> map)
        {
            if (node == null) return;
            var itemName  = ReadStringField(node, "itemName");
            var securosId = ReadStringField(node, "securosId");
            var objType   = ReadStringField(node, "objType");
            RegisterPairIfValid(itemName, securosId, objType, map);
            VisitChildNodes(node, map);
        }

        private static void RegisterPairIfValid(string itemName, string securosId, string objType,
                                                Dictionary<string, string> map)
        {
            if (string.IsNullOrEmpty(itemName) || string.IsNullOrEmpty(securosId)) return;
            var clientHandle = string.IsNullOrEmpty(objType) ? securosId : $"{objType}|{securosId}";
            map[itemName] = clientHandle;
        }

        private static void VisitChildNodes(Dictionary<string, object> node, Dictionary<string, string> map)
        {
            object childrenObj;
            if (!node.TryGetValue("children", out childrenObj)) return;
            var children = childrenObj as System.Collections.ArrayList;
            if (children == null) return;
            foreach (var child in children)
                TraverseNode(child as Dictionary<string, object>, map);
        }

        public static string ReadServerUrlFromTreeJson(string jsonFilePath)
        {
            var json     = ReadJsonFileContent(jsonFilePath);
            var rootNode = DeserializeJsonRootNode(json);
            return ReadStringField(rootNode, "server");
        }

        public static Dictionary<string, (string itemName, Dictionary<string, string> commands)> BuildCommandNodeMapFromTreeJson(string jsonFilePath)
        {
            var json     = ReadJsonFileContent(jsonFilePath);
            var rootNode = DeserializeJsonRootNode(json);
            var map      = new Dictionary<string, (string itemName, Dictionary<string, string> commands)>(StringComparer.OrdinalIgnoreCase);
            CollectCommandNodes(rootNode, map);
            Console.WriteLine($"[OPC_Exporter]\t\tMapa handle->comandos: {map.Count} items con comandos.");
            return map;
        }

        private static void CollectCommandNodes(Dictionary<string, object> node,
            Dictionary<string, (string itemName, Dictionary<string, string> commands)> map)
        {
            if (node == null) return;
            RegisterCommandNodeIfValid(node, map);
            DescendIntoChildren(node, map);
        }

        private static void RegisterCommandNodeIfValid(Dictionary<string, object> node,
            Dictionary<string, (string itemName, Dictionary<string, string> commands)> map)
        {
            var itemName  = ReadStringField(node, "itemName");
            var securosId = ReadStringField(node, "securosId");
            var objType   = ReadStringField(node, "objType");
            var commands  = ExtractCommands(node);
            if (string.IsNullOrEmpty(itemName) || string.IsNullOrEmpty(securosId) || commands.Count == 0)
                return;
            var securosKey = string.IsNullOrEmpty(objType) ? securosId : $"{objType}|{securosId}";
            map[securosKey] = (itemName, commands);
        }

        private static Dictionary<string, string> ExtractCommands(Dictionary<string, object> node)
        {
            object commandsObj;
            if (!node.TryGetValue("commands", out commandsObj)) return new Dictionary<string, string>();
            var raw = commandsObj as Dictionary<string, object>;
            if (raw == null) return new Dictionary<string, string>();
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in raw)
                result[kv.Key] = kv.Value?.ToString() ?? "";
            return result;
        }

        private static void DescendIntoChildren(Dictionary<string, object> node,
            Dictionary<string, (string itemName, Dictionary<string, string> commands)> map)
        {
            object childrenObj;
            if (!node.TryGetValue("children", out childrenObj)) return;
            var children = childrenObj as System.Collections.ArrayList;
            if (children == null) return;
            foreach (var child in children)
                CollectCommandNodes(child as Dictionary<string, object>, map);
        }

        #endregion

        #region Creacion de objetos en SecurOS

        public static (string prefix, string idUinP) ReadRootConfig(string treeJsonPath)
        {
            var root = DeserializeJsonRootNode(ReadJsonFileContent(treeJsonPath));
            return (ReadStringField(root, "prefix"), ReadStringField(root, "idUinP"));
        }

        public static List<SecurOsObjectDefinition> ExtractObjectDefinitionsFromTree(string treeJsonPath)
        {
            var json     = ReadJsonFileContent(treeJsonPath);
            var rootNode = DeserializeJsonRootNode(json);
            var definitions    = new List<SecurOsObjectDefinition>();
            foreach (var panelNode in GetChildNodes(rootNode))
            {
                var panelId   = ReadStringField(panelNode, "securosId");
                var panelType = ReadStringField(panelNode, "objType");
                if (string.IsNullOrEmpty(panelId) || string.IsNullOrEmpty(panelType)) continue;
                var panelItemName = ReadStringField(panelNode, "itemName");
                definitions.Add(new SecurOsObjectDefinition(panelType, panelId, panelItemName, parentId: "", isPanel: true));
                CollectItemObjectDefinitions(panelNode, definitions);
            }
            return definitions;
        }

        public static List<SecurOsObjectDefinition> ExtractObjectDefinitionsFromTree(string treeJsonPath, string prefix)
        {
            var json     = ReadJsonFileContent(treeJsonPath);
            var rootNode = DeserializeJsonRootNode(json);
            var definitions    = new List<SecurOsObjectDefinition>();
            foreach (var panelNode in GetChildNodes(rootNode))
            {
                var panelId = ReadStringField(panelNode, "securosId");
                if (string.IsNullOrEmpty(panelId)) continue;
                var panelName     = ReadStringField(panelNode, "name");
                var panelItemName = ReadStringField(panelNode, "itemName");
                var panelType     = prefix + StripDigits(Normalize(panelName));
                definitions.Add(new SecurOsObjectDefinition(panelType, panelId, panelItemName, parentId: "", isPanel: true));
                CollectItemObjectDefinitions(panelNode, definitions);
            }
            return definitions;
        }

        private static void CollectItemObjectDefinitions(Dictionary<string, object> node, List<SecurOsObjectDefinition> definitions)
        {
            foreach (var child in GetChildNodes(node))
            {
                var elementType = ReadStringField(child, "elementType");
                var isItem      = elementType == "item" || elementType == "branch|item";
                if (isItem)
                {
                    var objType   = ReadStringField(child, "objType");
                    var securosId = ReadStringField(child, "securosId");
                    var itemName  = ReadPropertyValue(child, 101) ?? ReadStringField(child, "itemName");
                    var parentId  = ReadStringField(child, "securosParentId");
                    if (!string.IsNullOrEmpty(objType) && !string.IsNullOrEmpty(securosId))
                        definitions.Add(new SecurOsObjectDefinition(objType, securosId, itemName, parentId));
                }
                if (elementType != "item")
                    CollectItemObjectDefinitions(child, definitions);
            }
        }

        #endregion

        #region Helpers internos compartidos

        internal static string ReadJsonFileContent(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Archivo de arbol OPC no encontrado: {path}");
            return File.ReadAllText(path, new UTF8Encoding(false));
        }

        internal static Dictionary<string, object> DeserializeJsonRootNode(string json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return serializer.Deserialize<Dictionary<string, object>>(json);
        }

        internal static string ReadStringField(Dictionary<string, object> node, string fieldName)
        {
            object value;
            return node.TryGetValue(fieldName, out value) ? value?.ToString() ?? "" : "";
        }

        internal static IEnumerable<Dictionary<string, object>> GetChildNodes(Dictionary<string, object> node)
        {
            object childrenObj;
            if (!node.TryGetValue("children", out childrenObj)) yield break;
            var children = childrenObj as System.Collections.ArrayList;
            if (children == null) yield break;
            foreach (var child in children)
            {
                var d = child as Dictionary<string, object>;
                if (d != null) yield return d;
            }
        }

        #endregion

        #region Helpers privados

        private static string Normalize(string value) =>
            value?.ToUpperInvariant().Replace(' ', '_') ?? string.Empty;

        private static string ReadPropertyValue(Dictionary<string, object> node, int code)
        {
            object propsObj;
            if (!node.TryGetValue("properties", out propsObj)) return null;
            var props = propsObj as System.Collections.ArrayList;
            if (props == null) return null;
            foreach (var prop in props)
            {
                var p = prop as Dictionary<string, object>;
                if (p == null) continue;
                object codeVal;
                if (!p.TryGetValue("code", out codeVal)) continue;
                int propCode;
                if (!int.TryParse(codeVal?.ToString(), out propCode) || propCode != code) continue;
                object valObj;
                return p.TryGetValue("value", out valObj) ? valObj?.ToString() ?? "" : "";
            }
            return null;
        }

        private static string StripDigits(string value)
        {
            var sb = new StringBuilder();
            foreach (char c in value)
                if (!char.IsDigit(c)) sb.Append(c);
            return sb.ToString().Trim(' ', '-', '_');
        }

        #endregion
    }
}