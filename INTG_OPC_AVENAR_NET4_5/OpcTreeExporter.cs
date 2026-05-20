using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Opc;
using Opc.Da;

namespace ISS.OPCMonitor
{
    class OpcTreeExporter
    {
        private readonly Opc.Da.Server _server;
        private static readonly BrowseFilters FilterAll = new BrowseFilters { BrowseFilter = browseFilter.all };

        public OpcTreeExporter(Opc.Da.Server server)
        {
            _server = server;
        }

        public void ExportCleanTreeToJson(string outputPath)
        {
            Console.WriteLine("Building OPC tree...");

            var root = new Dictionary<string, object>
            {
                { "name", "root" },
                { "elementType", "branch" },
                { "children", BuildChildren(null, "") }
            };

            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string json = PrettyPrint(serializer.Serialize(root));

            File.WriteAllText(outputPath, json, Encoding.UTF8);
            Console.WriteLine("Tree exported to: " + outputPath);
        }

        public void ExportBranchTreeToJson(string outputPath)
        {
            Console.WriteLine("Building OPC branch tree...");

            var root = new Dictionary<string, object>
            {
                { "name", "root" },
                { "elementType", "branch" },
                { "children", BuildBranchChildren(null) }
            };

            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string json = PrettyPrint(serializer.Serialize(root));

            File.WriteAllText(outputPath, json, Encoding.UTF8);
            Console.WriteLine("Branch tree exported to: " + outputPath);
        }

        private List<object> BuildBranchChildren(ItemIdentifier parentId)
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

                var node = new Dictionary<string, object>
                {
                    { "name",        elem.Name },
                    { "elementType", elem.IsItem ? "branch|item" : "branch" },
                    { "children",    BuildBranchChildren(new ItemIdentifier(elem.ItemName)) }
                };

                result.Add(node);
            }

            return result;
        }

        public void ExportIntegrationJson(string outputPath)
        {
            Console.WriteLine("Building OPC integration JSON...");

            var root = new Dictionary<string, object>
            {
                { "version",       1  },
                { "reset_timeout", 60 },
                { "structure",     BuildIntegrationChildren(null, isFirstLevel: true) }
            };

            string json = FormatIntegrationJson(root);

            File.WriteAllText(outputPath, json, Encoding.UTF8);
            Console.WriteLine("Integration JSON exported to: " + outputPath);
        }

        public void ExportDdiFile(string outputPath)
        {
            Console.WriteLine("Building DDI file...");

            var structure = BuildIntegrationChildren(null, isFirstLevel: true);
            var types = new List<string>();
            CollectUniqueTypes(structure, types);

            int c1 = Math.Max(types.Count > 0 ? types.Max(t => t.Length) + 2 : 22, 22);
            const int c2 = 27, c3 = 13, c4 = 20, c5 = 19, c6 = 16;

            var sb = new StringBuilder();

            sb.AppendLine("[OBJTYPE]");
            sb.AppendLine(Col("<objType>", c1) + "|" + Col("<Type name>", c2) + "|" + Col("<Group name>", c3) + "|");
            sb.AppendLine();
            foreach (var t in types)
                sb.AppendLine(Col(t, c1) + "|" + Col("", c2) + "|" + Col("", c3) + "|");
            sb.AppendLine();

            sb.AppendLine("[EVENT]");
            sb.AppendLine(Col("<objType>", c1) + "|" + Col("<type>", c2) + "|" + Col("<>", c3) + "|" + Col("<name>", c4) + "|" + Col("<>", c5) + "|");
            sb.AppendLine();
            sb.AppendLine();

            sb.AppendLine("[REACT]");
            sb.AppendLine(Col("<objType>", c1) + "|" + Col("<comand-type>", c2) + "|" + Col("<>", c3) + "|" + Col("<>", c4) + "|" + Col("<comand-name>", c5) + "|" + Col("<>", c6) + "|");
            sb.AppendLine();
            sb.AppendLine();

            sb.AppendLine("[STATE]");
            sb.AppendLine(Col("<objType>", c1) + "|" + Col("<state>", c2) + "|" + Col("<shortName>", c3) + "|" + Col("<>", c4) + "|" + Col("<>", c5) + "|" + Col("<Descripcion/name>", c6) + "|");
            sb.AppendLine();
            sb.AppendLine();

            sb.AppendLine("[MARKER]");
            sb.AppendLine();

            sb.AppendLine("[STATE_RULE]");
            sb.AppendLine(Col("<objType>", c1) + "|" + Col("<trigguer Event>", c2) + "|" + Col("<FromStatus>", c3) + "|" + Col("<To State>", c4) + "|");
            sb.AppendLine();

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            Console.WriteLine("DDI file exported to: " + outputPath);
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

        private static string Col(string value, int width)
        {
            if (value == null) value = "";
            return value.Length >= width ? value : value + new string(' ', width - value.Length);
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
            string pad = new string('\t', indent);
            var keys = new List<string>(obj.Keys);
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
            if (value == null)   return "null";
            if (value is string s) return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            if (value is bool b)   return b ? "true" : "false";
            return value.ToString();
        }

        private List<object> BuildIntegrationChildren(ItemIdentifier parentId, bool isFirstLevel = false)
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

                var children = BuildIntegrationChildren(new ItemIdentifier(elem.ItemName));

                var node = new Dictionary<string, object>();
                if (isFirstLevel)
                    node["parent"] = "INTEGRATION";
                node["type"] = elem.Name;
                if (children.Count > 0)
                    node["children"] = children;

                result.Add(node);
            }

            return result;
        }

        private List<object> BuildChildren(ItemIdentifier parentId, string parentPosId)
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
            var mergedList = new List<MergedElement>();
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

            var result = new List<object>();

            for (int i = 0; i < mergedList.Count; i++)
            {
                var g = mergedList[i];

                // Every node (branch or item) occupies a positional slot.
                // Only item/branch|item nodes expose their id; branches use their
                // positional id only as a prefix for their children.
                string posId = string.IsNullOrEmpty(parentPosId)
                    ? (i + 1).ToString()
                    : parentPosId + "." + (i + 1);

                string elementType = (g.HasChildren && g.IsItem) ? "branch|item"
                    : g.HasChildren ? "branch"
                    : "item";

                var node = new Dictionary<string, object>();
                node["name"]        = g.Name;
                node["elementType"] = elementType;

                if (g.IsItem)
                {
                    node["id"]         = posId;
                    node["properties"] = GetItemProperties(g.ItemItemName ?? g.BranchItemName);
                }

                if (g.HasChildren)
                {
                    var childId = new ItemIdentifier(g.BranchItemName ?? g.ItemItemName);
                    node["children"] = BuildChildren(childId, posId);
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
                    props.Add(new Dictionary<string, object>
                    {
                        { "code",        prop.ID.Code },
                        { "description", prop.Description ?? prop.ID.Name?.Name ?? string.Empty },
                        { "value",       prop.Value != null ? prop.Value.ToString() : string.Empty }
                    });
                }
                return props;
            }
            catch (Exception ex)
            {
                Console.WriteLine("GetProperties error for '" + itemName + "': " + ex.Message);
                return new List<object>();
            }
        }

        // Converts compact JSON from JavaScriptSerializer into indented JSON.
        private static string PrettyPrint(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inString = false;
            bool escaped = false;

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

        private static string CollapseLeafObjects(string json)
        {
            return LeafObjectRegex.Replace(json, m =>
                Regex.Replace(m.Value, @"\s+", " ").Trim());
        }

        private class MergedElement
        {
            public string Name          { get; set; }
            public bool   HasChildren   { get; set; }
            public bool   IsItem        { get; set; }
            public string BranchItemName { get; set; }
            public string ItemItemName  { get; set; }
        }
    }
}
