using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using Opc;
using Opc.Da;

namespace ISS.OPCMonitor
{
    class OpcTreeExporter
    {
        // ══════════════════════════════════════════════════════════════════
        // Campos y constructor
        // ══════════════════════════════════════════════════════════════════

        private readonly Opc.Da.Server _server;
        private readonly string _prefix;
        private readonly string _serverUrl;

        private Dictionary<int, Dictionary<string, string>> _cmdCache;

        private static readonly BrowseFilters FilterAll    = new BrowseFilters { BrowseFilter = browseFilter.all };
        private static readonly BrowseFilters FilterBranch = new BrowseFilters { BrowseFilter = browseFilter.branch };
        private static readonly BrowseFilters FilterItem   = new BrowseFilters { BrowseFilter = browseFilter.item };

        public OpcTreeExporter(Opc.Da.Server server, string prefix = "", string serverUrl = "")
        {
            _server    = server;
            _prefix    = prefix ?? "";
            _serverUrl = serverUrl ?? "";
        }

        
        // ══════════════════════════════════════════════════════════════════
        // 1. ExportCommandsToJson  →  commands_*.json
        //    Propiedades del ítem especial CMD con sus comandos XML parseados.
        //    Llamado automáticamente desde ExportItemTreeToJson (#2).
        // ══════════════════════════════════════════════════════════════════

        public void ExportCommandsToJson(string outputPath)
        {
            try
            {
                var props = FetchCmdItemProperties("CMD");
                var json  = SerializeCmdJson(props, _serverUrl, DateTime.Now);

                File.WriteAllText(outputPath, json, new UTF8Encoding(false));
                Console.WriteLine($"[OPC] Comandos CMD exportados: {outputPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC] Error al exportar comandos CMD: {ex.Message}");
            }
        }

        private List<CmdProperty> FetchCmdItemProperties(string itemName)
        {
            try
            {
                var results = _server.GetProperties(
                    new[] { new ItemIdentifier(itemName) }, null, true);

                if (results == null || results.Length == 0)
                    return new List<CmdProperty>();

                var props = new List<CmdProperty>();
                foreach (ItemProperty prop in results[0])
                {
                    if (!prop.ResultID.Succeeded()) continue;

                    var cmdProp = new CmdProperty
                    {
                        Code        = prop.ID.Code,
                        Description = string.IsNullOrWhiteSpace(prop.Description)
                            ? $"property_{prop.ID.Code}"
                            : prop.Description
                    };

                    var raw = prop.Value?.ToString() ?? string.Empty;
                    if (raw.TrimStart().StartsWith("<"))
                    {
                        cmdProp.IsXml    = true;
                        cmdProp.Commands = new Dictionary<string, string>();
                        try
                        {
                            var doc = XDocument.Parse(raw);
                            XNamespace ns = "file:///S3K/Proxyverwalter";
                            cmdProp.Commands = doc.Descendants(ns + "Command")
                                .ToDictionary(
                                    e => e.Attribute("Name")?.Value ?? "(unnamed)",
                                    e => e.ToString());
                        }
                        catch { }
                    }
                    else
                    {
                        cmdProp.Value = raw;
                    }

                    props.Add(cmdProp);
                }
                return props;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC] Error al leer propiedades de '{itemName}': {ex.Message}");
                return new List<CmdProperty>();
            }
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

        private sealed class CmdProperty
        {
            public int    Code        { get; set; }
            public string Description { get; set; }
            public string Value       { get; set; }
            public bool   IsXml       { get; set; }
            public Dictionary<string, string> Commands { get; set; }
        }

        // ══════════════════════════════════════════════════════════════════
        // 2. ExportCleanTreeToJson  →  treeExporter_*.json
        //    Árbol con elementType (branch / item / branch|item),
        //    IDs posicionales y todas las propiedades OPC por ítem.
        // ══════════════════════════════════════════════════════════════════

        public void ExportCleanTreeToJson(string outputPath)
        {
            Console.WriteLine("Building OPC tree...");

            var root = new Dictionary<string, object>
            {
                { "name",        "root" },
                { "elementType", "branch" },
                { "itemName",    "" },
                { "id",          "" },
                { "children",    BuildChildren(null) }
            };

            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string json = PrettyPrint(serializer.Serialize(root))
                .Replace("\\u003c", "<")
                .Replace("\\u003e", ">")
                .Replace("\\u0026", "&");

            File.WriteAllText(outputPath, json, new UTF8Encoding(false));
            Console.WriteLine("Tree exported to: " + outputPath);
        }

        private List<object> BuildChildren(ItemIdentifier parentId)
        {
            BrowsePosition pos;
            BrowseElement[] elements;

            try
            {
                elements = _server.Browse(parentId, FilterAll, out pos);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Browse error at '" + (parentId != null ? parentId.ItemName : "root") + "': " + ex.Message);
                return new List<object>();
            }

            if (elements == null || elements.Length == 0)
                return new List<object>();

            foreach (var e in elements)
                Console.WriteLine($"[DBG] Name='{e.Name}' ItemName='{e.ItemName}' HasChildren={e.HasChildren} IsItem={e.IsItem}");

            // Merge elements with the same Name at this level (branch + item → branch|item)
            var mergedList  = new List<MergedElement>();
            var nameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var elem in elements)
            {
                int idx;
                if (nameToIndex.TryGetValue(elem.Name, out idx))
                {
                    var existing = mergedList[idx];
                    if (elem.HasChildren) { existing.HasChildren = true; existing.BranchItemName = elem.ItemName; }
                    if (elem.IsItem)      { existing.IsItem = true;      existing.ItemItemName  = elem.ItemName; }
                }
                else
                {
                    var m = new MergedElement { Name = elem.Name };
                    if (elem.HasChildren) { m.HasChildren = true; m.BranchItemName = elem.ItemName; }
                    if (elem.IsItem)      { m.IsItem = true;      m.ItemItemName  = elem.ItemName; }
                    nameToIndex[elem.Name] = mergedList.Count;
                    mergedList.Add(m);
                }
            }

            // Items primero, luego branches (branch|item va con branches)
            var ordered = mergedList
                .Where(g => g.IsItem && !g.HasChildren)
                .Concat(mergedList.Where(g => g.HasChildren));

            var result = new List<object>();

            foreach (var g in ordered)
            {
                string elementType = (g.HasChildren && g.IsItem) ? "branch|item"
                    : g.HasChildren ? "branch"
                    : "item";

                string itemName = g.ItemItemName ?? g.BranchItemName ?? "";

                var node = new Dictionary<string, object>();
                node["name"]        = g.Name;
                node["elementType"] = elementType;
                node["itemName"]    = itemName;
                node["id"]          = "";

                if (g.IsItem)
                {
                    var props = GetItemProperties(itemName);
                    node["properties"] = props;
                    node["commands"]   = LookupCommands(props);
                }

                if (g.HasChildren)
                {
                    var children = BuildChildren(new ItemIdentifier(g.BranchItemName ?? g.ItemItemName));
                    node["hasItemChildren"] = children.Cast<Dictionary<string, object>>()
                        .Any(c => { var et = (string)c["elementType"]; return et == "item" || et == "branch|item"; });
                    node["children"] = children;
                }
                else
                {
                    node["hasItemChildren"] = false;
                }

                result.Add(node);
            }

            return result;
        }

        private List<object> GetItemProperties(string itemName)
        {
            if (string.IsNullOrEmpty(itemName))
                return new List<object>();

            try
            {
                var propCols = _server.GetProperties(
                    new[] { new ItemIdentifier(itemName) },
                    null,
                    true
                );

                if (propCols == null || propCols.Length == 0)
                    return new List<object>();

                var props = new List<object>();
                foreach (ItemProperty prop in propCols[0])
                {
                    var raw   = prop.Value != null ? prop.Value.ToString() : string.Empty;
                    var entry = new Dictionary<string, object>
                    {
                        { "code",        prop.ID.Code },
                        { "description", prop.Description ?? prop.ID.Name?.Name ?? string.Empty }
                    };

                    if (raw.TrimStart().StartsWith("<"))
                    {
                        var commands = new Dictionary<string, string>();
                        try
                        {
                            var doc = XDocument.Parse(raw);
                            XNamespace ns = "file:///S3K/Proxyverwalter";
                            commands = doc.Descendants(ns + "Command")
                                .ToDictionary(
                                    e => e.Attribute("Name")?.Value ?? "(unnamed)",
                                    e => e.ToString());
                        }
                        catch { }
                        entry["commands"] = commands;
                    }
                    else
                    {
                        entry["value"] = raw;
                    }

                    props.Add(entry);
                }
                return props;
            }
            catch (Exception ex)
            {
                Console.WriteLine("GetProperties error for '" + itemName + "': " + ex.Message);
                return new List<object>();
            }
        }

        private Dictionary<string, string> LookupCommands(List<object> properties)
        {
            var detectorProp = properties
                .Cast<Dictionary<string, object>>()
                .FirstOrDefault(p => string.Equals(
                    p["description"]?.ToString(), "DetectorTypeID",
                    StringComparison.OrdinalIgnoreCase));

            if (detectorProp == null
                || !int.TryParse(detectorProp["value"]?.ToString(), out int typeId)
                || typeId <= 0)
                return new Dictionary<string, string>();

            if (_cmdCache == null)
            {
                _cmdCache = new Dictionary<int, Dictionary<string, string>>();
                foreach (var cmd in FetchCmdItemProperties("CMD"))
                    if (cmd.Commands != null)
                        _cmdCache[cmd.Code] = cmd.Commands;
            }

            Dictionary<string, string> commands;
            return _cmdCache.TryGetValue(typeId, out commands)
                ? commands
                : new Dictionary<string, string>();
        }

        private class MergedElement
        {
            public string Name           { get; set; }
            public bool   HasChildren    { get; set; }
            public bool   IsItem         { get; set; }
            public string BranchItemName { get; set; }
            public string ItemItemName   { get; set; }
        }
                

        #region Archivos de Integración (JSON + DDI + DBI)
        // ══════════════════════════════════════════════════════════════════
        // 3. ExportIntegrationJson  →  integration_*.json
        //    Estructura de integración: tipos y jerarquía de objetos OPC.
        // ══════════════════════════════════════════════════════════════════

        public void ExportIntegrationJson(string outputPath, ICollection<string> allowedChildTypes = null)
        {
            Console.WriteLine("Building OPC integration JSON...");

            var root = new Dictionary<string, object>
            {
                { "version",       1  },
                { "reset_timeout", 60 },
                { "structure",     BuildIntegrationChildren(null, isFirstLevel: true, filter: allowedChildTypes) }
            };

            string json = FormatIntegrationJson(root);

            File.WriteAllText(outputPath, json, new UTF8Encoding(false));
            Console.WriteLine("Integration JSON exported to: " + outputPath);
        }

        private List<object> BuildIntegrationChildren(
            ItemIdentifier parentId,
            bool isFirstLevel = false,
            bool applyFilter  = false,
            ICollection<string> filter = null,
            string parentName = null,
            int depth = 0)
        {
            BrowsePosition pos;
            BrowseElement[] elements;

            try
            {
                elements = _server.Browse(parentId, FilterAll, out pos);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Browse error at '" + (parentId != null ? parentId.ItemName : "root") + "': " + ex.Message);
                return new List<object>();
            }

            if (elements == null || elements.Length == 0)
                return new List<object>();

            var result = new List<object>();

            foreach (var elem in elements)
            {
                if (!elem.HasChildren)
                    continue;

                if (applyFilter && filter != null && filter.Count > 0
                    && !filter.Contains(elem.Name))
                    continue;

                string typeName = IsNumeric(elem.Name)
                    ? (parentName ?? (_prefix + "ITEM")) + "_G" + elem.Name
                    : _prefix + StripDigits(Normalize(elem.Name));

                // depth 0 = root, depth 1 = panel children (tipos, sin recursión adicional)
                List<object> children = new List<object>();
                if (depth < 1)
                {
                    children = BuildIntegrationChildren(
                        new ItemIdentifier(elem.ItemName),
                        applyFilter: isFirstLevel,
                        filter: filter,
                        parentName: typeName,
                        depth: depth + 1);
                }

                var node = new Dictionary<string, object>();
                if (isFirstLevel)
                    node["parent"] = "INTEGRATION";
                node["type"] = typeName;
                if (children.Count > 0)
                    node["children"] = children;

                result.Add(node);
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

        // ══════════════════════════════════════════════════════════════════
        // 4. ExportDdiFile  →  integration_*.ddi
        //    Secciones DDI: [OBJTYPE] [EVENT] [REACT] [STATE] [MARKER] [STATE_RULE]
        // ══════════════════════════════════════════════════════════════════

        public void ExportDdiFile(string outputPath, ICollection<string> allowedChildTypes = null)
        {
            Console.WriteLine("Building DDI file...");

            var structure = BuildIntegrationChildren(null, isFirstLevel: true, filter: allowedChildTypes);
            var types = new List<string>();
            CollectUniqueTypes(structure, types);

            Console.WriteLine("Collecting events from OPC tree...");
            var events = CollectEventRows(allowedChildTypes);

            int c1 = Math.Max(22, Math.Max(
                types.Count  > 0 ? types.Max(t => t.Length)         + 2 : 0,
                events.Count > 0 ? events.Max(e => e.Item1.Length)  + 2 : 0));
            int c3base = types.Count > 0 ? types.Max(t => t.Length) + 2 : 27;
            int c4 = Math.Max(20, events.Count > 0 ? events.Max(e => e.Item3.Length) + 2 : 20);
            int c5 = c4;
            const int c2 = 27, c6 = 16;
            int c3 = Math.Max(c3base, 27);

            var sb = new StringBuilder();

            sb.AppendLine("[OBJTYPE]");
            foreach (var t in types)
            {
                string baseName = t.StartsWith(_prefix) ? t.Substring(_prefix.Length) : t;
                sb.AppendLine(Col(t, c1) + "|" + Col(baseName, c2) + "|" + Col(baseName, c3) + "|");
            }
            sb.AppendLine();

            sb.AppendLine("[EVENT]");
            foreach (var ev in events)
                sb.AppendLine(Col(ev.Item1, c1) + "|" + Col(ev.Item2, c2) + "|" + Col("", c3) + "|" + Col(ev.Item3, c4) + "|" + Col("", c5) + "|");
            sb.AppendLine();

            sb.AppendLine("[REACT]");
            foreach (var ev in events)
                sb.AppendLine(Col(ev.Item1, c1) + "|" + Col(ev.Item2, c2) + "|" + Col("", c3) + "|" + Col("", c4) + "|" + Col(ev.Item3, c5) + "|" + Col("", c6) + "|");
            sb.AppendLine();

            var stateRows = BuildStateRows(events);
            int cs2 = Math.Max(20, stateRows.Count > 0 ? stateRows.Max(r => r.Item2.Length) + 2 : 20);
            int cs3 = 7;
            int cs6 = Math.Max(16, stateRows.Count > 0 ? stateRows.Max(r => r.Item4.Length) + 2 : 16);

            sb.AppendLine("[STATE]");
            foreach (var sr in stateRows)
                sb.AppendLine(Col(sr.Item1, c1) + "|" + Col(sr.Item2, cs2) + "|" + Col(sr.Item3, cs3) + "|" + Col("", c4) + "|" + Col("", c5) + "|" + Col(sr.Item4, cs6) + "|");
            sb.AppendLine();

            sb.AppendLine("[MARKER]");
            sb.AppendLine();

            var stateRuleRows = BuildStateRuleRows(events);
            int csr2 = Math.Max(20, stateRuleRows.Count > 0 ? stateRuleRows.Max(r => r.Item2.Length) + 2 : 20);
            int csr3 = Math.Max(16, stateRuleRows.Count > 0 ? stateRuleRows.Max(r => r.Item3.Length) + 2 : 16);
            int csr4 = Math.Max(16, stateRuleRows.Count > 0 ? stateRuleRows.Max(r => r.Item4.Length) + 2 : 16);

            sb.AppendLine("[STATE_RULE]");
            foreach (var sr in stateRuleRows)
                sb.AppendLine(Col(sr.Item1, c1) + "|" + Col(sr.Item2, csr2) + "|" + Col(sr.Item3, csr3) + "|" + Col(sr.Item4, csr4) + "|");
            sb.AppendLine();

            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine("DDI file exported to: " + outputPath);
        }

        private List<Tuple<string, string, string>> CollectEventRows(ICollection<string> allowedChildTypes = null)
        {
            var events    = new List<Tuple<string, string, string>>();
            var doneTypes = new HashSet<string>();
            List<object>  cmdProps = null;

            // Root → panels → type branches
            foreach (var panel in BrowseElements(null))
            {
                if (!panel.HasChildren) continue;

                foreach (var typeBranch in BrowseElements(new ItemIdentifier(panel.ItemName)))
                {
                    if (!typeBranch.HasChildren) continue;

                    if (allowedChildTypes != null && allowedChildTypes.Count > 0
                        && !allowedChildTypes.Contains(typeBranch.Name))
                        continue;

                    string objType = _prefix + Normalize(typeBranch.Name);
                    if (doneTypes.Contains(objType)) continue;

                    // Search items up to depth 2 under the type branch
                    int detectorTypeId = FindDetectorTypeId(new ItemIdentifier(typeBranch.ItemName), maxDepth: 2);
                    if (detectorTypeId == 0) continue;

                    if (cmdProps == null)
                        cmdProps = GetItemProperties("CMD");

                    foreach (Dictionary<string, object> cmdProp in cmdProps)
                    {
                        if (System.Convert.ToInt32(cmdProp["code"]) != detectorTypeId) continue;

                        object commandsObj;
                        if (cmdProp.TryGetValue("commands", out commandsObj))
                        {
                            foreach (var kv in (Dictionary<string, string>)commandsObj)
                                foreach (var cmd in ParseCommands(kv.Value))
                                    events.Add(Tuple.Create(objType, cmd.Item1, cmd.Item2));
                        }
                        break;
                    }

                    doneTypes.Add(objType);
                }
            }

            return events;
        }

        private int FindDetectorTypeId(ItemIdentifier parentId, int maxDepth)
        {
            if (maxDepth < 0) return 0;

            foreach (var elem in BrowseElements(parentId))
            {
                if (elem.IsItem)
                {
                    foreach (Dictionary<string, object> p in GetItemProperties(elem.ItemName))
                    {
                        if (string.Equals(p["description"]?.ToString(), "DetectorTypeID", StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(p["value"]?.ToString(), out int tid) && tid > 0)
                            return tid;
                    }
                }

                if (elem.HasChildren)
                {
                    int tid = FindDetectorTypeId(new ItemIdentifier(elem.ItemName), maxDepth - 1);
                    if (tid > 0) return tid;
                }
            }

            return 0;
        }

        private static List<Tuple<string, string>> ParseCommands(string xml)
        {
            var result = new List<Tuple<string, string>>();
            foreach (Match m in Regex.Matches(xml, @"Name=""([^""]+)""[^>]*Description=""([^""]+)"""))
                result.Add(Tuple.Create(m.Groups[1].Value, m.Groups[2].Value));
            return result;
        }

        private static readonly Dictionary<string, Tuple<string, string, string>> CommandToState =
            new Dictionary<string, Tuple<string, string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "ACK",                 Tuple.Create("ACKNOWLEDGED", "ACK", "Reconocido") },
                { "RESET",               Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "RESET_PANEL",         Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "RESET_TRANS",         Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "RESET_SCOPE",         Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "RESET_NETWORK_SCOPE", Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "BYPASS_ON",           Tuple.Create("BYPASSED",     "BYP", "Anulado") },
                { "BYPASS_OFF",          Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "BYPASS_SCOPE_ON",     Tuple.Create("BYPASSED",     "BYP", "Anulado") },
                { "BYPASS_SCOPE_OFF",    Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "ISOLATE_ON",          Tuple.Create("ISOLATED",     "ISO", "Aislado") },
                { "ISOLATE_OFF",         Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "WALKTEST_ON",         Tuple.Create("WALKTEST",     "WLK", "Prueba en sitio") },
                { "WALKTEST_OFF",        Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "ACTIVATE_ON",         Tuple.Create("ACTIVATED",    "ACT", "Activado") },
                { "ACTIVATE_OFF",        Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "ALTERSET_ON",         Tuple.Create("ALTERSET",     "ALT", "Ajuste alternativo") },
                { "ALTERSET_OFF",        Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "SILENCE_ON",          Tuple.Create("SILENCED",     "SIL", "Silenciado") },
                { "SILENCE_OFF",         Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "SILENCE_ON_SCOPE",    Tuple.Create("SILENCED",     "SIL", "Silenciado") },
                { "SILENCE_OFF_SCOPE",   Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "DRILL_ON",            Tuple.Create("DRILL",        "DRL", "Simulacro") },
                { "DRILL_OFF",           Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "AUTOMATIC",           Tuple.Create("AUTOMATIC",    "AUT", "Automático") },
                { "BYBUTTON_ON",         Tuple.Create("ACTIVATED",    "ACT", "Activado") },
                { "BYBUTTON_OFF",        Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "TEST",                Tuple.Create("TEST",         "TST", "Prueba") },
                { "DAY_ON",              Tuple.Create("DAY_MODE",     "DAY", "Modo día") },
                { "DAY_OFF",             Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "OUTTOFPE_ON_PANEL",   Tuple.Create("OUT_OF_FPE",   "OFP", "Fuera de protección") },
                { "OUTTOFPE_OFF_PANEL",  Tuple.Create("NORMAL",       "NRM", "Normal") },
                { "OUTTOFPE_ON_SCOPE",   Tuple.Create("OUT_OF_FPE",   "OFP", "Fuera de protección") },
                { "OUTTOFPE_OFF_SCOPE",  Tuple.Create("NORMAL",       "NRM", "Normal") },
            };

        private static readonly Tuple<string, string, string>[] BaseStates =
        {
            Tuple.Create("NORMAL", "NRM", "Normal"),
            Tuple.Create("ALARM",  "ALM", "Alarma"),
            Tuple.Create("FAULT",  "FLT", "Falla"),
        };

        private static List<Tuple<string, string, string, string>> BuildStateRows(
            List<Tuple<string, string, string>> events)
        {
            var byType    = new Dictionary<string, List<Tuple<string, string, string>>>();
            var typeOrder = new List<string>();

            foreach (var ev in events)
            {
                if (!byType.ContainsKey(ev.Item1))
                {
                    byType[ev.Item1] = new List<Tuple<string, string, string>>(BaseStates);
                    typeOrder.Add(ev.Item1);
                }

                Tuple<string, string, string> state;
                if (!CommandToState.TryGetValue(ev.Item2, out state) || state == null) continue;

                if (!byType[ev.Item1].Exists(s => s.Item1 == state.Item1))
                    byType[ev.Item1].Add(state);
            }

            var result = new List<Tuple<string, string, string, string>>();
            foreach (var objType in typeOrder)
                foreach (var s in byType[objType])
                    result.Add(Tuple.Create(objType, s.Item1, s.Item2, s.Item3));
            return result;
        }

        private static readonly Dictionary<string, string> OffCommandFromState =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "BYPASS_OFF",         "BYPASSED"   },
                { "BYPASS_SCOPE_OFF",   "BYPASSED"   },
                { "ISOLATE_OFF",        "ISOLATED"   },
                { "WALKTEST_OFF",       "WALKTEST"   },
                { "ACTIVATE_OFF",       "ACTIVATED"  },
                { "BYBUTTON_OFF",       "ACTIVATED"  },
                { "ALTERSET_OFF",       "ALTERSET"   },
                { "SILENCE_OFF",        "SILENCED"   },
                { "SILENCE_OFF_SCOPE",  "SILENCED"   },
                { "DRILL_OFF",          "DRILL"      },
                { "DAY_OFF",            "DAY_MODE"   },
                { "OUTTOFPE_OFF_PANEL", "OUT_OF_FPE" },
                { "OUTTOFPE_OFF_SCOPE", "OUT_OF_FPE" },
            };

        private static List<Tuple<string, string, string, string>> BuildStateRuleRows(
            List<Tuple<string, string, string>> events)
        {
            var result = new List<Tuple<string, string, string, string>>();
            var seen   = new HashSet<string>();

            foreach (var ev in events)
            {
                string key = ev.Item1 + "|" + ev.Item2;
                if (!seen.Add(key)) continue;

                Tuple<string, string, string> stateInfo;
                if (!CommandToState.TryGetValue(ev.Item2, out stateInfo) || stateInfo == null) continue;

                string toState = stateInfo.Item1;
                string fromState;
                OffCommandFromState.TryGetValue(ev.Item2, out fromState);

                result.Add(Tuple.Create(ev.Item1, ev.Item2, fromState ?? "", toState));
            }

            return result;
        }

        private static string Col(string value, int width)
        {
            if (value == null) value = "";
            return value.Length >= width ? value : value + new string(' ', width - value.Length);
        }

        // ══════════════════════════════════════════════════════════════════
        // 5. ExportDbiFile  →  integration_*.dbi
        //    Definición de tablas de objetos por tipo (esquema DBI).
        // ══════════════════════════════════════════════════════════════════

        public void ExportDbiFile(string outputPath, ICollection<string> allowedChildTypes = null)
        {
            Console.WriteLine("Building DBI file...");

            var structure = BuildIntegrationChildren(null, isFirstLevel: true, filter: allowedChildTypes);
            var types = new List<string>();
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
            Console.WriteLine("DBI file exported to: " + outputPath);
        }


        #endregion

        // ══════════════════════════════════════════════════════════════════
        // Helpers compartidos
        // ══════════════════════════════════════════════════════════════════

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

        private BrowseElement[] BrowseElements(ItemIdentifier parentId)
        {
            BrowsePosition pos;
            try { return _server.Browse(parentId, FilterAll, out pos) ?? new BrowseElement[0]; }
            catch { return new BrowseElement[0]; }
        }

        private static string Normalize(string value) =>
            value?.ToUpperInvariant().Replace(' ', '_') ?? string.Empty;

        private static bool IsNumeric(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char c in value)
                if (!char.IsDigit(c)) return false;
            return true;
        }

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
                // Solo colapsa si el resultado cabe en una línea razonable.
                // Objetos grandes como "commands" quedan expandidos.
                return collapsed.Length <= 150 ? collapsed : m.Value;
            });
    }
}
