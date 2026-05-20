using Opc;
using Opc.Da;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace INTG_OPC
{
    /// <summary>
    /// Gestiona el ciclo de vida de una conexión OPC DA Classic:
    /// conexión, suscripción a ítems y desconexión limpia.
    /// </summary>
    public class OpcClient : IDisposable
    {
        // ══════════════════════════════════════════════════════
        // Campos privados — todos de instancia, ninguno static
        // ══════════════════════════════════════════════════════

        private Opc.Da.Server _server;
        private Subscription _subscription;
        private string _serverUrl;
        private bool _disposed;

        private static readonly BrowseFilters FilterAll = new BrowseFilters
        { BrowseFilter = browseFilter.all };
        private static readonly BrowseFilters FilterBranch = new BrowseFilters
        { BrowseFilter = browseFilter.branch };

        private static readonly BrowseFilters FilterItem = new BrowseFilters
        { BrowseFilter = browseFilter.item };

        // ══════════════════════════════════════════════════════
        // Eventos públicos
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// Se dispara cada vez que el servidor OPC detecta un cambio
        /// en un ítem del panel (valor o quality).
        /// </summary>
        public event Action<ItemValueResult[]> PanelChanged;

        // ══════════════════════════════════════════════════════
        // Propiedades públicas de solo lectura
        // ══════════════════════════════════════════════════════

        public bool IsConnected => _server != null && _server.IsConnected;

        public OpcClient()
        {
                
        }

        // ══════════════════════════════════════════════════════
        // Descubrimiento de servidores
        // ══════════════════════════════════════════════════════

        public IReadOnlyList<string> GetAvailableServers(string host = "localhost")
        {
            var result = new List<string>();
            try
            {
                var enumerator = new OpcCom.ServerEnumerator();
                foreach (var spec in new[] { Opc.Specification.COM_DA_20, Opc.Specification.COM_DA_30 })
                {
                    var servers = enumerator.GetAvailableServers(spec, host, null);
                    if (servers == null) continue;
                    foreach (var srv in servers)
                    {
                        var url = srv.Url?.ToString();
                        if (!string.IsNullOrEmpty(url) && !result.Contains(url))
                        {
                            Console.WriteLine($"Url Servidor {srv.Name}: {url}");
                            result.Add(url);
                        }

                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC] Error al enumerar servidores en '{host}': {ex.Message}");
            }
            return result;
        }

        // ══════════════════════════════════════════════════════
        // Conexión / Desconexión
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// Establece conexión con el servidor OPC DA.
        /// </summary>
        /// <param name="opcServerUrl">Ej: opcda://localhost/BOSCH.OpcDaServer.1</param>
        public bool Connect(string opcServerUrl)
        {
            if (string.IsNullOrWhiteSpace(opcServerUrl))
                throw new ArgumentException(
                    "La URL del servidor OPC no puede estar vacía.",
                    nameof(opcServerUrl));

            Console.WriteLine($"[OPC] Conectando a: {opcServerUrl}");

            try
            {
                var url = new Opc.URL(opcServerUrl);
                var factory = new OpcCom.Factory();

                _server = new Opc.Da.Server(factory, null);
                _server.Connect(url, null);

                if (!_server.IsConnected)
                {
                    Console.WriteLine("[OPC] Error: el servidor no reporta conexión activa.");
                    return false;
                }

                _serverUrl = opcServerUrl;
                Console.WriteLine("[OPC] Conexión establecida correctamente.");
                return true;
            }
            catch (ConnectFailedException ex)
            {
                Console.WriteLine($"[OPC] Falló la conexión: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC] Error inesperado al conectar: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Desconecta del servidor OPC y libera el grupo de suscripción.
        /// </summary>
        public void Disconnect()
        {
            ReleaseGroup();

            if (_server == null || !_server.IsConnected) return;

            try
            {
                _server.Disconnect();
                Console.WriteLine("[OPC] Desconectado correctamente.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC] Error al desconectar: {ex.Message}");
            }
            finally
            {
                _server.Dispose();
                _server = null;
            }
        }

        // ══════════════════════════════════════════════════════
        // Suscripción
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// Descubre todos los ítems del servidor, crea el grupo
        /// de suscripción y registra los ítems en él.
        /// </summary>
        public bool InitializeSubscriptionGroup(string groupName = "FPA5000_Group", int updateRateMs = 5000)
        {
            ThrowIfNotConnected();

            Console.WriteLine("[OPC] Inicializando grupo de suscripción...");

            try
            {
                var groupState = BuildGroupState(groupName, updateRateMs);
                _subscription = (Subscription)_server.CreateSubscription(groupState);

                var items = DiscoverItems();

                if (items.Length == 0)
                {
                    Console.WriteLine("[OPC] Advertencia: no se encontraron ítems en el servidor.");
                    return false;
                }

                _subscription.AddItems(items);
                _subscription.DataChanged += (s, r, values) => PanelChanged?.Invoke(values);
                Console.WriteLine($"[OPC] Grupo '{groupName}' inicializado " +
                                  $"con {items.Length} ítem(s).");
                return true;
            }
            catch (ResultIDException ex)
            {
                Console.WriteLine($"[OPC] Error al crear suscripción: {ex.Message} " +
                                  $"| ResultID: {ex.Result}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC] Error inesperado en suscripción: {ex.Message}");
                return false;
            }
        }

        // ══════════════════════════════════════════════════════
        // Lectura
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// Lanza una lectura asíncrona de todos los ítems del grupo.
        /// </summary>
        public void ReadItems()
        {
            ThrowIfNotConnected();

            if (_subscription == null)
                throw new InvalidOperationException(
                    "El grupo no está inicializado. " +
                    "Llama InitializeSubscriptionGroup() primero.");

            Console.WriteLine("[OPC] Iniciando lectura asíncrona...");
            _subscription.Read(
                _subscription.Items,
                100,
                new ReadCompleteEventHandler(OnReadComplete),
                out _);
        }

        // ══════════════════════════════════════════════════════
        // Escritura
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// Escribe un valor en un ítem OPC DA del servidor.
        /// </summary>
        /// <param name="itemName">Nombre completo del ítem (ej: "FPA5000.Zone1.Command")</param>
        /// <param name="value">Valor a escribir (tipo debe ser compatible con el ítem)</param>
        /// <returns>true si la escritura fue exitosa.</returns>
        public bool Write(string itemName, object value)
        {
            ThrowIfNotConnected();

            if (string.IsNullOrWhiteSpace(itemName))
                throw new ArgumentException("El nombre del ítem no puede estar vacío.", nameof(itemName));

            if (_subscription == null)
                throw new InvalidOperationException(
                    "El grupo no está inicializado. Llama InitializeSubscriptionGroup() primero.");

            // Busca el ítem en la suscripción activa para obtener su ServerHandle
            var subscriptionItem = _subscription.Items
                .FirstOrDefault(i => i.ItemName == itemName);

            if (subscriptionItem == null)
            {
                Console.WriteLine($"[OPC] Write: ítem '{itemName}' no encontrado en la suscripción.");
                return false;
            }

            try
            {
                var itemValue = new ItemValue
                {
                    ServerHandle = subscriptionItem.ServerHandle,
                    ItemName     = subscriptionItem.ItemName,
                    Value        = value
                };

                var result = _subscription.Write(new[] { itemValue });
                Console.WriteLine($"[OPC] Write OK: {itemName} = {value}");
                Console.WriteLine($"[OPC] result: {result}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC] Error al escribir '{itemName}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Escribe múltiples valores en el servidor en una sola llamada.
        /// </summary>
        /// <param name="itemValues">Array de ItemValue con ItemName y Value ya asignados.</param>
        /// <returns>Array de resultados por ítem, o null si ocurrió una excepción.</returns>
        public IdentifiedResult[] WriteItems(ItemValue[] itemValues)
        {
            ThrowIfNotConnected();

            if (itemValues == null || itemValues.Length == 0)
                throw new ArgumentException(
                    "Debe proporcionar al menos un ítem para escribir.", nameof(itemValues));

            try
            {
                var results = _server.Write(itemValues);

                if (results != null)
                {
                    foreach (var r in results)
                    {
                        var status = r.ResultID.Succeeded() ? "OK  " : "FAIL";
                        Console.WriteLine($"[OPC] Write {status}: {r.ItemName} — {r.ResultID}");
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC] Error al escribir ítems: {ex.Message}");
                return null;
            }
        }
                


        // ══════════════════════════════════════════════════════
        // IDisposable
        // ══════════════════════════════════════════════════════

        public void Dispose()
        {
            if (_disposed) return;
            Disconnect();
            _disposed = true;
        }

        // ══════════════════════════════════════════════════════
        // Métodos privados
        // ══════════════════════════════════════════════════════

        private static SubscriptionState BuildGroupState(string name, int updateRateMs)
        {
            return new SubscriptionState
            {
                //Name = name,
                Name = $"{name}_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                Active = true,
                UpdateRate = updateRateMs,
                Deadband = 0
            };
        }

        /// <summary>
        /// Recorre el árbol OPC y retorna todos los ítems descubiertos,
        /// sin duplicados.
        /// </summary>
        private Item[] DiscoverItems()
        {
            Console.WriteLine("[OPC] Descubriendo ítems...");

            var itemList = new List<Item>();
            BrowsePosition position;

            //BrowseRecursive(null, FilterBranch, out position, itemList);
            BrowseRecursive(null, FilterItem, out position, itemList);

            var unique = itemList
                .GroupBy(i => i.ItemName)
                .Select(g => g.First())
                .ToArray();

            Console.WriteLine($"[OPC] Ítems descubiertos: {unique.Length}");
            return unique;
        }

        /// <summary>
        /// Recorre el árbol OPC recursivamente acumulando ítems en la lista.
        /// </summary>
        private void BrowseRecursive(
            ItemIdentifier itemId,
            BrowseFilters filter,
            out BrowsePosition position,
            List<Item> accumulator)
        {
            // Recorre ramas
            var branches = _server.Browse(itemId, filter, out position);
            if (branches != null)
            {
                foreach (var branch in branches)
                {
                    if (branch.HasChildren)
                    {
                        BrowsePosition childPosition;
                        BrowseRecursive(
                            new ItemIdentifier(branch.ItemName),
                            FilterBranch,
                            out childPosition,
                            accumulator);
                    }
                    else
                    {
                        accumulator.Add(new Item { ItemName = branch.ItemName });
                    }
                }
            }

            // Recoge ítems directos del nodo actual
            var children = _server.Browse(itemId, FilterAll, out position);
            if (children != null)
            {
                foreach (var child in children)
                {
                    if (child.IsItem)
                        accumulator.Add(new Item { ItemName = child.ItemName });
                }
            }
        }

        private void ReleaseGroup()
        {
            if (_subscription == null) return;

            try
            {
                if (_server != null && _server.IsConnected)
                    _server.CancelSubscription(_subscription);

                _subscription.Dispose();
                Console.WriteLine("[OPC] Grupo de suscripción liberado.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC] Error al liberar grupo: {ex.Message}");
            }
            finally
            {
                _subscription = null;
            }
        }

        private void ThrowIfNotConnected()
        {
            if (!IsConnected)
                throw new InvalidOperationException(
                    "No hay conexión activa con el servidor OPC. " +
                    "Llama Connect() primero.");
        }

        private void OnReadComplete(object clientHandle, ItemValueResult[] results)
        {
            Console.WriteLine("[OPC] Lectura completada:");
            foreach (var result in results)
                Console.WriteLine($"  {result.ItemName} = {result.Value}");
            Console.WriteLine();
        }


        //----------------------------------------------------------------
        public void PrintItemProperties(string itemName, string indent)
        {
            try
            {
                var results = _server.GetProperties(new[] { new ItemIdentifier(itemName) }, null, true);
                if (results == null || results.Length == 0) return;

                foreach (ItemProperty prop in results[0])
                {
                    if (prop.ResultID.Succeeded())
                    {
                        string desc = string.IsNullOrEmpty(prop.Description) ? $"Property {prop.ID.Code}" : prop.Description;
                        string value = prop.Value?.ToString() ?? "(null)";
                        Console.WriteLine($"{indent}    [{prop.ID.Code,5}] {desc,-30} = {value}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{indent}    (Error al leer propiedades: {ex.Message})");
            }
        }

        // ══════════════════════════════════════════════════════
        // Exportación del árbol a JSON
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// Recorre el árbol OPC y guarda un JSON con la jerarquía completa.
        /// El archivo se llama treeAvenarGroup_yyyyMMdd_HHmmss.json
        /// </summary>
        /// <param name="outputFolder">Carpeta destino; null = directorio del ejecutable.</param>
        /// <returns>Ruta completa del archivo generado, o null si ocurrió un error.</returns>
        public string ExportTreeToJson(string outputFolder = null)
        {
            ThrowIfNotConnected();

            if (string.IsNullOrWhiteSpace(outputFolder))
                outputFolder = AppDomain.CurrentDomain.BaseDirectory;

            try
            {
                Directory.CreateDirectory(outputFolder);

                var timestamp = DateTime.Now;
                var fileName  = $"treeAvenarGroup_{timestamp:yyyyMMdd_HHmmss}.json";
                var filePath  = Path.Combine(outputFolder, fileName);

                var root = BuildTreeNode(null);
                var json = SerializeTree(root, _serverUrl, timestamp);

                File.WriteAllText(filePath, json, Encoding.UTF8);
                Console.WriteLine($"[OPC] Árbol exportado: {filePath}");
                return filePath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC] Error al exportar árbol: {ex.Message}");
                return null;
            }
        }

        private OpcTreeNode BuildTreeNode(ItemIdentifier nodeId)
        {
            var node = new OpcTreeNode
            {
                Name     = LeafName(nodeId?.ItemName),
                ItemName = nodeId?.ItemName ?? string.Empty,
                IsItem   = false
            };

            try
            {
                BrowsePosition pos;

                var branches = _server.Browse(nodeId, FilterBranch, out pos);
                if (branches != null)
                    foreach (var b in branches)
                        node.Children.Add(BuildTreeNode(new ItemIdentifier(b.ItemName)));

                var items = _server.Browse(nodeId, FilterItem, out pos);
                if (items != null)
                    foreach (var item in items)
                        node.Children.Add(new OpcTreeNode
                        {
                            Name     = LeafName(item.ItemName),
                            ItemName = item.ItemName,
                            IsItem   = true
                        });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC] Error al explorar nodo '{node.ItemName}': {ex.Message}");
            }

            return node;
        }

        private static string SerializeTree(OpcTreeNode root, string serverUrl, DateTime timestamp)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"createdAt\": \"{timestamp:yyyy-MM-ddTHH:mm:ss}\",");
            sb.AppendLine($"  \"server\": {ToJsonString(serverUrl)},");
            sb.Append("  \"tree\": ");
            AppendNode(sb, root, 1);
            sb.AppendLine();
            sb.Append("}");
            return sb.ToString();
        }

        private static void AppendNode(StringBuilder sb, OpcTreeNode node, int depth)
        {
            var pad  = new string(' ', depth * 2);
            var pad1 = new string(' ', (depth + 1) * 2);

            sb.AppendLine("{");
            sb.AppendLine($"{pad1}\"name\": {ToJsonString(node.Name)},");
            sb.AppendLine($"{pad1}\"itemName\": {ToJsonString(node.ItemName)},");
            sb.AppendLine($"{pad1}\"isItem\": {(node.IsItem ? "true" : "false")},");
            sb.Append($"{pad1}\"children\": ");

            if (node.Children.Count == 0)
            {
                sb.AppendLine("[]");
            }
            else
            {
                sb.AppendLine("[");
                for (int i = 0; i < node.Children.Count; i++)
                {
                    sb.Append(pad1 + "  ");
                    AppendNode(sb, node.Children[i], depth + 2);
                    sb.AppendLine(i < node.Children.Count - 1 ? "," : string.Empty);
                }
                sb.AppendLine($"{pad1}]");
            }

            sb.Append($"{pad}}}");
        }

        private static string ToJsonString(string value)
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

        private static string LeafName(string itemName) =>
            string.IsNullOrEmpty(itemName) ? "root" : itemName.Split('.').Last();

        // ══════════════════════════════════════════════════════
        // Exportación del árbol limpio (solo ítems + propiedades)
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// Exporta solo los ítems del árbol OPC conservando la jerarquía de ramas.
        /// Cada ítem incluye todas sus propiedades disponibles.
        /// El archivo se llama treeAvenarGroup_clean_yyyyMMdd_HHmmss.json
        /// </summary>
        /// <param name="outputFolder">Carpeta destino; null = directorio del ejecutable.</param>
        /// <returns>Ruta completa del archivo generado, o null si ocurrió un error.</returns>
        public string ExportCleanTreeToJson(string outputFolder = null)
        {
            ThrowIfNotConnected();

            if (string.IsNullOrWhiteSpace(outputFolder))
                outputFolder = AppDomain.CurrentDomain.BaseDirectory;

            try
            {
                Directory.CreateDirectory(outputFolder);

                var timestamp = DateTime.Now;
                var fileName  = $"treeAvenarGroup_clean_{timestamp:yyyyMMdd_HHmmss}.json";
                var filePath  = Path.Combine(outputFolder, fileName);

                var rawRoot   = BuildTreeNode(null);
                var cleanRoot = BuildCleanNode(rawRoot);
                AssignIds(cleanRoot);
                var json      = SerializeCleanTree(cleanRoot, _serverUrl, timestamp);

                File.WriteAllText(filePath, json, Encoding.UTF8);
                Console.WriteLine($"[OPC] Árbol limpio exportado: {filePath}");
                return filePath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC] Error al exportar árbol limpio: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Transforma un nodo del árbol raw en un nodo limpio.
        /// Fusiona hijos que comparten nombre (rama + ítem → nodo único con
        /// properties del ítem y children de la rama).
        /// </summary>
        private CleanOpcNode BuildCleanNode(OpcTreeNode rawNode)
        {
            if (rawNode.IsItem)
            {
                return new CleanOpcNode
                {
                    Name        = rawNode.Name,
                    ElementType = "item",
                    Properties  = FetchAllProperties(rawNode.ItemName)
                };
            }

            // Procesa hijos y fusiona los que tengan el mismo nombre
            var orderedKeys = new List<string>();
            var mergeMap    = new Dictionary<string, CleanOpcNode>(StringComparer.Ordinal);

            foreach (var child in rawNode.Children)
            {
                var cleanChild = BuildCleanNode(child);
                if (cleanChild == null) continue;

                if (mergeMap.TryGetValue(cleanChild.Name, out var existing))
                {
                    // Toma las propiedades del ítem si la rama aún no las tiene
                    if ((existing.Properties == null || existing.Properties.Count == 0) && cleanChild.Properties?.Count > 0)
                        existing.Properties = cleanChild.Properties;

                    foreach (var grandchild in cleanChild.Children)
                        existing.Children.Add(grandchild);

                    if (existing.Children.Count > 0)
                        existing.ElementType = "branch";
                }
                else
                {
                    mergeMap[cleanChild.Name] = cleanChild;
                    orderedKeys.Add(cleanChild.Name);
                }
            }

            if (orderedKeys.Count == 0) return null;

            var node = new CleanOpcNode { Name = rawNode.Name, ElementType = "branch" };
            foreach (var key in orderedKeys)
                node.Children.Add(mergeMap[key]);

            return node;
        }

        private static void AssignIds(CleanOpcNode node, string parentId = "")
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                var child = node.Children[i];
                child.Id = string.IsNullOrEmpty(parentId)
                    ? (i + 1).ToString()
                    : $"{parentId}.{i + 1}";
                AssignIds(child, child.Id);
            }
        }

        private List<OpcProperty> FetchAllProperties(string itemName)
        {
            try
            {
                var results = _server.GetProperties(
                    new[] { new ItemIdentifier(itemName) }, null, true);

                if (results == null || results.Length == 0)
                    return new List<OpcProperty>();

                var props = new List<OpcProperty>();
                foreach (ItemProperty prop in results[0])
                {
                    if (!prop.ResultID.Succeeded()) continue;

                    props.Add(new OpcProperty
                    {
                        Code        = prop.ID.Code,
                        Description = string.IsNullOrWhiteSpace(prop.Description)
                            ? $"property_{prop.ID.Code}"
                            : prop.Description,
                        Value       = prop.Value?.ToString() ?? string.Empty
                    });
                }
                return props;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC] Error al leer propiedades de '{itemName}': {ex.Message}");
                return new List<OpcProperty>();
            }
        }

        private static string SerializeCleanTree(CleanOpcNode root, string serverUrl, DateTime timestamp)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"createdAt\": \"{timestamp:yyyy-MM-ddTHH:mm:ss}\",");
            sb.AppendLine($"  \"server\": {ToJsonString(serverUrl)},");
            sb.Append("  \"tree\": ");
            AppendCleanNode(sb, root, 1);
            sb.AppendLine();
            sb.Append("}");
            return sb.ToString();
        }

        private static void AppendCleanNode(StringBuilder sb, CleanOpcNode node, int depth)
        {
            var pad  = new string(' ', depth * 2);
            var pad1 = new string(' ', (depth + 1) * 2);

            sb.AppendLine("{");
            sb.AppendLine($"{pad1}\"name\": {ToJsonString(node.Name)},");
            sb.AppendLine($"{pad1}\"id\": {ToJsonString(node.Id)},");
            sb.AppendLine($"{pad1}\"elementType\": \"{node.ElementType}\",");

            // properties — siempre presente, vacío si es rama pura
            var props = node.Properties ?? new List<OpcProperty>();
            sb.AppendLine($"{pad1}\"properties\": [");
            for (int i = 0; i < props.Count; i++)
            {
                var comma = i < props.Count - 1 ? "," : string.Empty;
                var p = props[i];
                sb.AppendLine($"{pad1}  {{ \"code\": {p.Code}, \"description\": {ToJsonString(p.Description)}, \"value\": {ToJsonString(p.Value)} }}{comma}");
            }
            sb.AppendLine($"{pad1}],");

            // children — siempre presente, vacío si es ítem puro
            sb.Append($"{pad1}\"children\": ");
            if (node.Children.Count == 0)
            {
                sb.AppendLine("[]");
            }
            else
            {
                sb.AppendLine("[");
                for (int i = 0; i < node.Children.Count; i++)
                {
                    sb.Append(pad1 + "  ");
                    AppendCleanNode(sb, node.Children[i], depth + 2);
                    sb.AppendLine(i < node.Children.Count - 1 ? "," : string.Empty);
                }
                sb.AppendLine($"{pad1}]");
            }

            sb.Append($"{pad}}}");
        }

        private sealed class OpcProperty
        {
            public int    Code        { get; set; }
            public string Description { get; set; }
            public string Value       { get; set; }
        }

        private sealed class CleanOpcNode
        {
            public string Name        { get; set; }
            public string Id          { get; set; }
            public string ElementType { get; set; }
            public List<OpcProperty> Properties { get; set; }
            public List<CleanOpcNode> Children { get; } = new List<CleanOpcNode>();
        }

        private sealed class OpcTreeNode
        {
            public string           Name     { get; set; }
            public string           ItemName { get; set; }
            public bool             IsItem   { get; set; }
            public List<OpcTreeNode> Children { get; } = new List<OpcTreeNode>();
        }

        //----------------------------------------------------------------
        public Dictionary<string, string> GetItemProperties(string itemName, int NumPropertie)
        {
            try
            {
                var id= new PropertyID(NumPropertie);
                var results = _server.GetProperties(new[] { new ItemIdentifier(itemName) }, null, true);
                if (results == null || results.Length == 0) return null;


                var prop = results[0]
                      .Cast<ItemProperty>()
                      .FirstOrDefault(p => p.ID.Code == NumPropertie);



                if (prop?.Value is string raw)
                {
                    if (raw.TrimStart().StartsWith("<"))
                    {
                        // Es XML con comandos → parsear
                        var doc = XDocument.Parse(raw);
                        XNamespace ns = "file:///S3K/Proxyverwalter";
                        var commands = doc.Descendants(ns + "Command")
                                          .ToDictionary(e => e.Attribute("Name").Value, e => e.ToString());
                        return commands;
                    }
                    return prop != null ? new Dictionary<string, string> { { prop.Description ?? $"Property {prop.ID.Code}", prop.Value?.ToString() ?? "(null)" } } : null;
                }
                return null;

            }
            catch (Exception ex)
            {
                Console.WriteLine($"   (Error al leer propiedades: {ex.Message})");
                return null;
            }
        }
    }
}
