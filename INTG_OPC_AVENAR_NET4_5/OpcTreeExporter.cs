using System;
using System.Collections.Generic;
using System.IO;
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
