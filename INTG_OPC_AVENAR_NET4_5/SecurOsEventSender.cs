using SecurosIIDK;
using Opc.Da;
using System;
using System.Configuration;
using System.Threading;

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
            var tipo   = ResolveObjectType(item.ItemName);
            var id     = item.ClientHandle?.ToString();
            var estado = ResolveState(item.Value);

            if (string.IsNullOrEmpty(tipo) || string.IsNullOrEmpty(id)) return;

            SendMessage(tipo, id, estado);
        }

        private void SendMessage(string tipo, string id, string estado)
        {
            if (!_Securos.IsConnected())
            {
                Console.WriteLine("[SecurOS] Sin conexión — mensaje descartado.");
                return;
            }

            var msg = $"{tipo}|{id}|{estado}";
            _Securos.SendMessage(msg);
            Console.WriteLine($"[SecurOS] ← {msg}");
        }

        #endregion

        #region Resolución de Tipo y Estado

        private static string ResolveObjectType(string itemName)
        {
            if (itemName.Contains("Detector")) return "AVEJ1_DETECTOR";//ResolveDetectorSubtype(itemName);
            if (itemName.Contains("Input"))    return "AVEJ1_INPUT";
            if (itemName.Contains("NAC"))      return "AVEJ1_NAC";
            if (itemName.Contains("Aux"))      return "AVEJ1_AUX_POWER";
            if (itemName.Contains("Buzzer"))   return "AVEJ1_BUZZER";
            return "";
        }

        //private static string ResolveDetectorSubtype(string itemName)
        //{
        //    var parts = itemName.Split('.');
        //    if (parts.Length < 3) return "AV_DETECTOR";
        //    var prefix = parts[2];
        //    if (prefix == "1" || prefix == "4") return "AV_SENSOR_HUMO";
        //    if (prefix == "2" || prefix == "3") return "AV_MONITOREO";
        //    return "AV_DETECTOR";
        //}

        private static string ResolveState(object value) =>
            value?.ToString() == "5" ? "NORMAL" : "ACTIVATE_ON";

        #endregion
    }
}
