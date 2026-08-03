using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace INTG_FIRE_PANEL_AVENAR.Application
{
    class SecurOsFileExporter
    {
        #region Campos y Constructor

        private readonly Dictionary<string, object> _tree;
        private readonly string _prefix;
        private readonly string _serverUrl;
        private readonly string _idUinP;

        public SecurOsFileExporter(Dictionary<string, object> tree)
        {
            _tree      = tree;
            _prefix    = OpcTreeReader.ReadStringField(tree, "prefix");
            _serverUrl = OpcTreeReader.ReadStringField(tree, "server");
            _idUinP    = OpcTreeReader.ReadStringField(tree, "idUinP");
        }

        #endregion

        #region Exportacion OPC file

        public void ExportCommandsToJson(string outputPath)
        {
            try
            {
                var cmdNode = OpcTreeReader.GetChildNodes(_tree)
                    .FirstOrDefault(n => string.Equals(OpcTreeReader.ReadStringField(n, "name"), "CMD", StringComparison.OrdinalIgnoreCase));
                var props   = ExtractCmdPropertiesFromNode(cmdNode);
                var json    = SerializeCmdJson(props, _serverUrl, DateTime.Now);
                File.WriteAllText(outputPath, json, new UTF8Encoding(false));
                Console.WriteLine($"[OPC_Exporter]\t\tComandos CMD exportados: {outputPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC_Exporter]\t\tError al exportar comandos CMD: {ex.Message}");
            }
        }

        private static List<CmdProperty> ExtractCmdPropertiesFromNode(Dictionary<string, object> cmdNode)
        {
            if (cmdNode == null) return new List<CmdProperty>();
            var propsRaw = cmdNode.ContainsKey("properties") ? cmdNode["properties"] as List<object> : null;
            if (propsRaw == null) return new List<CmdProperty>();

            var result = new List<CmdProperty>();
            foreach (Dictionary<string, object> prop in propsRaw.OfType<Dictionary<string, object>>())
            {
                object codeObj, descObj;
                prop.TryGetValue("code", out codeObj);
                prop.TryGetValue("description", out descObj);
                var cmdProp = new CmdProperty
                {
                    Code        = codeObj is int ? (int)codeObj : 0,
                    Description = descObj?.ToString() ?? ""
                };
                object cmdsObj;
                if (prop.TryGetValue("commands", out cmdsObj) && cmdsObj is Dictionary<string, object> cmds)
                {
                    cmdProp.IsXml    = true;
                    cmdProp.Commands = cmds.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
                }
                else
                {
                    object valObj;
                    cmdProp.Value = prop.TryGetValue("value", out valObj) ? valObj?.ToString() ?? "" : "";
                }
                result.Add(cmdProp);
            }
            return result;
        }

        private static string SerializeCmdJson(List<CmdProperty> props, string serverUrl, DateTime timestamp)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"createdAt\": \"{timestamp:yyyy-MM-ddTHH:mm:ss}\",");
            sb.AppendLine($"  \"server\": {ToJsonLiteral(serverUrl)},");
            sb.AppendLine("  \"cmd\": [");

            for (int i = 0; i < props.Count; i++)
            {
                var p     = props[i];
                var comma = i < props.Count - 1 ? "," : string.Empty;

                if (p.IsXml)
                {
                    sb.AppendLine("    {");
                    sb.AppendLine($"      \"code\": {p.Code},");
                    sb.AppendLine($"      \"description\": {ToJsonLiteral(p.Description)},");
                    sb.AppendLine("      \"commands\": {");

                    var cmdList = p.Commands.ToList();
                    for (int j = 0; j < cmdList.Count; j++)
                    {
                        var cmdComma = j < cmdList.Count - 1 ? "," : string.Empty;
                        sb.AppendLine($"        {ToJsonLiteral(cmdList[j].Key)}: {ToJsonLiteral(cmdList[j].Value)}{cmdComma}");
                    }

                    sb.AppendLine("      }");
                    sb.AppendLine($"    }}{comma}");
                }
                else
                {
                    sb.AppendLine($"    {{ \"code\": {p.Code}, \"description\": {ToJsonLiteral(p.Description)}, \"value\": {ToJsonLiteral(p.Value)} }}{comma}");
                }
            }

            sb.AppendLine("  ]");
            sb.Append("}");
            return sb.ToString();
        }

        public void ExportOpcTreeWithPropertiesToJson(string outputPath)
        {
            Console.WriteLine("[OPC_Exporter]\t\tExportando arbol OPC...");
            var json = SerializeTreeToJson(_tree);
            WriteJsonToFile(json, outputPath);
            Console.WriteLine("[OPC_Exporter]\t\tTree exported to: " + outputPath);
        }

        private static string SerializeTreeToJson(Dictionary<string, object> rootNode)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return PrettyPrint(serializer.Serialize(rootNode))
                .Replace("\\u003c", "<")
                .Replace("\\u003e", ">")
                .Replace("\\u0026", "&");
        }

        private static void WriteJsonToFile(string json, string outputPath)
        {
            File.WriteAllText(outputPath, json, new UTF8Encoding(false));
        }

        #endregion

        #region Archivos de Integracion

        public void ExportIntegrationJson(string treeJsonPath, string outputPath, ICollection<string> allowedChildTypes = null)
        {
            Console.WriteLine("[OPC_Exporter]\t\tBuilding OPC integration JSON...");

            var rootNode  = LoadTreeFromJsonFile(treeJsonPath);
            var structure = BuildIntegrationStructureFromTree(rootNode, allowedChildTypes);

            var root = new Dictionary<string, object>
            {
                { "version",       1  },
                { "reset_timeout", 60 },
                { "structure",     structure }
            };

            File.WriteAllText(outputPath, FormatIntegrationJson(root), new UTF8Encoding(false));
            Console.WriteLine("[OPC_Exporter]\t\tIntegration JSON exported to: " + outputPath);
        }

        private static Dictionary<string, object> LoadTreeFromJsonFile(string treeJsonPath)
        {
            var json = OpcTreeReader.ReadJsonFileContent(treeJsonPath);
            return OpcTreeReader.DeserializeJsonRootNode(json);
        }

        private static IEnumerable<Dictionary<string, object>> GetTypeNodesFromTree(Dictionary<string, object> rootNode)
        {
            foreach (var panel in OpcTreeReader.GetChildNodes(rootNode))
            {
                if (!IsPanelNode(panel)) continue;
                foreach (var typeNode in OpcTreeReader.GetChildNodes(panel))
                    yield return typeNode;
            }
        }

        private static bool IsPanelNode(Dictionary<string, object> node) =>
            !string.IsNullOrEmpty(OpcTreeReader.ReadStringField(node, "securosId"));

        private static bool IsTypeAllowed(string name, ICollection<string> allowedTypes) =>
            allowedTypes == null || allowedTypes.Count == 0 || allowedTypes.Contains(name);

        private List<object> BuildIntegrationStructureFromTree(
            Dictionary<string, object> rootNode,
            ICollection<string> allowedChildTypes)
        {
            var result     = new List<object>();
            var seenPanels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var panelNode in OpcTreeReader.GetChildNodes(rootNode))
            {
                if (!IsPanelNode(panelNode)) continue;

                string panelName = OpcTreeReader.ReadStringField(panelNode, "name");
                string panelType = _prefix + StripDigits(Normalize(panelName));
                if (!seenPanels.Add(panelType)) continue;

                var children = BuildTypeChildrenFromPanel(panelNode, allowedChildTypes);
                var node     = new Dictionary<string, object>
                {
                    { "parent", "INTEGRATION" },
                    { "type",   panelType }
                };
                if (children.Count > 0)
                    node["children"] = children;

                result.Add(node);
            }

            return result;
        }

        private List<object> BuildTypeChildrenFromPanel(
            Dictionary<string, object> panelNode,
            ICollection<string> allowedChildTypes)
        {
            var result    = new List<object>();
            var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var typeNode in OpcTreeReader.GetChildNodes(panelNode))
            {
                string name = OpcTreeReader.ReadStringField(typeNode, "name");
                if (!IsTypeAllowed(name, allowedChildTypes)) continue;

                string typeName = _prefix + StripDigits(Normalize(name));
                if (!seenTypes.Add(typeName)) continue;

                result.Add(new Dictionary<string, object> { { "type", typeName } });
            }

            return result;
        }

        private static string FormatIntegrationJson(Dictionary<string, object> root)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            WriteIntegrationObject(sb, root, indent: 1);
            sb.Append("}");
            return sb.ToString();
        }

        private static void WriteIntegrationObject(StringBuilder sb, Dictionary<string, object> obj, int indent)
        {
            string pad  = new string('\t', indent);
            var keys    = new List<string>(obj.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                string key   = keys[i];
                object value = obj[key];
                bool   last  = i == keys.Count - 1;

                if (value is List<object> list)
                {
                    sb.AppendLine(pad + "\"" + key + "\":");
                    sb.Append(pad + "[");
                    WriteIntegrationArray(sb, list, indent, last);
                }
                else
                {
                    sb.Append(pad + "\"" + key + "\": " + ToJsonScalar(value));
                    sb.AppendLine(last ? "" : ",");
                }
            }
        }

        private static void WriteIntegrationArray(StringBuilder sb, List<object> list, int indent, bool lastProp)
        {
            string pad = new string('\t', indent);
            for (int i = 0; i < list.Count; i++)
            {
                sb.Append(i > 0 ? "},{" : "{");
                sb.AppendLine();
                WriteIntegrationObject(sb, (Dictionary<string, object>)list[i], indent + 1);
                sb.Append(pad);
            }
            sb.Append("}]");
            if (!lastProp) sb.Append(",");
            sb.AppendLine();
        }

        private static string ToJsonScalar(object value)
        {
            if (value == null)     return "null";
            if (value is string s) return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            if (value is bool b)   return b ? "true" : "false";
            return value.ToString();
        }

        public void ExportDdiFile(string treeJsonPath, string outputPath, ICollection<string> allowedChildTypes = null)
        {
            Console.WriteLine("[OPC_Exporter]\t\tBuilding DDI file...");

            var rootNode  = LoadTreeFromJsonFile(treeJsonPath);
            var typeInfos = CollectTypeInfosFromTree(rootNode, allowedChildTypes);
            var types     = typeInfos.Select(t => t.Item1).ToList();
            var opcStates = LoadOpcStateEntries();

            var cmdEvents      = CollectEventRowsFromTree(rootNode, allowedChildTypes);
            var typesWithGroup = typeInfos.Where(t => !string.IsNullOrEmpty(t.Item3)).Select(t => t.Item1).ToList();
            var opcEventRows   = BuildOpcStateEventRows(typesWithGroup, opcStates);
            var opcStateRows   = BuildOpcStateStateRows(typesWithGroup, opcStates);
            var stateRuleRows  = BuildStateRuleRows(typesWithGroup, opcStates);

            int c1 = ComputeC1Width(types, cmdEvents);
            int c2 = Math.Max(27, typeInfos.Count > 0 ? typeInfos.Max(t => t.Item2.Length) + 2 : 27);
            int c3 = Math.Max(27, typeInfos.Count > 0 ? typeInfos.Max(t => t.Item3.Length) + 2 : 27);
            int c4 = ComputeDescriptionWidth(cmdEvents, opcStates);
            int c5 = c4;
            const int c6 = 16;

            int cs2 = Math.Max(20, opcStateRows.Count > 0 ? opcStateRows.Max(r => r.Item2.Length) + 2 : 20);
            int cs3 = Math.Max(7,  opcStateRows.Count > 0 ? opcStateRows.Max(r => r.Item3.Length) + 2 : 7);
            int cs6 = Math.Max(16, opcStateRows.Count > 0 ? opcStateRows.Max(r => r.Item4.Length) + 2 : 16);

            var sb = new StringBuilder();

            WriteObjTypeSection(sb, typeInfos, c1, c2, c3);
            WriteEventSection(sb, cmdEvents, opcEventRows, c1, c2, c3, c4, c5);
            WriteReactSection(sb, cmdEvents, c1, c2, c3, c4, c5, c6);
            WriteStateSection(sb, opcStateRows, c1, cs2, cs3, c4, c5, cs6);
            WriteMarkerSection(sb);
            WriteStateRuleSection(sb, stateRuleRows, c1, cs2);

            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine("[OPC_Exporter]\t\tDDI file exported to: " + outputPath);
        }

        private static List<Tuple<string, string, string>> BuildOpcStateEventRows(
            List<string> types, List<OpcStateEntry> opcStates)
        {
            var result = new List<Tuple<string, string, string>>();
            foreach (var t in types)
                foreach (var s in opcStates)
                    result.Add(Tuple.Create(t, s.LzName, s.DescriptionEs));
            return result;
        }

        private static List<Tuple<string, string, string, string>> BuildOpcStateStateRows(
            List<string> types, List<OpcStateEntry> opcStates)
        {
            var result = new List<Tuple<string, string, string, string>>();
            foreach (var t in types)
                foreach (var s in opcStates)
                    result.Add(Tuple.Create(t, s.LzName, s.LzName, s.DescriptionEs));
            return result;
        }

        private static List<Tuple<string, string>> BuildStateRuleRows(
            List<string> types, List<OpcStateEntry> opcStates)
        {
            var result = new List<Tuple<string, string>>();
            foreach (var t in types)
                foreach (var s in opcStates)
                    result.Add(Tuple.Create(t, s.LzName));
            return result;
        }

        private static int ComputeC1Width(List<string> types,
                                          List<Tuple<string, string, string>> cmdEvents) =>
            Math.Max(22, Math.Max(
                types.Count     > 0 ? types.Max(t => t.Length)          + 2 : 0,
                cmdEvents.Count > 0 ? cmdEvents.Max(e => e.Item1.Length) + 2 : 0));

        private static int ComputeDescriptionWidth(List<Tuple<string, string, string>> cmdEvents,
                                                   List<OpcStateEntry> opcStates) =>
            Math.Max(20, Math.Max(
                cmdEvents.Count > 0 ? cmdEvents.Max(e => e.Item3.Length)       + 2 : 0,
                opcStates.Count > 0 ? opcStates.Max(s => s.DescriptionEs.Length) + 2 : 0));

        private static void WriteObjTypeSection(StringBuilder sb,
            List<Tuple<string, string, string>> typeInfos, int c1, int c2, int c3)
        {
            sb.AppendLine("[OBJTYPE]");
            foreach (var ti in typeInfos)
                sb.AppendLine(Col(ti.Item1, c1) + "|" + Col(ti.Item2, c2) + "|" + Col(ti.Item3, c3) + "|");
            sb.AppendLine();
        }

        private static void WriteEventSection(StringBuilder sb,
            List<Tuple<string, string, string>> cmdEvents,
            List<Tuple<string, string, string>> opcEventRows,
            int c1, int c2, int c3, int c4, int c5)
        {
            sb.AppendLine("[EVENT]");
            foreach (var ev in cmdEvents)
                sb.AppendLine(Col(ev.Item1, c1) + "|" + Col(ev.Item2, c2) + "|" + Col("", c3) + "|" + Col(ev.Item3, c4) + "|" + Col("", c5) + "|");
            foreach (var ev in opcEventRows)
                sb.AppendLine(Col(ev.Item1, c1) + "|" + Col(ev.Item2, c2) + "|" + Col("", c3) + "|" + Col(ev.Item3, c4) + "|" + Col("", c5) + "|");
            sb.AppendLine();
        }

        private static void WriteReactSection(StringBuilder sb,
            List<Tuple<string, string, string>> cmdEvents,
            int c1, int c2, int c3, int c4, int c5, int c6)
        {
            sb.AppendLine("[REACT]");
            foreach (var ev in cmdEvents)
                sb.AppendLine(Col(ev.Item1, c1) + "|" + Col(ev.Item2, c2) + "|" + Col("", c3) + "|" + Col("", c4) + "|" + Col(ev.Item3, c5) + "|" + Col("", c6) + "|");
            sb.AppendLine();
        }

        private static void WriteStateSection(StringBuilder sb,
            List<Tuple<string, string, string, string>> opcStateRows,
            int c1, int cs2, int cs3, int c4, int c5, int cs6)
        {
            sb.AppendLine("[STATE]");
            foreach (var sr in opcStateRows)
                sb.AppendLine(Col(sr.Item1, c1) + "|" + Col(sr.Item2, cs2) + "|" + Col(sr.Item3, cs3) + "|" + Col("", c4) + "|" + Col("", c5) + "|" + Col(sr.Item4, cs6) + "|");
            sb.AppendLine();
        }

        private static void WriteMarkerSection(StringBuilder sb)
        {
            sb.AppendLine("[MARKER]");
            sb.AppendLine();
        }

        private static void WriteStateRuleSection(StringBuilder sb,
            List<Tuple<string, string>> stateRuleRows, int c1, int cs2)
        {
            sb.AppendLine("[STATE_RULE]");
            foreach (var r in stateRuleRows)
                sb.AppendLine(Col(r.Item1, c1) + "|" + Col(r.Item2, cs2) + "|" + Col("", cs2) + "|" + Col(r.Item2, cs2) + "|");
            sb.AppendLine();
        }

        private List<Tuple<string, string, string>> CollectTypeInfosFromTree(
            Dictionary<string, object> rootNode,
            ICollection<string> allowedChildTypes)
        {
            var result    = new List<Tuple<string, string, string>>();
            var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            CollectPanelTypeInfos(rootNode, result, seenTypes);
            CollectChildTypeInfos(rootNode, allowedChildTypes, result, seenTypes);

            return result;
        }

        private void CollectPanelTypeInfos(Dictionary<string, object> rootNode,
                                           List<Tuple<string, string, string>> result,
                                           HashSet<string> seenTypes)
        {
            var prefixClean = _prefix.TrimEnd('_');

            foreach (var panelNode in OpcTreeReader.GetChildNodes(rootNode))
            {
                if (!IsPanelNode(panelNode)) continue;

                string panelName = OpcTreeReader.ReadStringField(panelNode, "name");
                string objType   = _prefix + StripDigits(Normalize(panelName));
                if (!seenTypes.Add(objType)) continue;

                result.Add(Tuple.Create(objType, prefixClean + " " + StripDigits(panelName), ""));
            }
        }

        private void CollectChildTypeInfos(Dictionary<string, object> rootNode,
                                           ICollection<string> allowedChildTypes,
                                           List<Tuple<string, string, string>> result,
                                           HashSet<string> seenTypes)
        {
            var prefixClean = _prefix.TrimEnd('_');

            foreach (var typeNode in GetTypeNodesFromTree(rootNode))
            {
                string name = OpcTreeReader.ReadStringField(typeNode, "name");
                if (!IsTypeAllowed(name, allowedChildTypes)) continue;

                string objType = OpcTreeReader.ReadStringField(typeNode, "objType");
                if (string.IsNullOrEmpty(objType) || !seenTypes.Add(objType)) continue;

                string typeName  = OpcTreeReader.ReadStringField(typeNode, "typeName");
                string groupName = OpcTreeReader.ReadStringField(typeNode, "groupName");
                result.Add(Tuple.Create(objType, prefixClean + " " + typeName, prefixClean + " " + groupName));
            }
        }

        private List<Tuple<string, string, string>> CollectEventRowsFromTree(
            Dictionary<string, object> rootNode,
            ICollection<string> allowedChildTypes)
        {
            var events    = new List<Tuple<string, string, string>>();
            var doneTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var typeNode in GetTypeNodesFromTree(rootNode))
            {
                string name = OpcTreeReader.ReadStringField(typeNode, "name");
                if (!IsTypeAllowed(name, allowedChildTypes)) continue;

                string objType = _prefix + StripDigits(Normalize(name));
                if (!doneTypes.Add(objType)) continue;

                var uniqueCommands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                CollectUniqueCommandsFromDescendants(typeNode, uniqueCommands);

                foreach (var kv in uniqueCommands)
                    foreach (var cmd in ParseCommands(kv.Value))
                        events.Add(Tuple.Create(objType, cmd.Item1, cmd.Item2));
            }

            return events;
        }

        private static void CollectUniqueCommandsFromDescendants(
            Dictionary<string, object> node,
            Dictionary<string, string> uniqueCommands)
        {
            object commandsObj;
            if (node.TryGetValue("commands", out commandsObj))
            {
                var commands = commandsObj as Dictionary<string, object>;
                if (commands != null)
                    foreach (var kv in commands)
                        if (!uniqueCommands.ContainsKey(kv.Key))
                            uniqueCommands[kv.Key] = kv.Value?.ToString() ?? "";
            }

            foreach (var child in OpcTreeReader.GetChildNodes(node))
                CollectUniqueCommandsFromDescendants(child, uniqueCommands);
        }

        private static List<Tuple<string, string>> ParseCommands(string xml)
        {
            var result = new List<Tuple<string, string>>();
            foreach (Match m in Regex.Matches(xml, @"Name=""([^""]+)""[^>]*Description=""([^""]+)"""))
                result.Add(Tuple.Create(m.Groups[1].Value, m.Groups[2].Value));
            return result;
        }

        private static string Col(string value, int width)
        {
            if (value == null) value = "";
            return value.Length >= width ? value : value + new string(' ', width - value.Length);
        }

        public void ExportDbiFile(string treeJsonPath, string outputPath, ICollection<string> allowedChildTypes = null)
        {
            Console.WriteLine("[OPC_Exporter]\t\tBuilding DBI file...");

            var rootNode  = LoadTreeFromJsonFile(treeJsonPath);
            var structure = BuildIntegrationStructureFromTree(rootNode, allowedChildTypes);
            var types     = new List<string>();
            CollectUniqueTypes(structure, types);

            var sb = new StringBuilder();

            foreach (var t in types)
            {
                sb.AppendLine("[OBJ_" + t + "]");
                sb.AppendLine("id, CHAR, 16");
                sb.AppendLine("name, CHAR, 60");
                sb.AppendLine("parent_id, CHAR, 60");
                sb.AppendLine("flags, INTEGER");
                sb.AppendLine("camera_name, TEXT");
                sb.AppendLine("camera_id, CHAR, 60");
                sb.AppendLine("priority, CHAR, 60");
                sb.AppendLine();
            }

            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine("[OPC_Exporter]\t\tDBI file exported to: " + outputPath);
        }

        private static void CollectUniqueTypes(List<object> items, List<string> types)
        {
            foreach (Dictionary<string, object> item in items)
            {
                string type = (string)item["type"];
                if (!types.Contains(type))
                    types.Add(type);
                if (item.ContainsKey("children"))
                    CollectUniqueTypes((List<object>)item["children"], types);
            }
        }

        #endregion

        #region Modelos

        private class OpcStateEntry
        {
            public string LzName        { get; set; }
            public string DescriptionEs { get; set; }
        }

        private static List<OpcStateEntry> LoadOpcStateEntries()
        {
            var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "opc_states.json");
            if (!File.Exists(path))
            {
                Console.WriteLine("[OPC_Exporter]\t\topc_states.json no encontrado en Config\\");
                return new List<OpcStateEntry>();
            }
            var raw = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(
                File.ReadAllText(path, new UTF8Encoding(false)));
            return ParseOpcStateEntries(raw);
        }

        private static List<OpcStateEntry> ParseOpcStateEntries(List<Dictionary<string, object>> raw)
        {
            var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<OpcStateEntry>();
            foreach (var d in raw)
            {
                object lz, desc;
                d.TryGetValue("lzName",        out lz);
                d.TryGetValue("descriptionES", out desc);
                var lzName = lz?.ToString() ?? "";
                if (string.IsNullOrEmpty(lzName) || !seen.Add(lzName)) continue;
                result.Add(new OpcStateEntry { LzName = lzName, DescriptionEs = desc?.ToString() ?? "" });
            }
            return result;
        }

        private sealed class CmdProperty
        {
            public int    Code        { get; set; }
            public string Description { get; set; }
            public string Value       { get; set; }
            public bool   IsXml       { get; set; }
            public Dictionary<string, string> Commands { get; set; }
        }

        #endregion

        #region Helpers privados

        private static string Normalize(string value) =>
            value?.ToUpperInvariant().Replace(' ', '_') ?? string.Empty;

        private static string StripDigits(string value)
        {
            var sb = new StringBuilder();
            foreach (char c in value)
                if (!char.IsDigit(c)) sb.Append(c);
            return sb.ToString().Trim(' ', '-', '_');
        }

        private static string ToJsonLiteral(string value)
        {
            if (value == null) return "null";
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20)
                            sb.Append($"\\u{(int)c:x4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string PrettyPrint(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inString = false;
            bool escaped  = false;

            foreach (char c in json)
            {
                if (escaped)
                {
                    sb.Append(c);
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    sb.Append(c);
                    escaped = true;
                    continue;
                }

                if (c == '"')
                    inString = !inString;

                if (inString)
                {
                    sb.Append(c);
                    continue;
                }

                switch (c)
                {
                    case '{':
                    case '[':
                        sb.Append(c);
                        sb.AppendLine();
                        indent++;
                        sb.Append(new string(' ', indent * 2));
                        break;

                    case '}':
                    case ']':
                        sb.AppendLine();
                        indent--;
                        sb.Append(new string(' ', indent * 2));
                        sb.Append(c);
                        break;

                    case ',':
                        sb.Append(c);
                        sb.AppendLine();
                        sb.Append(new string(' ', indent * 2));
                        break;

                    case ':':
                        sb.Append(": ");
                        break;

                    default:
                        if (c != ' ')
                            sb.Append(c);
                        break;
                }
            }

            return CollapseLeafObjects(sb.ToString());
        }

        private static readonly Regex LeafObjectRegex =
            new Regex(@"\{[^{}\[\]]*\}", RegexOptions.Singleline);

        private static string CollapseLeafObjects(string json) =>
            LeafObjectRegex.Replace(json, m =>
            {
                var collapsed = Regex.Replace(m.Value, @"\s+", " ").Trim();
                return collapsed.Length <= 150 ? collapsed : m.Value;
            });

        #endregion
    }
}