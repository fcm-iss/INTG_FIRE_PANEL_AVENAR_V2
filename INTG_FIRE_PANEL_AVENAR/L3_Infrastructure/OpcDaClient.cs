using Opc;
using Opc.Da;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace INTG_OPC
{
    /// <summary>
    /// Gestiona el ciclo de vida de una conexión OPC DA Classic:
    /// conexión, suscripción a ítems y desconexión limpia.
    /// </summary>
    public class OpcDaClient : IDisposable
    {
        #region Campos y Constructor

        private Opc.Da.Server _opcDaServer;
        private Subscription _subscription;
        private string _serverUrl;
        private bool _disposed;
        private OpcGroupIdTable _groupIds;

        private static readonly BrowseFilters FilterAll    = new BrowseFilters { BrowseFilter = browseFilter.all };
        private static readonly BrowseFilters FilterBranch = new BrowseFilters { BrowseFilter = browseFilter.branch };
        private static readonly BrowseFilters FilterItem   = new BrowseFilters { BrowseFilter = browseFilter.item };

        public bool IsConnected => _opcDaServer != null && _opcDaServer.IsConnected;

        public event Action<ItemValueResult[]> PanelChanged;

        public OpcDaClient() { }

        #endregion

        #region Descubrimiento de Servidores

        public IReadOnlyList<string> GetAvailableServers(string host = "localhost")
        {
            var urls = new List<string>();
            try
            {
                var enumerator = new OpcCom.ServerEnumerator();
                foreach (var spec in new[] { Opc.Specification.COM_DA_20, Opc.Specification.COM_DA_30 })
                    AppendUniqueServerUrls(enumerator, spec, host, urls);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC_Client]\tError al enumerar servidores en '{host}': {ex.Message}");
            }
            return urls;
        }

        private static void AppendUniqueServerUrls(
            OpcCom.ServerEnumerator enumerator,
            Opc.Specification spec,
            string host,
            List<string> urls)
        {
            var servers = enumerator.GetAvailableServers(spec, host, null);
            if (servers == null) return;

            foreach (var srv in servers)
            {
                var url = srv.Url?.ToString();
                if (!string.IsNullOrEmpty(url) && !urls.Contains(url))
                {
                    Console.WriteLine($"Url Servidor {srv.Name}: {url}");
                    urls.Add(url);
                }
            }
        }

        #endregion

        #region Conexión y Desconexión

        /// <summary>
        /// Establece conexión con el servidor OPC DA.
        /// </summary>
        /// <param name="opcServerUrl">Ej: opcda://localhost/BOSCH.OpcDaServer.1</param>
        public bool Connect(string opcServerUrl)
        {    
            try
            {
                ValidateServerUrl(opcServerUrl);
                CreateAndConnectServer(opcServerUrl);
                _serverUrl = opcServerUrl;
                return true;
            }
            catch (ConnectFailedException ex)
            {
                Console.WriteLine($"[OPC_Client]\tFalló la conexión: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC_Client]\tError inesperado al conectar: {ex.Message}");
                return false;
            }
        }

        private static void ValidateServerUrl(string opcServerUrl)
        {
            if (string.IsNullOrWhiteSpace(opcServerUrl))
                throw new ArgumentException(
                    "La URL del servidor OPC no puede estar vacía.",
                    nameof(opcServerUrl));
        }

        private void CreateAndConnectServer(string opcServerUrl)
        {
            Console.WriteLine($"[OPC_Client]\tConectando a: {opcServerUrl}");
            var url     = new Opc.URL(opcServerUrl);
            var factory = new OpcCom.Factory();
            _opcDaServer = new Opc.Da.Server(factory, null);
            _opcDaServer.Connect(url, BuildOpcConnectData());

            if (!_opcDaServer.IsConnected)
                throw new InvalidOperationException("El servidor no reporta conexión activa.");

            Console.WriteLine("[OPC_Client]\tConexión establecida correctamente.");
        }

        /// <summary>
        /// Arma las credenciales DCOM para la conexión OPC a partir de App.config.
        /// Si opc_username no está configurado, devuelve null (usa la identidad del proceso).
        /// </summary>
        private static Opc.ConnectData BuildOpcConnectData()
        {
            var username = ConfigurationManager.AppSettings["opc_username"];
            if (string.IsNullOrWhiteSpace(username))
                return null;

            var password = ConfigurationManager.AppSettings["opc_password"] ?? "";
            var domain   = ConfigurationManager.AppSettings["opc_domain"]   ?? "";
            var credentials = new System.Net.NetworkCredential(username, password, domain);
            return new Opc.ConnectData(credentials);
        }

        /// <summary>
        /// Desconecta del servidor OPC y libera el grupo de suscripción.
        /// </summary>
        public void Disconnect()
        {
            ReleaseGroup();
            ReleaseServerConnection();
        }

        private void ReleaseServerConnection()
        {
            if (_opcDaServer == null || !_opcDaServer.IsConnected) return;

            try
            {
                _opcDaServer.Disconnect();
                Console.WriteLine("[OPC_Client]\tDesconectado correctamente.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC_Client]\tError al desconectar: {ex.Message}");
            }
            finally
            {
                _opcDaServer.Dispose();
                _opcDaServer = null;
            }
        }

        #endregion

        #region Monitoreo

        /// <summary>
        /// Inicia el monitoreo de cambios del panel: crea el grupo de suscripción OPC,
        /// registra los ítems con sus ClientHandles y comienza a escuchar eventos.
        /// </summary>
        public bool StartIntegrationMonitoring(Dictionary<string, string> nodeIdMapByItemName = null, int updateRateMs = 1000)
        {
            ThrowIfNotConnected();
            Console.WriteLine("[OPC_Client]\tInicializando grupo de suscripción...");

            try
            {
                CreateSubscriptionGroup(groupName: "FPA5000_Group", updateRateMs: updateRateMs);

                if (!RegisterItemsInGroup(nodeIdMapByItemName, groupName: "FPA5000_Group"))
                    return false;

                AttachDataChangedHandler();
                return true;
            }
            catch (ResultIDException ex)
            {
                Console.WriteLine($"[OPC_Client]\tError al crear suscripción: {ex.Message} " +
                                  $"| ResultID: {ex.Result}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC_Client]\tError inesperado en suscripción: {ex.Message}");
                return false;
            }
        }

        private void CreateSubscriptionGroup(string groupName, int updateRateMs)
        {
            var groupState = BuildGroupState(groupName, updateRateMs);
            _subscription  = (Subscription)_opcDaServer.CreateSubscription(groupState);
        }

        private bool RegisterItemsInGroup(Dictionary<string, string> itemIdMap, string groupName)
        {
            var items = DiscoverItems(itemIdMap);

            if (items.Length == 0)
            {
                Console.WriteLine("[OPC_Client]\tAdvertencia: no se encontraron ítems en el servidor.");
                return false;
            }

            _subscription.AddItems(items);
            Console.WriteLine($"[OPC_Client]\tGrupo '{groupName}' inicializado con {items.Length} ítem(s).");
            return true;
        }

        private void AttachDataChangedHandler()
        {
            _subscription.DataChanged += (s, r, values) =>
            {
                try   { if (values != null) PanelChanged?.Invoke(values); }
                catch (Exception ex) { Console.WriteLine($"[OPC_Client]\tError en DataChanged: {ex.Message}"); }
            };
        }

        private static SubscriptionState BuildGroupState(string name, int updateRateMs) =>
            new SubscriptionState
            {
                Name       = $"{name}_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                Active     = true,
                UpdateRate = updateRateMs,
                Deadband   = 0
            };

        private Item[] DiscoverItems(Dictionary<string, string> itemIdMap = null)
        {
            Console.WriteLine("[OPC_Client]\tDescubriendo ítems...");

            var rawItems = BrowseAllItems(itemIdMap);
            var unique   = DeduplicateByItemName(rawItems);

            Console.WriteLine($"[OPC_Client]\tÍtems descubiertos: {unique.Length}");
            return unique;
        }

        private List<Item> BrowseAllItems(Dictionary<string, string> itemIdMap)
        {
            var itemList = new List<Item>();
            BrowseRecursive(null, FilterItem, itemList, itemIdMap);
            return itemList;
        }

        private static Item[] DeduplicateByItemName(List<Item> items) =>
            items.GroupBy(i => i.ItemName).Select(g => g.First()).ToArray();

        private void BrowseRecursive(
            ItemIdentifier itemId,
            BrowseFilters filter,
            List<Item> accumulator,
            Dictionary<string, string> itemIdMap = null)
        {
            AccumulateBranchItems(itemId, filter, accumulator, itemIdMap);
            AccumulateDirectItems(itemId, accumulator, itemIdMap);
        }

        private void AccumulateBranchItems(
            ItemIdentifier itemId,
            BrowseFilters filter,
            List<Item> accumulator,
            Dictionary<string, string> itemIdMap)
        {
            BrowsePosition pos;
            var branches = _opcDaServer.Browse(itemId, filter, out pos);
            if (branches == null) return;

            foreach (var branch in branches)
            {
                if (branch.HasChildren)
                    BrowseRecursive(new ItemIdentifier(branch.ItemName), FilterBranch, accumulator, itemIdMap);
                else
                    accumulator.Add(MakeItem(branch.ItemName, itemIdMap));
            }
        }

        private void AccumulateDirectItems(
            ItemIdentifier itemId,
            List<Item> accumulator,
            Dictionary<string, string> itemIdMap)
        {
            BrowsePosition pos;
            var children = _opcDaServer.Browse(itemId, FilterAll, out pos);
            if (children == null) return;

            foreach (var child in children)
            {
                if (child.IsItem)
                    accumulator.Add(MakeItem(child.ItemName, itemIdMap));
            }
        }

        private static Item MakeItem(string itemName, Dictionary<string, string> itemIdMap)
        {
            string treeId = "";
            itemIdMap?.TryGetValue(itemName, out treeId);
            return new Item { ItemName = itemName, ClientHandle = treeId ?? "" };
        }

        private void ReleaseGroup()
        {
            if (_subscription == null) return;

            try
            {
                CancelServerSubscription();
                _subscription.Dispose();
                Console.WriteLine("[OPC_Client]\tGrupo de suscripción liberado.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC_Client]\tError al liberar grupo: {ex.Message}");
            }
            finally
            {
                _subscription = null;
            }
        }

        private void CancelServerSubscription()
        {
            if (_opcDaServer != null && _opcDaServer.IsConnected)
                _opcDaServer.CancelSubscription(_subscription);
        }

        #endregion

        #region Lectura y Escritura

        /// <summary>
        /// Lanza una lectura asíncrona de todos los ítems del grupo.
        /// </summary>
        public void ReadItems()
        {
            ThrowIfNotConnected();
            ThrowIfSubscriptionNotInitialized();

            Console.WriteLine("[OPC_Client]\tIniciando lectura asíncrona...");
            _subscription.Read(
                _subscription.Items,
                100,
                new ReadCompleteEventHandler(OnReadComplete),
                out _);
        }

        private void OnReadComplete(object clientHandle, ItemValueResult[] results)
        {
            Console.WriteLine("[OPC_Client]\tLectura completada:");
            foreach (var result in results)
                Console.WriteLine($"  {result.ItemName} = {result.Value}");
            Console.WriteLine();
        }

        /// <summary>
        /// Escribe un valor en un ítem OPC DA del servidor.
        /// </summary>
        public bool Write(string itemName, object value)
        {
            ThrowIfNotConnected();
            ValidateWriteArguments(itemName);

            var subscriptionItem = FindSubscribedItem(itemName);
            if (subscriptionItem == null)
            {
                Console.WriteLine($"[OPC_Client]\tWrite: ítem '{itemName}' no encontrado en la suscripción.");
                return false;
            }

            return ExecuteWrite(subscriptionItem, value, itemName);
        }

        private void ValidateWriteArguments(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName))
                throw new ArgumentException(
                    "El nombre del ítem no puede estar vacío.", nameof(itemName));

            ThrowIfSubscriptionNotInitialized();
        }

        private Item FindSubscribedItem(string itemName) =>
            _subscription.Items.FirstOrDefault(i => i.ItemName == itemName);

        private static ItemValue BuildItemValue(Item subscriptionItem, object value) =>
            new ItemValue
            {
                ServerHandle = subscriptionItem.ServerHandle,
                ItemName     = subscriptionItem.ItemName,
                Value        = value
            };

        private bool ExecuteWrite(Item subscriptionItem, object value, string itemName)
        {
            try
            {
                var itemValue = BuildItemValue(subscriptionItem, value);
                var result    = _subscription.Write(new[] { itemValue });
                Console.WriteLine($"[OPC_Client]\tWrite OK: {itemName} = {value}");
                Console.WriteLine($"[OPC_Client]\tresult: {result[0].ResultID}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC_Client]\tError al escribir '{itemName}': {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Helpers Internos

        private void ThrowIfNotConnected()
        {
            if (!IsConnected)
                throw new InvalidOperationException(
                    "No hay conexión activa con el servidor OPC. " +
                    "Llama Connect() primero.");
        }

        private void ThrowIfSubscriptionNotInitialized()
        {
            if (_subscription == null)
                throw new InvalidOperationException(
                    "El grupo no está inicializado. " +
                    "Llama StartIntegrationMonitoring() primero.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            Disconnect();
            _disposed = true;
        }

        public bool WriteCommand(string commandXml)
        {
            ThrowIfNotConnected();
            ThrowIfSubscriptionNotInitialized();

            var cmdItem = FindSubscribedItem("CMD");
            if (cmdItem == null)
            {
                Console.WriteLine("[OPC_Client]\tWriteCommand: ítem 'CMD' no encontrado en la suscripción.");
                return false;
            }

            return ExecuteCommandWrite(cmdItem, commandXml);
        }

        private bool ExecuteCommandWrite(Item cmdItem, string commandXml)
        {
            try
            {
                var itemValue = new ItemValue
                {
                    ServerHandle = cmdItem.ServerHandle,
                    ItemName     = cmdItem.ItemName,
                    Value        = commandXml
                };
                _subscription.Write(new[] { itemValue });
                Console.WriteLine($"[OPC_Client]\tWriteCommand OK: {cmdItem.ItemName}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC_Client]\tError al enviar comando: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Browse

        public BrowseElement[] BrowseLevel(ItemIdentifier parentId)
        {
            try
            {
                BrowsePosition pos;
                return _opcDaServer.Browse(parentId, FilterAll, out pos);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC_Client]\tBrowse error en '{parentId?.ItemName ?? "root"}': {ex.Message}");
                return null;
            }
        }

        public ItemPropertyCollection GetItemProperties(string itemName)
        {
            try
            {
                var results = _opcDaServer.GetProperties(
                    new[] { new ItemIdentifier(itemName) }, null, true);
                return results != null && results.Length > 0 ? results[0] : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC_Client]\tGetProperties error para '{itemName}': {ex.Message}");
                return null;
            }
        }

        #endregion

        #region BuildTree

        /// <summary>
        /// Construye el árbol OPC completo navegando el servidor y retorna la estructura
        /// en memoria lista para serializar a JSON.
        /// </summary>
        public Dictionary<string, object> BuildTree(string prefix, string serverUrl, string idUinP)
        {
            _groupIds = OpcGroupIdTable.Load();
            var tree  = new Dictionary<string, object>
            {
                { "name",        "root" },
                { "elementType", "branch" },
                { "itemName",    "" },
                { "securosId",   "" },
                { "server",      serverUrl },
                { "prefix",      prefix },
                { "idUinP",      idUinP },
                { "children",    BuildChildNodes(null, prefix) }
            };
            _groupIds.SaveIfChanged();
            return tree;
        }

        private List<object> BuildChildNodes(ItemIdentifier parentId, string prefix, int depth = 0,
                                             string parentIdStr = "",
                                             Dictionary<string, string> inheritedFields = null)
        {
            var elements = BrowseLevelSafe(parentId);

            if (elements == null || elements.Length == 0)
                return new List<object>();

            LogBrowsedElements(elements);

            var merged      = MergeElementsByName(elements);
            var treeOrdered = OrderElementsForTraversal(merged);

            var treeNodesAtCurrentLevel = new List<object>();

            foreach (var element in treeOrdered)
            {
                string nodeId = ComputeNodeId(element.Name, depth, parentIdStr);
                var    node   = BuildNodeEntry(element, nodeId, depth, parentIdStr, prefix, inheritedFields);
                treeNodesAtCurrentLevel.Add(node);
            }

            return treeNodesAtCurrentLevel;
        }

        private BrowseElement[] BrowseLevelSafe(ItemIdentifier parentId)
        {
            try
            {
                return BrowseLevel(parentId);
            }
            catch (Exception ex)
            {
                var nodeName = parentId?.ItemName ?? "root";
                Console.WriteLine($"[OPC_Client]\tBrowse error at '{nodeName}': {ex.Message}");
                return null;
            }
        }

        private static void LogBrowsedElements(BrowseElement[] elements)
        {
            foreach (var e in elements)
                Console.WriteLine($"[OPC_Client]\tName='{e.Name}' ItemName='{e.ItemName}' HasChildren={e.HasChildren} IsItem={e.IsItem}");
        }

        private Dictionary<string, object> BuildNodeEntry(MergedElement element, string nodeId,
                                                          int depth, string parentIdStr,
                                                          string prefix,
                                                          Dictionary<string, string> inheritedFields = null)
        {
            var elementType       = DetermineElementType(element);
            var itemName          = ResolveItemName(element);
            var integrationFields = ComputeIntegrationFields(element.Name, elementType, depth, parentIdStr, prefix, inheritedFields);
            var securosParentId   = ResolveSecurosParentId(element, integrationFields);
            var node              = CreateBaseNodeFields(element.Name, elementType, itemName, nodeId, securosParentId);

            AttachIntegrationFields(node, integrationFields);
            if (depth == 0 && elementType == "branch")
                node["objType"] = prefix + StripDigits(Normalize(element.Name));
            AttachItemPropertiesIfItem(node, element, itemName);
            AttachChildrenIfBranch(node, element, depth, nodeId, prefix, integrationFields);

            return node;
        }

        private static string ResolveSecurosParentId(MergedElement element, Dictionary<string, string> integrationFields)
        {
            if (!element.IsItem || integrationFields == null) return null;
            string panelId;
            integrationFields.TryGetValue("panelId", out panelId);
            return panelId;
        }

        private Dictionary<string, string> ComputeIntegrationFields(string name, string elementType,
                                                                     int depth, string parentIdStr,
                                                                     string prefix,
                                                                     Dictionary<string, string> inherited)
        {
            if (string.IsNullOrEmpty(parentIdStr)) return null;
            if (depth == 1)                        return BuildOwnIntegrationFields(name, elementType, parentIdStr, prefix);
            if (depth > 1 && inherited != null)    return inherited;
            return null;
        }

        private static Dictionary<string, string> BuildOwnIntegrationFields(string name, string elementType,
                                                                             string panelId, string prefix)
        {
            bool isBranch = elementType == "branch" || elementType == "branch|item";
            var fields = new Dictionary<string, string>
            {
                ["objType"]  = prefix + Normalize(name),
                ["typeName"] = name,
                ["panelId"]  = panelId
            };
            if (isBranch) fields["groupName"] = name;
            return fields;
        }

        private static void AttachIntegrationFields(Dictionary<string, object> node,
                                                    Dictionary<string, string> fields)
        {
            if (fields == null) return;
            foreach (var kv in fields)
            {
                if (kv.Key == "panelId") continue;
                node[kv.Key] = kv.Value;
            }
        }

        private static string DetermineElementType(MergedElement element)
        {
            if (element.HasChildren && element.IsItem) return "branch|item";
            if (element.HasChildren)                   return "branch";
            return "item";
        }

        private static string ResolveItemName(MergedElement element) =>
            element.ItemItemName ?? element.BranchItemName ?? "";

        private static Dictionary<string, object> CreateBaseNodeFields(string name, string elementType,
                                                                        string itemName, string nodeId,
                                                                        string securosParentId = null)
        {
            var fields = new Dictionary<string, object>
            {
                { "name",        name },
                { "elementType", elementType },
                { "itemName",    itemName },
                { "securosId",   nodeId }
            };
            if (!string.IsNullOrEmpty(securosParentId))
                fields["securosParentId"] = securosParentId;
            return fields;
        }

        private void AttachItemPropertiesIfItem(Dictionary<string, object> node,
                                                MergedElement element, string itemName)
        {
            if (!element.IsItem) return;

            var props          = GetItemPropertiesAsList(itemName);
            node["properties"] = props;
            node["commands"]   = LookupCommands(props);
        }

        private void AttachChildrenIfBranch(Dictionary<string, object> node,
                                            MergedElement element, int depth, string nodeId,
                                            string prefix,
                                            Dictionary<string, string> integrationFields = null)
        {
            if (!element.HasChildren)
            {
                node["hasItemChildren"] = false;
                return;
            }

            var childParentId       = new ItemIdentifier(element.BranchItemName ?? element.ItemItemName);
            var children            = BuildChildNodes(childParentId, prefix, depth + 1, nodeId, integrationFields);
            node["hasItemChildren"] = HasDirectItemChildren(children);
            node["children"]        = children;
        }

        private static bool HasDirectItemChildren(List<object> children) =>
            children.Cast<Dictionary<string, object>>()
                    .Any(c => { var et = (string)c["elementType"]; return et == "item" || et == "branch|item"; });

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

        private static void UpdateExistingMergedElement(MergedElement existing, BrowseElement elem)
        {
            if (elem.HasChildren) { existing.HasChildren = true; existing.BranchItemName = elem.ItemName; }
            if (elem.IsItem)      { existing.IsItem = true;      existing.ItemItemName   = elem.ItemName; }
        }

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

        private static IEnumerable<MergedElement> OrderElementsForTraversal(List<MergedElement> merged) =>
            merged.Where(e => e.IsItem && !e.HasChildren)
                  .Concat(merged.Where(e => e.HasChildren));

        private string ComputeNodeId(string name, int depth, string parentIdStr)
        {
            if (depth == 0) return ComputePanelNodeId(name);
            if (depth == 1) return ComputeTypeNodeId(name, parentIdStr);
            if (depth <= 3) return ComputeInstanceNodeId(name, parentIdStr);
            return "";
        }

        private static string ComputePanelNodeId(string name) =>
            ExtractLastTwoNumbers(name);

        private static string ExtractLastTwoNumbers(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var matches = Regex.Matches(name, @"\d+");
            if (matches.Count == 0) return "";
            if (matches.Count == 1) return matches[0].Value;
            return matches[matches.Count - 2].Value + "-" + matches[matches.Count - 1].Value;
        }

        // El número del grupo sale de opc_group_ids.json, no de su posición en el browse:
        // así un grupo nuevo o eliminado no corre los ids de los demás.
        private string ComputeTypeNodeId(string name, string parentIdStr) =>
            string.IsNullOrEmpty(parentIdStr) ? "" : parentIdStr + "." + _groupIds.GetOrAssign(name);

        private static string ComputeInstanceNodeId(string name, string parentIdStr) =>
            IsNumeric(name) && !string.IsNullOrEmpty(parentIdStr)
                ? parentIdStr + "." + name
                : "";

        public List<object> GetItemPropertiesAsList(string itemName)
        {
            if (string.IsNullOrEmpty(itemName))
                return new List<object>();

            try
            {
                var collection = GetItemProperties(itemName);

                if (collection == null)
                    return new List<object>();

                var props = new List<object>();
                foreach (ItemProperty prop in collection)
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
                Console.WriteLine($"[OPC_Client]\tGetProperties error for '{itemName}': " + ex.Message);
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

            var cmdCache = BuildCmdCache();

            Dictionary<string, string> commands;
            return cmdCache.TryGetValue(typeId, out commands)
                ? commands
                : new Dictionary<string, string>();
        }

        private Dictionary<int, Dictionary<string, string>> BuildCmdCache()
        {
            var cache = new Dictionary<int, Dictionary<string, string>>();
            foreach (var prop in FetchCmdPropertiesFromServer())
                if (prop.Commands != null)
                    cache[prop.Code] = prop.Commands;
            return cache;
        }

        private List<CmdPropertyEntry> FetchCmdPropertiesFromServer()
        {
            try
            {
                var collection = GetItemProperties("CMD");
                if (collection == null) return new List<CmdPropertyEntry>();

                var props = new List<CmdPropertyEntry>();
                foreach (ItemProperty prop in collection)
                {
                    if (!prop.ResultID.Succeeded()) continue;

                    var entry = new CmdPropertyEntry { Code = prop.ID.Code };
                    var raw   = prop.Value?.ToString() ?? string.Empty;

                    if (raw.TrimStart().StartsWith("<"))
                    {
                        entry.Commands = new Dictionary<string, string>();
                        try
                        {
                            var doc = XDocument.Parse(raw);
                            XNamespace ns = "file:///S3K/Proxyverwalter";
                            entry.Commands = doc.Descendants(ns + "Command")
                                .ToDictionary(
                                    e => e.Attribute("Name")?.Value ?? "(unnamed)",
                                    e => e.ToString());
                        }
                        catch { }
                    }
                    props.Add(entry);
                }
                return props;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC_Client]\tError leyendo CMD properties: {ex.Message}");
                return new List<CmdPropertyEntry>();
            }
        }

        private class MergedElement
        {
            public string Name           { get; set; }
            public bool   HasChildren    { get; set; }
            public bool   IsItem         { get; set; }
            public string BranchItemName { get; set; }
            public string ItemItemName   { get; set; }
        }

        private class CmdPropertyEntry
        {
            public int    Code     { get; set; }
            public Dictionary<string, string> Commands { get; set; }
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

        #endregion

        #region Debug

        public IdentifiedResult[] WriteItems(ItemValue[] itemValues)
        {
            ThrowIfNotConnected();

            if (itemValues == null || itemValues.Length == 0)
                throw new ArgumentException(
                    "Debe proporcionar al menos un ítem para escribir.", nameof(itemValues));

            try
            {
                var results = _opcDaServer.Write(itemValues);

                if (results != null)
                {
                    foreach (var r in results)
                    {
                        var status = r.ResultID.Succeeded() ? "OK  " : "FAIL";
                        Console.WriteLine($"[OPC_Client]\tWrite {status}: {r.ItemName} — {r.ResultID}");
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC_Client]\tError al escribir ítems: {ex.Message}");
                return null;
            }
        }

        public void PrintItemProperties(string itemName, string indent)
        {
            try
            {
                var results = _opcDaServer.GetProperties(new[] { new ItemIdentifier(itemName) }, null, true);
                if (results == null || results.Length == 0) return;

                foreach (ItemProperty prop in results[0])
                {
                    if (prop.ResultID.Succeeded())
                    {
                        string desc  = string.IsNullOrEmpty(prop.Description) ? $"Property {prop.ID.Code}" : prop.Description;
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

        public Dictionary<string, string> GetItemProperties(string itemName, int NumPropertie)
        {
            try
            {
                var results = _opcDaServer.GetProperties(new[] { new ItemIdentifier(itemName) }, null, true);
                if (results == null || results.Length == 0) return null;

                var prop = results[0]
                    .Cast<ItemProperty>()
                    .FirstOrDefault(p => p.ID.Code == NumPropertie);

                if (prop?.Value is string raw)
                {
                    if (raw.TrimStart().StartsWith("<"))
                    {
                        var doc = XDocument.Parse(raw);
                        XNamespace ns = "file:///S3K/Proxyverwalter";
                        return doc.Descendants(ns + "Command")
                            .ToDictionary(e => e.Attribute("Name").Value, e => e.ToString());
                    }

                    return prop != null
                        ? new Dictionary<string, string>
                          {
                              { prop.Description ?? $"Property {prop.ID.Code}", prop.Value?.ToString() ?? "(null)" }
                          }
                        : null;
                }

                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   (Error al leer propiedades: {ex.Message})");
                return null;
            }
        }

        #endregion
    }
}
