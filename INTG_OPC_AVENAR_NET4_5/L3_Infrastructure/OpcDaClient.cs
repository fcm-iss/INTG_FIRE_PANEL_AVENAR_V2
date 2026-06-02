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
        #region Campos y Constructor

        private Opc.Da.Server _opcDaServer;
        private Subscription _subscription;
        private string _serverUrl;
        private bool _disposed;

        private static readonly BrowseFilters FilterAll    = new BrowseFilters { BrowseFilter = browseFilter.all };
        private static readonly BrowseFilters FilterBranch = new BrowseFilters { BrowseFilter = browseFilter.branch };
        private static readonly BrowseFilters FilterItem   = new BrowseFilters { BrowseFilter = browseFilter.item };

        public bool IsConnected => _opcDaServer != null && _opcDaServer.IsConnected;
        public Opc.Da.Server Server => _opcDaServer;

        public event Action<ItemValueResult[]> PanelChanged;

        public OpcClient() { }

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
            ValidateServerUrl(opcServerUrl);
            Console.WriteLine($"[OPC_Client]\tConectando a: {opcServerUrl}");

            try
            {
                CreateAndConnectServer(opcServerUrl);

                if (!_opcDaServer.IsConnected)
                {
                    Console.WriteLine("[OPC_Client]\tError: el servidor no reporta conexión activa.");
                    return false;
                }

                _serverUrl = opcServerUrl;
                Console.WriteLine("[OPC_Client]\tConexión establecida correctamente.");
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
            var url     = new Opc.URL(opcServerUrl);
            var factory = new OpcCom.Factory();
            _opcDaServer = new Opc.Da.Server(factory, null);
            _opcDaServer.Connect(url, null);
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
