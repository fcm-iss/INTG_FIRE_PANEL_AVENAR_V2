using INTG_OPC;

using System;
using System.Configuration;
using System.Linq;

namespace INTG_OPC_AVENAR_NET4_5
{
    internal class Program
    {
        static void Main(string[] args)
        {
            //seleciona la url del servidor OPC desde la configuracion en App.config
            //var opcServerUrl = ConfigurationManager.AppSettings["opcda_url"];


            // 'using' garantiza Dispose() automático al salir del bloque,
            // incluso si ocurre una excepción — libera conexión y recursos COM.
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

                opcClient.ReadItems();
                Console.WriteLine("\n==================================================");
                Console.WriteLine("[APP] Presiona cualquier tecla para desconectar...");
                Console.WriteLine("==================================================\n");
                Console.ReadKey();
            }
            // Disconnect() y Dispose() se llaman automáticamente aquí
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
