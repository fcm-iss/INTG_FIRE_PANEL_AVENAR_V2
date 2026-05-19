using Opc;
using Opc.Da;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
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
                Name = name,
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
