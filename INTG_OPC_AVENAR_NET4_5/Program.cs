using INTG_OPC;
using Opc;
using Opc.Da;
using System;
using System.Configuration;
using System.Linq;
using System.Net.Sockets;
using System.Windows.Input;

namespace INTG_OPC_AVENAR_NET4_5
{
    internal class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            using (var opcClient = new OpcClient())
            {
                var selectedUrl = DiscoverAndSelectOpcServer(opcClient);

                if (string.IsNullOrEmpty(selectedUrl))
                    return;

                if (!opcClient.Connect(selectedUrl))
                {
                    Console.WriteLine("[APP] No se pudo establecer conexión. Abortando.");
                    return;
                }

                if (!opcClient.InitializeSubscriptionGroup())
                {
                    Console.WriteLine("[APP] No se pudo inicializar el grupo. Abortando.");
                    return;
                }

                //var pahtJson = opcClient.ExportTreeToJson();
                var pathjsonClean =  opcClient.ExportCleanTreeToJson();

                //Console.WriteLine($"Path arbol completo{pahtJson}");
                Console.WriteLine($"Path arbol limpio {pathjsonClean}");

                opcClient.PanelChanged += (values) => OnPanelChanged(values, opcClient);

                //opcClient.ReadItems();
                Console.WriteLine("\n==================================================");
                Console.WriteLine("[APP] Presiona cualquier tecla para desconectar...");
                Console.WriteLine("==================================================\n");

               

                Console.ReadKey();                              
            }
        }

        private static void OnPanelChanged(Opc.Da.ItemValueResult[] values, OpcClient opcClient)
        {
            foreach (var r in values)
            {
                if (r.Quality.QualityBits == Opc.Da.qualityBits.bad)
                {
                    Console.WriteLine($"[PANEL] Desconectado — ítem: {r.ItemName}");                    
                }
                else
                {
                    Console.WriteLine($"[PANEL] Cambio: {r.ItemName} = {r.Value}");
                    //obtener propiedades.
                    //otneret pripiedad 5001
                    var propierties = opcClient.GetItemProperties(r.ItemName, 5001);

                    //buscar comandos 
                    var comands = opcClient.GetItemProperties("CMD", int.Parse(propierties["DetectorTypeID"]));
                    //escribir comandos

                    if (comands != null && comands.ContainsKey("ACTIVATE_OFF"))
                    {
                        var results = opcClient.Write("CMD", comands["ACTIVATE_OFF"]);
                    }
                }

            }
        }

        /// <summary>
        /// Consulta los servidores OPC DA disponibles en el host local y,
        /// si hay más de uno, presenta un menú numerado para que el usuario
        /// elija con cuál conectar. Repite la pregunta hasta recibir una
        /// opción válida. Retorna la URL del servidor seleccionado.
        /// </summary>
        private static string DiscoverAndSelectOpcServer(OpcClient opcClient)
        {
            var servers = opcClient.GetAvailableServers();

            if (servers == null || servers.Count == 0)
            {
                Console.WriteLine("[APP] No se encontraron servidores OPC disponibles.");
                return null;
            }

            if (servers.Count == 1)
            {
                Console.WriteLine($"[APP] Servidor seleccionado: {servers[0]}");
                return servers[0];
            }

            int selectedOption;
            do
            {
                var optionNumber = 1;
                foreach (var url in servers)
                    Console.WriteLine($"{optionNumber++}- {url}");

                Console.Write("Seleccione una opción: ");

                if (!int.TryParse(Console.ReadLine()?.Trim(), out selectedOption))
                {
                    Console.WriteLine("[APP] Por favor ingrese un número válido.");
                    selectedOption = 0;
                }
                else if (selectedOption < 1 || selectedOption > servers.Count)
                {
                    Console.WriteLine("[APP] Opción seleccionada no existe, intente de nuevo.");
                    selectedOption = 0;
                }

            } while (selectedOption < 1 || selectedOption > servers.Count);

            var selected = servers[selectedOption - 1];
            Console.WriteLine($"[APP] Servidor seleccionado: {selected}");
            return selected;
        }
    }
}
