using System;
using System.Collections.Generic;
using System.Linq;
using Opc;
using Opc.Da;

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
    }
}



//using Opc;
//using Opc.Da;
//using System;
//using System.Collections.Generic;
////using System.Collections.Generic;
////using System.Configuration;
////using System.Threading;

//namespace INTG_OPC
//{
//    public class OpcDaConnector : IDisposable
//    {
//        private bool _disposed;

//        public Opc.Da.Server OpcDaServer = null;
//        static Opc.IRequest request;
//        //public OpcCom.Factory fact = new OpcCom.Factory();
//        //public SubscriptionState groupState = new Opc.Da.SubscriptionState();
//        static Subscription group;

//        static ItemIdentifier itemId = null;
//        static BrowseFilters filter_all = new BrowseFilters() { BrowseFilter = browseFilter.all };
//        static BrowseFilters filter_branch = new BrowseFilters() { BrowseFilter = browseFilter.branch };
//        static BrowsePosition position;

//        static Item[] items;
//        static List<Item> item_list = new List<Item>();

//        //public  Dictionary<string, object> previousValues = new Dictionary<string, object>();
//        //public  Thread readThread;
//        //public  bool continueReading = false;

//        public OpcDaConnector(){}


//        /// <summary>
//        /// Establece conexión con el servidor OPC DA e inicializa el grupo de suscripción.
//        /// </summary>
//        /// <param name="opcda_url">URL del servidor OPC. Ej: opcda://localhost/BOSCH.OpcDaServer.1</param>
//        /// <returns>True si la conexión fue exitosa, False en caso contrario.</returns>
//        public bool Connect(string opcServerUrl)
//        {
//            if (string.IsNullOrWhiteSpace(opcServerUrl))
//                throw new ArgumentException("La URL del servidor OPC no puede estar vacía.", nameof(opcServerUrl));

//            Console.WriteLine($"[OPC] Conectando a: {opcServerUrl}");

//            try
//            {
//                var url = new Opc.URL(opcServerUrl);
//                var factory = new OpcCom.Factory();
//                OpcDaServer = new Opc.Da.Server(factory, null);

//                OpcDaServer.Connect(url, null);

//                if (!OpcDaServer.IsConnected)
//                {
//                    Console.WriteLine("[OPC] Error: el servidor no reporta conexión activa.");
//                    return false;
//                }

//                Console.WriteLine("[OPC] Conexión establecida correctamente.");                
//                return true;
//            }
//            catch (Opc.ConnectFailedException ex)
//            {
//                Console.WriteLine($"[OPC] Falló la conexión: {ex.Message}");
//                return false;
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"[OPC] Error inesperado al conectar: {ex.Message}");
//                return false;
//            }
//        }

//        /// <summary>
//        /// Inicializa el grupo de suscripción y registra los ítems descubiertos.
//        /// </summary>
//        public void InitializeSubscriptionGroup()
//        {
//            Console.WriteLine("[OPC] Inicializando grupo de suscripción...");

//            var groupState = new Opc.Da.SubscriptionState();
//            try
//            {
//                // 1. Configurar el estado del grupo ANTES de crearlo
//                groupState.Name = "FPA5000_Group";
//                groupState.Active = true;
//                groupState.UpdateRate = 5000;   // milisegundos — el servidor notifica cada 5s
//                groupState.Deadband = 0;      // 0 = notifica cualquier cambio, sin filtro de variación

//                // 2. Crear el grupo en el servidor con esa configuración
//                group = (Subscription)OpcDaServer.CreateSubscription(groupState);

//                // 3. Registrar los ítems dentro del grupo
//                Browse(itemId, filter_branch, out position);
//                var items = item_list.ToArray();
//                group.AddItems(items);

//                Console.WriteLine($"[OPC] Grupo '{groupState.Name}' inicializado con {items.Length} ítem(s).");
//            }
//            catch (Opc.ResultIDException ex)
//            {
//                Console.WriteLine("Error creating subscription: " + ex.Message);
//                Console.WriteLine("ResultID: " + ex.Result);
//            }              
//        }

//        public void SearchItems()
//        {
//            if (OpcDaServer != null && OpcDaServer.IsConnected && group != null)
//            {
//                group.Read(group.Items, 100, new Opc.Da.ReadCompleteEventHandler(ReadCompleteCallback), out request);
//                Console.ReadLine();
//            }
//            else
//            {
//                Console.WriteLine("Server is not connected or group is not initialized.");
//            }
//        }

//        public void Disconnect()
//        {
//            if (OpcDaServer is null || !OpcDaServer.IsConnected) return;

//            try
//            {
//                OpcDaServer.Disconnect();
//                Console.WriteLine("[OPC] Desconectado correctamente.");
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"[OPC] Error al desconectar: {ex.Message}");
//            }
//        }

//        public void Dispose()
//        {
//            if (_disposed) return;
//            Disconnect();
//            OpcDaServer = null;
//            _disposed = true;
//        }



//        private void ReadCompleteCallback(object clientHandle, Opc.Da.ItemValueResult[] results)
//        {
//            Console.WriteLine("Read completed");
//            foreach (ItemValueResult readResult in results)
//            {
//                Console.WriteLine("\t{0}\tval:{1}", readResult.ItemName, readResult.Value);
//            }
//            Console.WriteLine();
//        }

//        private void Browse(ItemIdentifier _itemId, BrowseFilters _filter, out BrowsePosition _position)
//        {
//            BrowseElement[] branches = OpcDaServer.Browse(_itemId, _filter, out _position);
//            if (branches != null)
//            {
//                foreach (BrowseElement branch in branches)
//                {
//                    if (branch.HasChildren)
//                    {
//                        var id = new ItemIdentifier(branch.ItemName);
//                        BrowsePosition position_branch;
//                        Browse(id, filter_branch, out position_branch);
//                    }
//                    else
//                    {
//                        item_list.Add(new Item()
//                        {
//                            ItemName = branch.ItemName
//                        });
//                    }
//                }
//            }

//            BrowseElement[] childrens = OpcDaServer.Browse(_itemId, filter_all, out _position);
//            if (childrens != null)
//            {
//                foreach (BrowseElement child in childrens)
//                {
//                    if (child.IsItem)
//                    {
//                        item_list.Add(new Item()
//                        {
//                            ItemName = child.ItemName
//                        });
//                    }
//                }
//            }
//        }
//    }
//}
