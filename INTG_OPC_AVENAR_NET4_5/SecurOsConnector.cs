using INTG_OPC_AVENAR_NET4_5.Models;
using SecurosIIDK;
using Opc.Da;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace INTG_OPC_AVENAR_NET4_5
{
    class SecurOsConnector : IDisposable
    {
        #region Campos y Constructor

        private readonly IidkManager _Securos;
        private bool _disposed;

        public event Action<SecurOsMessage> MessageReceived;

        private SecurOsConnector(IidkManager securosIidkManager)
        {
            _Securos = securosIidkManager;
            _Securos.OnSecurOSMessage += OnMessageReceived;
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

        public static SecurOsConnector Connect()
        {
            var host = ConfigurationManager.AppSettings["securos_host"] ?? "127.0.0.1";
            var port = ConfigurationManager.AppSettings["securos_port"] ?? "3000";
            var id   = ConfigurationManager.AppSettings["idUinP"]       ?? "2";

            var iidkManager = new IidkManager();
            WaitForConnection(iidkManager, host, port, id);
            return new SecurOsConnector(iidkManager);
        }

        private static void WaitForConnection(IidkManager manager, string host, string port, string id)
        {
            const int TimeoutMs = 5000;
            var connected = new ManualResetEventSlim(false);

            manager.OnConnectionStateChanged += state =>
            {
                if (state) try { connected.Set(); } catch (ObjectDisposedException) { }
            };

            manager.SyncConnect(host, port, id);
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

                var msg = BuildSecurOsMessage(message);
                Console.WriteLine($"[SecurOS]\tRecibido <- Type:{msg.ObjType} Id:{msg.Id} Action:{msg.EventName} Params:{msg.Params}");
                MessageReceived?.Invoke(msg);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SecurOS]\tError al procesar mensaje: {ex.Message}");
            }
        }

        private static SecurOsMessage BuildSecurOsMessage(SecurosIIDK.Message iidk)
        {
            return new SecurOsMessage
            {
                ObjType   = iidk.Type   ?? "",
                Id        = iidk.Id     ?? "",
                EventName = iidk.Action ?? "",
                Params    = BuildParamsString(iidk.Params)
            };
        }

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
