using INTG_FIRE_PANEL_AVENAR.Application;
using INTG_FIRE_PANEL_AVENAR.Domain;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
using Newtonsoft.Json;
using Opc.Da;
using SecurosIIDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Runtime.InteropServices;
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
        private volatile bool _isReconnecting;
        private volatile bool _isInitialized;
        private string _host;
        private string _port;
        private string _idUinP;

        public event Action<SecurOsCommand> CommandReceived;
        public event Action ResetRequested;

        private SecurOsAdapter(IidkManager securosIidkManager, string host, string port, string idUinP)
        {
            _Securos = securosIidkManager;
            _host = host;
            _port = port;
            _idUinP = idUinP;
            _Securos.OnSecurOSMessage += OnMessageReceived;
            // La reconexión automática solo aplica tras la conexión inicial; antes de eso
            // Connect() ya reintenta, y dos SyncConnect simultáneos hacen caer iidk.dll.
            _Securos.OnConnectionStateChanged += state =>
            {
                if (!state && _isInitialized) StartReconnectionInBackground("Conexión TCP perdida");
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            DisconnectSecurOs();
            (_Securos as IDisposable)?.Dispose();
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

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetDllDirectory(string lpPathName);

        // Misma firma que SecurosIIDK.IidkWrapper.SendMsg (internal en el SDK); IidkManager.SendMessage descarta el rc.
        [DllImport("iidk.dll", EntryPoint = "SendMsg", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        private static extern int IidkSendMsg(string id, string msg);

        private static void ConfigureIidkDllSearchPath()
        {
            var iidkDllDirectory = ConfigurationManager.AppSettings["iidk_dll_directory"];
            if (string.IsNullOrWhiteSpace(iidkDllDirectory))
                return;

            if (!Directory.Exists(iidkDllDirectory))
            {
                Console.WriteLine($"[SecurOS]\t\tiidk_dll_directory configurado no existe: {iidkDllDirectory}");
                return;
            }

            if (SetDllDirectory(iidkDllDirectory))
                Console.WriteLine($"[SecurOS]\t\tDirectorio de búsqueda de iidk.dll: {iidkDllDirectory}");
            else
                Console.WriteLine($"[SecurOS]\t\tNo se pudo establecer el directorio de búsqueda de iidk.dll: {iidkDllDirectory}");
        }

        public static SecurOsAdapter Connect()
        {
            Console.WriteLine("[SecurOS]\t\tinicio de conexion...");
            ConfigureIidkDllSearchPath();
            var host = ConfigurationManager.AppSettings["securos_host"] ?? "127.0.0.1";
            var port = ConfigurationManager.AppSettings["securos_port"] ?? "3000";
            var id = ConfigurationManager.AppSettings["idUinP"] ?? "1";
            Console.WriteLine($"[SecurOS]\t\tidUinP{id}");
            var iidkManager = new IidkManager();

            Console.WriteLine("[SecurOS]\t\tCreando conector...");
            var connector = new SecurOsAdapter(iidkManager, host, port, id);
            Console.WriteLine("[SecurOS]\t\tEsperando conexión...");
            WaitForConnection(iidkManager, host, port, id);
            Console.WriteLine("[SecurOS]\t\tVerificando punto de integración...");
            connector.WaitForInitializationComplete();
            connector._isInitialized = true;
            return connector;
        }

        private static void WaitForConnection(IidkManager manager, string host, string port, string id)
        {
            const int TimeoutMs = 5000;
            const int RetryDelayMs = 2000;

            int attempt = 0;
            while (true)
            {
                attempt++;
                Console.WriteLine($"[SecurOS]\tIntento {attempt} -> {host}:{port}");

                if (TryConnectOnce(manager, host, port, id, TimeoutMs))
                    return;

                Console.WriteLine($"[SecurOS]\tSin conexión en intento {attempt} — reintentando en {RetryDelayMs / 1000}s...");
                Thread.Sleep(RetryDelayMs);
            }
        }

        private static bool TryConnectOnce(IidkManager manager, string host, string port, string id, int timeoutMs)
        {
            var connected = new ManualResetEventSlim(false);

            // SyncConnect puede bloquearse indefinidamente si SecurOS no responde;
            // se ejecuta en un hilo separado para poder aplicar el timeout externo.
            ConnectionStateListener onStateChanged = state =>
            {
                if (state) try { connected.Set(); } catch (ObjectDisposedException) { }
            };

            manager.OnConnectionStateChanged += onStateChanged;

            var connectThread = new Thread(() =>
            {
                try { manager.SyncConnect(host, port, id); }
                catch (Exception ex) { Console.WriteLine($"[SecurOS]\tError en SyncConnect: {ex.Message}"); }
            })
            { IsBackground = true };

            connectThread.Start();
            bool signaled = connected.Wait(timeoutMs);
            connected.Dispose();
            manager.OnConnectionStateChanged -= onStateChanged;

            if (signaled && manager.IsConnected())
            {
                Console.WriteLine($"[SecurOS]\tConectado -> {host}:{port} id={id}");
                return true;
            }

            Console.WriteLine($"[SecurOS]\tTimeout esperando conexión a {host}:{port}");
            return false;
        }

        private void WaitForInitializationComplete()
        {
            const int TcpTimeoutMs = 5000;
            const int InitTimeoutMs = 3000;
            const int RetryDelayMs = 2000;
            int attempt = 0;

            while (!_disposed)
            {
                if (!_Securos.IsConnected())
                {
                    attempt++;
                    Console.WriteLine($"[SecurOS]\tIntento {attempt} -> {_host}:{_port}");
                    if (!TryConnectOnce(_Securos, _host, _port, _idUinP, TcpTimeoutMs))
                    {
                        Console.WriteLine($"[SecurOS]\tSin conexión — reintentando en {RetryDelayMs / 1000}s...");
                        Thread.Sleep(RetryDelayMs);
                        continue;
                    }
                }

                var initReceived = new ManualResetEventSlim(false);

                SecurosIIDK.MessagesListener onMessage = msg =>
                {
                    if (string.Equals(msg.Type, "SLAVE", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(msg.Action, "INITIALIZATION_COMPLETE", StringComparison.OrdinalIgnoreCase))
                        try { initReceived.Set(); } catch (ObjectDisposedException) { }
                };

                _Securos.OnSecurOSMessage += onMessage;
                bool received = initReceived.Wait(InitTimeoutMs);
                initReceived.Dispose();
                _Securos.OnSecurOSMessage -= onMessage;

                if (received)
                {
                    Console.WriteLine("[SecurOS]\tSecurOS inicializado — listo para monitoreo.");
                    return;
                }

                Console.WriteLine("[SecurOS]\tEsperando inicialización de punto de integracion de SecurOS...");
                Thread.Sleep(RetryDelayMs);
            }
        }

        private void StartReconnectionInBackground(string reason)
        {
            if (_isReconnecting || _disposed) return;
            _isReconnecting = true;

            Console.WriteLine($"[SecurOS]\t{reason} — iniciando reconexión...");
            new Thread(() =>
            {
                try { WaitForInitializationComplete(); }
                finally { _isReconnecting = false; }
            })
            { IsBackground = true }.Start();
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

                if (IsIntegrationDeleted(message))
                {
                    StartReconnectionInBackground("Sin Punto de integración.");
                    return;
                }

                if (IsIntegrationReset(message))
                {
                    ResetRequested?.Invoke();
                    return;
                }

                if (IsSystemMessage(message)) return;

                CommandReceived?.Invoke(cmd);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SecurOS]\tError al procesar mensaje: {ex.Message}");
            }
        }

        private bool IsIntegrationDeleted(SecurosIIDK.Message message)
            => string.Equals(message.Type, "INTEGRATION", StringComparison.OrdinalIgnoreCase)
            && string.Equals(message.Action, "DELETE", StringComparison.OrdinalIgnoreCase)
            && string.Equals(message.Id, _idUinP, StringComparison.OrdinalIgnoreCase);

        private bool IsIntegrationReset(SecurosIIDK.Message message)
            => string.Equals(message.Type, "INTEGRATION", StringComparison.OrdinalIgnoreCase)
            && string.Equals(message.Action, "RESET", StringComparison.OrdinalIgnoreCase)
            && string.Equals(message.Id, _idUinP, StringComparison.OrdinalIgnoreCase);

        private static bool IsSystemMessage(SecurosIIDK.Message message)
            => string.Equals(message.Type, "INTEGRATION", StringComparison.OrdinalIgnoreCase)
            || string.Equals(message.Type, "SLAVE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(message.Action, "SETUP", StringComparison.OrdinalIgnoreCase);

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

        #region Respuesta al RESET

        private const int ResetTreeVersion = 1;

        public void SendResetResponse(List<SecurOsObjectDefinition> objects)
        {
            if (!_Securos.IsConnected())
            {
                Console.WriteLine("[SecurOS]\tSin conexión — respuesta al RESET descartada.");
                return;
            }

            // El Id es el del RESET recibido, que solo se atiende si coincide con idUinP.
            var msg = $"{ResolveResetResponseType()}|{_idUinP}|RESET|{BuildResetTreeJson(objects)}";
            var returnCode = IidkSendMsg(_idUinP, msg);
            Console.WriteLine($"[SecurOS]\tRespuesta RESET -> rc={returnCode}  ({msg})");
        }

        // Tipo de objeto raíz de la integración; configurable en App.config con "reset_response_type".
        private static string ResolveResetResponseType()
        {
            return (ConfigurationManager.AppSettings["ddi_prefix"] ?? "") + "FIRE_PANEL";
        }

        private static string BuildResetTreeJson(List<SecurOsObjectDefinition> objects)
        {
            var tree = new Dictionary<string, object>
            {
                { "version", ResetTreeVersion },
                { "objects", objects.ConvertAll(BuildResetTreeObject) }
            };
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(tree);
        }

        private static Dictionary<string, object> BuildResetTreeObject(SecurOsObjectDefinition def)
            => new Dictionary<string, object>
            {
                { "type", def.ObjType },
                { "id",   def.ObjId },
                { "name", def.Name ?? "" }
            };

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
            securosId = null;

            if (string.IsNullOrEmpty(handle)) return false;

            var parts = handle.Split('|');
            if (parts.Length != 2) return false;

            objType = parts[0];
            securosId = parts[1];
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
            var definitions = OpcTreeReader.ExtractObjectDefinitionsFromTree(treeJsonPath, ddiPrefix);
            var integrationParentId = ConfigurationManager.AppSettings["idUinP"] ?? "1";

            Console.WriteLine($"[SecurOS]\tUsando parent_id para paneles: {integrationParentId}");

            CreatePanelObjects(definitions, integrationParentId);
            CreateItemObjects(definitions);

            Console.WriteLine("[SecurOS]\tCreación de objetos completada.");
        }


        private void CreatePanelObjects(List<SecurOsObjectDefinition> definitions, string integrationParentId)
        {
            foreach (var spec in definitions.FindAll(s => s.IsPanel))
                CreateObject(spec.ObjType, spec.ObjId, spec.Name, integrationParentId);
        }

        private void CreateItemObjects(List<SecurOsObjectDefinition> definitions)
        {
            foreach (var spec in definitions.FindAll(s => !s.IsPanel))
                CreateObject(spec.ObjType, spec.ObjId, spec.Name, spec.ParentId);
        }

        #endregion

        #region Comandos CORE sobre objetos

        // Id de cabecera de los comandos CORE ("CORE|<id>|ACCION|..."). Configurable para probar "1" frente a vacío.
        public string CoreTargetId { get; set; } = "1";

        // Simulación: los comandos CORE se muestran en consola pero no se envían a SecurOS.
        public bool DryRun { get; set; }

        public int CreateObject(string objType, string objId, string name, string parentId)
            => SendCoreObjectCommand("CREATE_OBJECT", objType, objId,
                                     $"objtype<{objType}>,objid<{objId}>,name<{name}>,parent_id<{parentId}>");

        // name y parent_id son opcionales: solo se envían los que traen valor.
        public int UpdateObject(string objType, string objId, string name, string parentId)
            => SendCoreObjectCommand("UPDATE_OBJECT", objType, objId,
                                     $"objtype<{objType}>,objid<{objId}>" + OptionalParam("name", name) + OptionalParam("parent_id", parentId));

        public int DeleteObject(string objType, string objId)
            => SendCoreObjectCommand("DELETE_OBJECT", objType, objId, $"objtype<{objType}>,objid<{objId}>");

        private static string OptionalParam(string key, string value)
            => string.IsNullOrEmpty(value) ? "" : $",{key}<{value}>";

        // rc que devuelven los comandos cuando no se envían por falta de conexión.
        public const int NotSentReturnCode = -1;

        private int SendCoreObjectCommand(string action, string objType, string objId, string parameters)
        {
            if (DryRun)
            {
                Console.WriteLine($"[SecurOS]\t(simulado) CORE|{CoreTargetId}|{action}|{parameters}");
                return NotSentReturnCode;
            }

            if (!_Securos.IsConnected())
            {
                Console.WriteLine($"[SecurOS]\tSin conexión — {action} descartado.");
                return NotSentReturnCode;
            }

            var msg = $"CORE|{CoreTargetId}|{action}|{parameters}";
            var returnCode = IidkSendMsg(_idUinP, msg);
            Console.WriteLine($"[SecurOS]\t{action} -> {objType} id={objId} rc={returnCode}  ({msg})");
            return returnCode;
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

            var json = File.ReadAllText(path, new UTF8Encoding(false));
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
