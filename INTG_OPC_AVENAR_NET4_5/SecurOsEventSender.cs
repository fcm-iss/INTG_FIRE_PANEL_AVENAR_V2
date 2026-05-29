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
    class SecurOsEventSender
    {
        #region Campos y Constructor

        private readonly IidkManager _Securos;

        private SecurOsEventSender(IidkManager iidkManager)
        {
            _Securos = iidkManager;
        }

        public static SecurOsEventSender Connect()
        {
            var host = ConfigurationManager.AppSettings["securos_host"] ?? "127.0.0.1";
            var port = ConfigurationManager.AppSettings["securos_port"] ?? "3000";
            var id   = ConfigurationManager.AppSettings["idUinP"]       ?? "2";

            var iidkManager = new IidkManager();
            WaitForConnection(iidkManager, host, port, id);
            return new SecurOsEventSender(iidkManager);
        }

        private static void WaitForConnection(IidkManager manager, string host, string port, string id)
        {
            const int TimeoutMs = 5000;
            using (var connected = new ManualResetEventSlim(false))
            {
                manager.OnConnectionStateChanged += state => { if (state) connected.Set(); };
                manager.SyncConnect(host, port, id);

                if (connected.Wait(TimeoutMs))
                    Console.WriteLine($"[SecurOS] Conectado → {host}:{port} id={id}");
                else
                    Console.WriteLine($"[SecurOS] Timeout esperando conexión a {host}:{port}");
            }
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
                Console.WriteLine("[SecurOS] Sin conexión — mensaje descartado.");
                return;
            }

            var msg = $"{objType}|{securosId}|{state}";
            _Securos.SendMessage(msg);
            Console.WriteLine($"[SecurOS] ← {msg}");
        }

        #endregion

        #region Resolución de Estado

        private static readonly Dictionary<int, string> _lzNameByCode = LoadLzNames();

        private static Dictionary<int, string> LoadLzNames()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "opc_states.json");
            if (!File.Exists(path))
            {
                Console.WriteLine("[SecurOS] opc_states.json no encontrado en Config\\");
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
