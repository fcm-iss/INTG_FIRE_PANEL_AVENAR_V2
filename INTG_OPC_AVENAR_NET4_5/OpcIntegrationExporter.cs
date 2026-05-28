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
    class OpcIntegrationExporter
    {
        #region Campos y Constructor

        private readonly Opc.Da.Server _server;
        private readonly string _prefix;
        private readonly string _serverUrl;

        private Dictionary<int, Dictionary<string, string>> _cmdCache;

        private static readonly BrowseFilters FilterAll    = new BrowseFilters { BrowseFilter = browseFilter.all };
        private static readonly BrowseFilters FilterBranch = new BrowseFilters { BrowseFilter = browseFilter.branch };
        private static readonly BrowseFilters FilterItem   = new BrowseFilters { BrowseFilter = browseFilter.item };

        public OpcIntegrationExporter(Opc.Da.Server server, string prefix = "", string serverUrl = "")
        {
            _server    = server;
            _prefix    = prefix ?? "";
            _serverUrl = serverUrl ?? "";
        }

        #endregion

        #region Exportación OPC file

        // ══════════════════════════════════════════════════════════════════
        // ExportCommandsToJson  →  commands_*.json
        // ══════════════════════════════════════════════════════════════════

        public void ExportCommandsToJson(string outputPath)
        {
            try
            {
                var props = FetchCmdItemProperties("CMD");
                var json  = SerializeCmdJson(props, _serverUrl, DateTime.Now);

                File.WriteAllText(outputPath, json, new UTF8Encoding(false));
                Console.WriteLine($"[OPC_Exporter] Comandos CMD exportados: {outputPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC_Exporter] Error al exportar comandos CMD: {ex.Message}");
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
                Console.WriteLine($"[OPC_Exporter] Error al leer propiedades de '{itemName}': {ex.Message}");
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

        // ══════════════════════════════════════════════════════════════════
        // ExportOpcTreeWithPropertiesToJson  →  treeExporter_*.json
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Construye el árbol OPC completo con propiedades e IDs y lo escribe en un archivo JSON.
        /// </summary>
        public void ExportOpcTreeWithPropertiesToJson(string outputPath)
        {
            Console.WriteLine("[OPC_Exporter] Building OPC tree...");

            var rootNode = BuildRootNode();
            var json     = SerializeTreeToJson(rootNode);
            WriteJsonToFile(json, outputPath);

            Console.WriteLine("[OPC_Exporter] Tree exported to: " + outputPath);
        }

        /// <summary>
        /// Construye el nodo raíz del árbol con sus hijos recursivos.
        /// </summary>
        private Dictionary<string, object> BuildRootNode()
        {
            return new Dictionary<string, object>
            {
                { "name",        "root" },
                { "elementType", "branch" },
                { "itemName",    "" },
                { "id",          "" },
                { "children",    BuildChildNodes(null) }
            };
        }

        /// <summary>
        /// Serializa el árbol en memoria a JSON con formato legible,
        /// decodificando los caracteres unicode escapados por el serializador.
        /// </summary>
        private static string SerializeTreeToJson(Dictionary<string, object> rootNode)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return PrettyPrint(serializer.Serialize(rootNode))
                .Replace("\\u003c", "<")
                .Replace("\\u003e", ">")
                .Replace("\\u0026", "&");
        }

        /// <summary>
        /// Escribe el contenido JSON en el archivo de salida en codificación UTF-8 sin BOM.
        /// </summary>
        private static void WriteJsonToFile(string json, string outputPath)
        {
            File.WriteAllText(outputPath, json, new UTF8Encoding(false));
        }

        /// <summary>
        /// Recorre recursivamente un nivel del árbol OPC y devuelve la lista de nodos hijos construidos.
        /// </summary>
        private List<object> BuildChildNodes(ItemIdentifier parentId, int depth = 0, string parentIdStr = "")
        {
            var elements = BrowseLevel(parentId);

            if (elements == null || elements.Length == 0)
                return new List<object>();

            LogBrowsedElements(elements);

            var merged      = MergeElementsByName(elements);
            var treeOrdered = OrderElementsForTraversal(merged);

            var treeNodesAtCurrentLevel = new List<object>();
            int childCounter = 1;

            foreach (var element in treeOrdered)
            {
                string nodeId = ComputeNodeId(element.Name, depth, parentIdStr, ref childCounter);
                var    node   = BuildNodeEntry(element, nodeId, depth, parentIdStr);
                treeNodesAtCurrentLevel.Add(node);
            }

            return treeNodesAtCurrentLevel;
        }

        /// <summary>
        /// Escribe en consola los elementos OPC devueltos por el servidor en el nivel actual.
        /// </summary>
        private static void LogBrowsedElements(BrowseElement[] elements)
        {
            foreach (var e in elements)
                Console.WriteLine($"[OPC_Exporter] Name='{e.Name}' ItemName='{e.ItemName}' HasChildren={e.HasChildren} IsItem={e.IsItem}");
        }

        /// <summary>
        /// Construye el diccionario completo de un nodo: campos base, propiedades OPC y nodos hijos.
        /// </summary>
        private Dictionary<string, object> BuildNodeEntry(MergedElement element, string nodeId,
                                                          int depth, string parentIdStr)
        {
            var elementType = DetermineElementType(element);
            var itemName    = ResolveItemName(element);
            var node        = CreateBaseNodeFields(element.Name, elementType, itemName, nodeId);

            AttachItemPropertiesIfItem(node, element, itemName);
            AttachChildrenIfBranch(node, element, depth, nodeId);

            return node;
        }

        /// <summary>
        /// Determina el tipo estructural del elemento: branch, item, o branch|item.
        /// </summary>
        private static string DetermineElementType(MergedElement element)
        {
            if (element.HasChildren && element.IsItem) return "branch|item";
            if (element.HasChildren)                   return "branch";
            return "item";
        }

        /// <summary>
        /// Resuelve el ItemName OPC del elemento, priorizando el path de ítem sobre el de rama.
        /// </summary>
        private static string ResolveItemName(MergedElement element) =>
            element.ItemItemName ?? element.BranchItemName ?? "";

        /// <summary>
        /// Crea el diccionario con los cuatro campos base que todo nodo del árbol tiene:
        /// name, elementType, itemName e id.
        /// </summary>
        private static Dictionary<string, object> CreateBaseNodeFields(string name, string elementType,
                                                                        string itemName, string nodeId)
        {
            return new Dictionary<string, object>
            {
                { "name",        name },
                { "elementType", elementType },
                { "itemName",    itemName },
                { "id",          nodeId }
            };
        }

        /// <summary>
        /// Si el elemento es un ítem OPC, obtiene sus propiedades y comandos y los adjunta al nodo.
        /// </summary>
        private void AttachItemPropertiesIfItem(Dictionary<string, object> node,
                                                MergedElement element, string itemName)
        {
            if (!element.IsItem) return;

            var props          = GetItemProperties(itemName);
            node["properties"] = props;
            node["commands"]   = LookupCommands(props);
        }

        /// <summary>
        /// Si el elemento es una rama, construye sus hijos recursivamente y los adjunta al nodo.
        /// Si no tiene hijos, establece hasItemChildren en false.
        /// </summary>
        private void AttachChildrenIfBranch(Dictionary<string, object> node,
                                            MergedElement element, int depth, string nodeId)
        {
            if (!element.HasChildren)
            {
                node["hasItemChildren"] = false;
                return;
            }

            var childParentId       = new ItemIdentifier(element.BranchItemName ?? element.ItemItemName);
            var children            = BuildChildNodes(childParentId, depth + 1, nodeId);
            node["hasItemChildren"] = HasDirectItemChildren(children);
            node["children"]        = children;
        }

        /// <summary>
        /// Indica si alguno de los hijos directos es un ítem OPC (elementType item o branch|item).
        /// </summary>
        private static bool HasDirectItemChildren(List<object> children) =>
            children.Cast<Dictionary<string, object>>()
                    .Any(c => { var et = (string)c["elementType"]; return et == "item" || et == "branch|item"; });

        /// <summary>
        /// Consulta al servidor OPC los elementos hijos del nodo indicado.
        /// Devuelve null si ocurre un error de comunicación.
        /// </summary>
        private BrowseElement[] BrowseLevel(ItemIdentifier parentId)
        {
            try
            {
                BrowsePosition pos;
                return _server.Browse(parentId, FilterAll, out pos);
            }
            catch (Exception ex)
            {
                var nodeName = parentId?.ItemName ?? "root";
                Console.WriteLine($"[OPC_Exporter] Browse error at '{nodeName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fusiona los elementos del nivel actual agrupándolos por Name en un solo MergedElement,
        /// ya que un mismo nodo OPC puede aparecer como branch, como item, o como ambos.
        /// </summary>
        private static List<MergedElement> MergeElementsByName(BrowseElement[] elements)
        {
            var mergedList  = new List<MergedElement>();
            var nameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var elem in elements)
            {
                int idx;
                if (nameToIndex.TryGetValue(elem.Name, out idx))
                    UpdateExistingMergedElement(mergedList[idx], elem);
                else
                    AddNewMergedElement(mergedList, nameToIndex, elem);
            }

            return mergedList;
        }

        /// <summary>
        /// Actualiza el MergedElement existente con los flags del elemento duplicado encontrado.
        /// </summary>
        private static void UpdateExistingMergedElement(MergedElement existing, BrowseElement elem)
        {
            if (elem.HasChildren) { existing.HasChildren = true; existing.BranchItemName = elem.ItemName; }
            if (elem.IsItem)      { existing.IsItem = true;      existing.ItemItemName   = elem.ItemName; }
        }

        /// <summary>
        /// Crea un nuevo MergedElement a partir del elemento OPC y lo registra en la lista e índice.
        /// </summary>
        private static void AddNewMergedElement(List<MergedElement> mergedList,
                                                Dictionary<string, int> nameToIndex,
                                                BrowseElement elem)
        {
            var m = new MergedElement { Name = elem.Name };
            if (elem.HasChildren) { m.HasChildren = true; m.BranchItemName = elem.ItemName; }
            if (elem.IsItem)      { m.IsItem = true;      m.ItemItemName   = elem.ItemName; }
            nameToIndex[elem.Name] = mergedList.Count;
            mergedList.Add(m);
        }

        /// <summary>
        /// Ordena los elementos del nivel: ítems puros primero, luego ramas (branch y branch|item).
        /// Garantiza que el contador del nivel 1 produzca IDs deterministas.
        /// </summary>
        private static IEnumerable<MergedElement> OrderElementsForTraversal(List<MergedElement> merged) =>
            merged.Where(e => e.IsItem && !e.HasChildren)
                  .Concat(merged.Where(e => e.HasChildren));

        /// <summary>
        /// Calcula el ID del nodo delegando en la regla correspondiente a su profundidad.
        /// depth 0 → panel, depth 1 → tipo, depth 2-3 → instancia, depth ≥4 → sin ID.
        /// </summary>
        private static string ComputeNodeId(string name, int depth, string parentIdStr,
                                            ref int childCounter)
        {
            if (depth == 0) return ComputePanelNodeId(name);
            if (depth == 1) return ComputeTypeNodeId(parentIdStr, ref childCounter);
            if (depth <= 3) return ComputeInstanceNodeId(name, parentIdStr);
            return "";
        }

        /// <summary>
        /// Depth 0 — Extrae el último número del nombre del panel.
        /// Ejemplo: "Fire Panel 1-1" → "1". CMD y License no contienen número → "".
        /// </summary>
        private static string ComputePanelNodeId(string name) =>
            ExtractLastNumber(name);

        /// <summary>
        /// Depth 1 — Construye el ID del tipo concatenando el ID del panel y un contador consecutivo.
        /// Ejemplo: panel "1", tercer hijo → "1.3".
        /// </summary>
        private static string ComputeTypeNodeId(string parentIdStr, ref int childCounter)
        {
            var id = string.IsNullOrEmpty(parentIdStr) ? "" : parentIdStr + "." + childCounter;
            childCounter++;
            return id;
        }

        /// <summary>
        /// Depth 2-3 — Construye el ID de instancia concatenando el ID del padre con el nombre numérico.
        /// Ejemplo: padre "1.6", nombre "2" → "1.6.2".
        /// </summary>
        private static string ComputeInstanceNodeId(string name, string parentIdStr) =>
            IsNumeric(name) && !string.IsNullOrEmpty(parentIdStr)
                ? parentIdStr + "." + name
                : "";

        #endregion

        #region Mapeo ItemName → Id para Suscripción

        // ══════════════════════════════════════════════════════════════════
        // BuildNodeIdMapFromTreeJson  →  Dictionary<ItemName, nodeId>
        // Lee el JSON exportado y extrae el mapa ItemName→id sin conectarse al servidor OPC.
        // El JSON es la fuente de verdad: lo que ves en el archivo es
        // exactamente lo que se asigna como ClientHandle en la suscripción.
        // ══════════════════════════════════════════════════════════════════

        // Paso 1 — Punto de entrada: orquesta la lectura, deserialización y extracción.
        public static Dictionary<string, string> BuildNodeIdMapFromTreeJson(string jsonFilePath)
        {
            var json     = ReadJsonFileContent(jsonFilePath);
            var rootNode = DeserializeJsonRootNode(json);
            var map      = CollectItemIdPairsFromNode(rootNode);

            Console.WriteLine($"[OPC_Exporter] Mapa ItemName→id extraído del JSON: {map.Count} ítems.");
            return map;
        }

        // Paso 2 — Lee el texto completo del archivo JSON del disco.
        private static string ReadJsonFileContent(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Archivo de árbol OPC no encontrado: {path}");

            return File.ReadAllText(path, new UTF8Encoding(false));
        }

        // Paso 3 — Deserializa el texto JSON en un árbol de diccionarios anidados.
        private static Dictionary<string, object> DeserializeJsonRootNode(string json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return serializer.Deserialize<Dictionary<string, object>>(json);
        }

        // Paso 4 — Crea el mapa, inicia el recorrido desde la raíz y lo devuelve.
        private static Dictionary<string, string> CollectItemIdPairsFromNode(Dictionary<string, object> rootNode)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            TraverseNode(rootNode, map);
            return map;
        }

        // Paso 5 — Registra el par del nodo actual y desciende recursivamente en sus hijos.
        private static void TraverseNode(Dictionary<string, object> node, Dictionary<string, string> map)
        {
            if (node == null) return;

            RegisterPairIfValid(ReadStringField(node, "itemName"), ReadStringField(node, "id"), map);
            VisitChildNodes(node, map);
        }

        // Paso 6 — Registra el par en el mapa solo si itemName e id son no vacíos.
        //          Nodos raíz, paneles y elementos sin itemName quedan excluidos.
        private static void RegisterPairIfValid(string itemName, string nodeId,
                                                Dictionary<string, string> map)
        {
            if (!string.IsNullOrEmpty(itemName) && !string.IsNullOrEmpty(nodeId))
                map[itemName] = nodeId;
        }

        // Paso 7 — Obtiene el array "children" del nodo y visita cada hijo.
        private static void VisitChildNodes(Dictionary<string, object> node,
                                            Dictionary<string, string> map)
        {
            object childrenObj;
            if (!node.TryGetValue("children", out childrenObj)) return;

            var children = childrenObj as System.Collections.ArrayList;
            if (children == null) return;

            foreach (var child in children)
                TraverseNode(child as Dictionary<string, object>, map);
        }

        // Paso 8 — Lee un campo string del nodo; devuelve "" si no existe o es nulo.
        private static string ReadStringField(Dictionary<string, object> node, string fieldName)
        {
            object value;
            return node.TryGetValue(fieldName, out value) ? value?.ToString() ?? "" : "";
        }

        #endregion

        #region Archivos de Integración

        // ══════════════════════════════════════════════════════════════════
        // ExportIntegrationJson  →  integration_*.json
        // ══════════════════════════════════════════════════════════════════

        public void ExportIntegrationJson(string treeJsonPath, string outputPath, ICollection<string> allowedChildTypes = null)
        {
            Console.WriteLine("[OPC_Exporter] Building OPC integration JSON...");

            var rootNode  = LoadTreeFromJsonFile(treeJsonPath);
            var structure = BuildIntegrationStructureFromTree(rootNode, allowedChildTypes);

            var root = new Dictionary<string, object>
            {
                { "version",       1  },
                { "reset_timeout", 60 },
                { "structure",     structure }
            };

            File.WriteAllText(outputPath, FormatIntegrationJson(root), new UTF8Encoding(false));
            Console.WriteLine("[OPC_Exporter] Integration JSON exported to: " + outputPath);
        }

        private static Dictionary<string, object> LoadTreeFromJsonFile(string treeJsonPath)
        {
            var json = ReadJsonFileContent(treeJsonPath);
            return DeserializeJsonRootNode(json);
        }

        private static IEnumerable<Dictionary<string, object>> GetChildNodes(Dictionary<string, object> node)
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

        private static IEnumerable<Dictionary<string, object>> GetTypeNodesFromTree(Dictionary<string, object> rootNode)
        {
            foreach (var panel in GetChildNodes(rootNode))
                foreach (var typeNode in GetChildNodes(panel))
                    yield return typeNode;
        }

        private static bool IsBranchNode(Dictionary<string, object> node)
        {
            var et = ReadStringField(node, "elementType");
            return et == "branch" || et == "branch|item";
        }

        private static bool IsTypeAllowed(string name, ICollection<string> allowedTypes) =>
            allowedTypes == null || allowedTypes.Count == 0 || allowedTypes.Contains(name);

        private List<object> BuildIntegrationStructureFromTree(
            Dictionary<string, object> rootNode,
            ICollection<string> allowedChildTypes)
        {
            var result    = new List<object>();
            var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var typeNode in GetTypeNodesFromTree(rootNode))
            {
                string name = ReadStringField(typeNode, "name");
                if (!IsTypeAllowed(name, allowedChildTypes)) continue;

                string typeName = _prefix + StripDigits(Normalize(name));
                if (!seenTypes.Add(typeName)) continue;

                var children = BuildInstanceSubtypesFromTree(typeNode, typeName);
                var node     = new Dictionary<string, object>
                {
                    { "parent", "INTEGRATION" },
                    { "type",   typeName }
                };
                if (children.Count > 0)
                    node["children"] = children;

                result.Add(node);
            }

            return result;
        }

        private List<object> BuildInstanceSubtypesFromTree(
            Dictionary<string, object> typeNode,
            string parentTypeName)
        {
            var result    = new List<object>();
            var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var instanceNode in GetChildNodes(typeNode))
            {
                if (!IsBranchNode(instanceNode)) continue;

                string instanceName = ReadStringField(instanceNode, "name");
                string subTypeName  = IsNumeric(instanceName)
                    ? parentTypeName + "_G" + instanceName
                    : _prefix + StripDigits(Normalize(instanceName));

                if (!seenTypes.Add(subTypeName)) continue;

                result.Add(new Dictionary<string, object> { { "type", subTypeName } });
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
        // ExportDdiFile  →  integration_*.ddi
        // ══════════════════════════════════════════════════════════════════

        public void ExportDdiFile(string treeJsonPath, string outputPath, ICollection<string> allowedChildTypes = null)
        {
            Console.WriteLine("[OPC_Exporter] Building DDI file...");

            var rootNode  = LoadTreeFromJsonFile(treeJsonPath);
            var structure = BuildIntegrationStructureFromTree(rootNode, allowedChildTypes);
            var types     = new List<string>();
            CollectUniqueTypes(structure, types);

            var events = CollectEventRowsFromTree(rootNode, allowedChildTypes);

            int c1 = Math.Max(22, Math.Max(
                types.Count  > 0 ? types.Max(t => t.Length)        + 2 : 0,
                events.Count > 0 ? events.Max(e => e.Item1.Length) + 2 : 0));
            int c3base = types.Count  > 0 ? types.Max(t => t.Length) + 2 : 27;
            int c4     = Math.Max(20, events.Count > 0 ? events.Max(e => e.Item3.Length) + 2 : 20);
            int c5     = c4;
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
            Console.WriteLine("[OPC_Exporter] DDI file exported to: " + outputPath);
        }

        private List<Tuple<string, string, string>> CollectEventRowsFromTree(
            Dictionary<string, object> rootNode,
            ICollection<string> allowedChildTypes)
        {
            var events    = new List<Tuple<string, string, string>>();
            var doneTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var typeNode in GetTypeNodesFromTree(rootNode))
            {
                string name = ReadStringField(typeNode, "name");
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

            foreach (var child in GetChildNodes(node))
                CollectUniqueCommandsFromDescendants(child, uniqueCommands);
        }

        private static List<Tuple<string, string>> ParseCommands(string xml)
        {
            var result = new List<Tuple<string, string>>();
            foreach (Match m in Regex.Matches(xml, @"Name=""([^""]+)""[^>]*Description=""([^""]+)"""))
                result.Add(Tuple.Create(m.Groups[1].Value, m.Groups[2].Value));
            return result;
        }

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
        // ExportDbiFile  →  integration_*.dbi
        // ══════════════════════════════════════════════════════════════════

        public void ExportDbiFile(string treeJsonPath, string outputPath, ICollection<string> allowedChildTypes = null)
        {
            Console.WriteLine("[OPC_Exporter] Building DBI file...");

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
            Console.WriteLine("[OPC_Exporter] DBI file exported to: " + outputPath);
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

        private sealed class CmdProperty
        {
            public int    Code        { get; set; }
            public string Description { get; set; }
            public string Value       { get; set; }
            public bool   IsXml       { get; set; }
            public Dictionary<string, string> Commands { get; set; }
        }

        private class MergedElement
        {
            public string Name           { get; set; }
            public bool   HasChildren    { get; set; }
            public bool   IsItem         { get; set; }
            public string BranchItemName { get; set; }
            public string ItemItemName   { get; set; }
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

        #endregion

        #region Helpers Compartidos

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
                Console.WriteLine($"[OPC_Exporter] GetProperties error for '{itemName}': " + ex.Message);
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

        private static string Normalize(string value) =>
            value?.ToUpperInvariant().Replace(' ', '_') ?? string.Empty;

        private static bool IsNumeric(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char c in value)
                if (!char.IsDigit(c)) return false;
            return true;
        }

        private static string ExtractLastNumber(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var matches = Regex.Matches(name, @"\d+");
            return matches.Count > 0 ? matches[matches.Count - 1].Value : "";
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

        #endregion
    }
}
