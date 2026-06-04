using INTG_FIRE_PANEL_AVENAR.Domain;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
using ISS.OPCMonitor;
using Opc.Da;
using SecurosIIDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using static SecurosIIDK.User32Wrapper;

namespace INTG_FIRE_PANEL_AVENAR
{
    class SecurOsAdapter : ISecurOsGateway
    {
        #region Campos y Constructor

        private readonly IidkManager _Securos;
        private bool _disposed;

        public event Action<SecurOsCommand> CommandReceived;

        // SecurOS envía GET_CONFIG para todos los objetos existentes al conectar
        // y finaliza el burst con SLAVE|hostname.idUinP|INITIALIZATION_COMPLETE.
        private readonly ConcurrentBag<int>      _existingIntegrationIds  = new ConcurrentBag<int>();
        private readonly ManualResetEventSlim     _initializationComplete  = new ManualResetEventSlim(false);

        private SecurOsAdapter(IidkManager securosIidkManager)
        {
            _Securos = securosIidkManager;
            _Securos.OnSecurOSMessage += OnMessageReceived;
            _Securos.OnSecurOSMessage += ProcessInitialConfigBurst;
        }

        private void ProcessInitialConfigBurst(SecurosIIDK.Message msg)
        {
            if (string.Equals(msg.Type,   "INTEGRATION", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(msg.Action, "GET_CONFIG",  StringComparison.OrdinalIgnoreCase))
            {
                int id;
                if (int.TryParse(msg.Id, out id) && id > 0)
                    _existingIntegrationIds.Add(id);
            }
            else if (string.Equals(msg.Type,   "SLAVE",                  StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(msg.Action, "INITIALIZATION_COMPLETE", StringComparison.OrdinalIgnoreCase))
            {
                try { _initializationComplete.Set(); } catch (ObjectDisposedException) { }
            }
        }

        private void WaitForInitializationComplete()
        {
            // Conexiones cortas (creación de objetos) pueden no recibir el burst completo.
            // El timeout es esperado en ese caso — el flujo continúa con el fallback a idUinP.
            const int TimeoutMs = 2000;
            bool received = _initializationComplete.Wait(TimeoutMs);
            var ids = new List<int>(_existingIntegrationIds);

            if (received)
                Console.WriteLine($"[SecurOS]\tConfiguración inicial completa — IDs INTEGRATION detectados: [{string.Join(", ", ids)}]");
            else if (ids.Count > 0)
                Console.WriteLine($"[SecurOS]\tBurst parcial recibido — IDs INTEGRATION detectados: [{string.Join(", ", ids)}]");
            else
                Console.WriteLine("[SecurOS]\tSin burst inicial — usando idUinP del config como parent_id.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            DisconnectSecurOs();
            (_Securos as IDisposable)?.Dispose();
            _initializationComplete.Dispose();
            _disposed = true;
        }

        private void DisconnectSecurOs()
        {
            try
            {
                if (_Securos.IsConnected())
                    _Securos.Disconnect();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SecurOS]\tError al desconectar: {ex.Message}");
            }
        }

        public static SecurOsAdapter Connect()
        {
            Console.WriteLine("[SecurOS]\t\tinicio de conexion...");
            var host = ConfigurationManager.AppSettings["securos_host"] ?? "127.0.0.1";
            var port = ConfigurationManager.AppSettings["securos_port"] ?? "3000";
            var id   = ConfigurationManager.AppSettings["idUinP"]       ?? "1";
            Console.WriteLine($"[SecurOS]\t\tidUinP{id}");
            var iidkManager = new IidkManager();

            // El listener se registra ANTES de conectar para no perder
            // el burst de GET_CONFIG que SecurOS envía al establecer conexión.
            Console.WriteLine("[SecurOS]\t\tCreando conector...");
            var connector = new SecurOsAdapter(iidkManager);
            Console.WriteLine("[SecurOS]\t\tesperando por conexion...");
            WaitForConnection(iidkManager, host, port, id);
            Console.WriteLine("[SecurOS]\t\tEspereando inicializacion...");
            connector.WaitForInitializationComplete();
            Console.WriteLine("[SecurOS]\t\tfin...");
            return connector;
        }

        private static void WaitForConnection(IidkManager manager, string host, string port, string id)
        {
            const int TimeoutMs = 5000;
            var connected = new ManualResetEventSlim(false);

            manager.OnConnectionStateChanged += state =>
            {
                if (state) try { connected.Set(); } catch (ObjectDisposedException) { }
            };

            // SyncConnect puede bloquearse indefinidamente si SecurOS no responde;
            // se ejecuta en un hilo separado para poder aplicar el timeout externo.
            var connectThread = new Thread(() =>
            {
                try { manager.SyncConnect(host, port, id); }
                catch (Exception ex) { Console.WriteLine($"[SecurOS]\tError en SyncConnect: {ex.Message}"); }
            }) { IsBackground = true };

            connectThread.Start();
            bool ok = connected.Wait(TimeoutMs);
            connected.Dispose();

            Console.WriteLine(ok
                ? $"[SecurOS]\tConectado -> {host}:{port} id={id}"
                : $"[SecurOS]\tTimeout esperando conexión a {host}:{port}");
        }

        #endregion

        #region Recepción de Mensajes

        private void OnMessageReceived(SecurosIIDK.Message message)
        {
            try
            {
                if (!message.IsValid) return;

                var cmd = BuildCommand(message);
                Console.WriteLine($"[SecurOS]\tRecibido <- Type:{cmd.ObjType} Id:{cmd.Id} Action:{cmd.EventName} Params:{cmd.Params}");
                CommandReceived?.Invoke(cmd);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SecurOS]\tError al procesar mensaje: {ex.Message}");
            }
        }

        private static SecurOsCommand BuildCommand(SecurosIIDK.Message iidk)
            => new SecurOsCommand(iidk.Type ?? "", iidk.Id ?? "", iidk.Action ?? "", BuildParamsString(iidk.Params));

        private static string BuildParamsString(SecurosIIDK.IMessageParameters parameters)
        {
            if (parameters == null) return "";

            var sb = new StringBuilder();
            foreach (string key in parameters.Keys)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append($"{key}={parameters.GetAs<string>(key)}");
            }
            return sb.ToString();
        }

        #endregion

        #region Envío de Eventos

        public void Send(ItemValueResult item)
        {
            var handle = item.ClientHandle?.ToString();
            var state = ResolveState(item.Value);

            string objType, securosId;
            if (!ParseClientHandle(handle, out objType, out securosId)) return;

            SendMessage(objType, securosId, state);
        }

        public void SendItemState(string nodeId, object rawOpcValue)
        {
            string objType, securosId;
            if (!ParseClientHandle(nodeId, out objType, out securosId)) return;

            SendMessage(objType, securosId, ResolveState(rawOpcValue));
        }

        private static bool ParseClientHandle(string handle, out string objType, out string securosId)
        {
            objType = null;
            securosId   = null;

            if (string.IsNullOrEmpty(handle)) return false;

            var parts = handle.Split('|');
            if (parts.Length != 2) return false;

            objType = parts[0];
            securosId   = parts[1];
            return !string.IsNullOrEmpty(objType) && !string.IsNullOrEmpty(securosId);
        }

        private void SendMessage(string objType, string securosId, string state)
        {
            if (!_Securos.IsConnected())
            {
                Console.WriteLine("[SecurOS]\tSin conexión — mensaje descartado.");
                return;
            }

            var msg = $"{objType}|{securosId}|{state}";
            _Securos.SendMessage(msg);
            Console.WriteLine($"[SecurOS]\tEnviado ->: {msg}");
        }

        #endregion

        #region Creación de objetos en SecurOS

        public void CreateObjectsInSecurOs(string treeJsonPath, string ddiPrefix)
        {
            // 1. Extraer paneles e ítems desde OPCTree.json
            var objectSpecs = OpcIntegrationExporter.ExtractObjectSpecsFromTree(treeJsonPath, ddiPrefix);
            Console.WriteLine($"[SecurOS]\tobjeto json capturado");
            // 2. Leer IDs de INTEGRATION capturados en el burst inicial de GET_CONFIG
            //    Si no llegó ninguno, usar idUinP del config como fallback
            var existingIds         = new List<int>(_existingIntegrationIds);
            var integrationParentId = existingIds.Count > 0
                ? FindNextAvailableCardinalId(existingIds).ToString()
                : ConfigurationManager.AppSettings["idUinP"] ?? "1";

            Console.WriteLine($"[SecurOS]\tIDs INTEGRATION existentes: [{string.Join(", ", existingIds)}]");
            Console.WriteLine($"[SecurOS]\tUsando parent_id para paneles: {integrationParentId}");

            // 3. Crear los paneles con parent_id = id del punto de integración
            Console.WriteLine($"[SecurOS]\tcreando paneles...");
            CreatePanelObjects(objectSpecs, integrationParentId);
            Console.WriteLine($"[SecurOS]\tcreando items...");
            // 4. Crear los ítems con parent_id = securosId de su panel
            CreateItemObjects(objectSpecs);

            Console.WriteLine("[SecurOS]\tCreación de objetos completada.");
        }

        private static int FindNextAvailableCardinalId(IEnumerable<int> existingIds)
        {
            var taken = new HashSet<int>(existingIds);
            var next  = 1;
            while (taken.Contains(next))
                next++;
            return next;
        }

        private void CreatePanelObjects(List<OpcObjectSpec> specs, string integrationParentId)
        {
            foreach (var spec in specs.FindAll(s => s.IsPanel))
                SendCreateObject(spec.ObjType, spec.ObjId, spec.Name, integrationParentId);
        }

        private void CreateItemObjects(List<OpcObjectSpec> specs)
        {
            foreach (var spec in specs.FindAll(s => !s.IsPanel))
                SendCreateObject(spec.ObjType, spec.ObjId, spec.Name, spec.ParentId);
        }

        private void SendCreateObject(string objType, string objId, string name, string parentId)
        {
            if (!_Securos.IsConnected())
            {
                Console.WriteLine("[SecurOS]\tSin conexión — CREATE_OBJECT descartado.");
                return;
            }

            var msg = $"CORE|1|CREATE_OBJECT|objtype<{objType}>,objid<{objId}>,name<{name}>,parent_id<{parentId}>";
            _Securos.SendMessage(msg);
            Console.WriteLine($"[SecurOS]\tCREATE_OBJECT -> {objType} id={objId}");
        }

        #endregion

        #region Resolución de Estado

        private static readonly Dictionary<int, string> _lzNameByCode = LoadLzNames();

        private static Dictionary<int, string> LoadLzNames()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "opc_states.json");
            if (!File.Exists(path))
            {
                Console.WriteLine("[SecurOS]\topc_states.json no encontrado en Config\\");
                return new Dictionary<int, string>();
            }

            var json    = File.ReadAllText(path, new UTF8Encoding(false));
            var entries = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(json);
            return BuildLzNameMap(entries);
        }

        private static Dictionary<int, string> BuildLzNameMap(List<Dictionary<string, object>> entries)
        {
            var map = new Dictionary<int, string>();
            foreach (var entry in entries)
            {
                object opcValue, lzName;
                if (!entry.TryGetValue("opcValue", out opcValue) || !entry.TryGetValue("lzName", out lzName))
                    continue;

                int code;
                if (int.TryParse(opcValue?.ToString(), out code))
                    map[code] = lzName?.ToString() ?? "";
            }
            return map;
        }

        private static string ResolveState(object value)
        {
            string lzName;
            if (!int.TryParse(value?.ToString(), out int code)) return "";
            return _lzNameByCode.TryGetValue(code, out lzName) ? lzName : "";
        }

        #endregion
    }

}
