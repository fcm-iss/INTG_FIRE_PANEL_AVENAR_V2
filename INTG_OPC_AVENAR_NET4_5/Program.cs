using INTG_OPC;
using INTG_OPC_AVENAR_NET4_5.Models;
using ISS.OPCMonitor;
using Opc.Da;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;

namespace INTG_OPC_AVENAR_NET4_5
{
    internal class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            RunApp();
        }

        private static void RunApp()
        {
            using (var securOs = SecurOsConnector.Connect())
            using (var opcClient = new OpcClient())
            {
                var selectedUrl = DiscoverAndSelectOpcServer(opcClient);
                if (string.IsNullOrEmpty(selectedUrl)) return;

                if (!opcClient.Connect(selectedUrl))
                {
                    Console.WriteLine("[APP]\t\tNo se pudo establecer conexión con el servidor OPC. Abortando.");
                    return;
                }

                var exporterPath = ConfirmCreateIntegrationFiles()
                    ? ExportOpcFiles(opcClient, selectedUrl)
                    : FindLatestTreeExporterFile();
                    
                if (string.IsNullOrEmpty(exporterPath))
                {
                    Console.WriteLine("[APP]\t\tNo hay archivo de árbol disponible. Monitoreo no disponible.");
                    return;
                }

                if (ConfirmStartMonitoring())
                    StartMonitoring(opcClient, exporterPath, securOs, ResolveUpdateRateMs());
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Exportación OPC
        // ══════════════════════════════════════════════════════════════════

        private static int ResolveUpdateRateMs()
        {
            var raw = ConfigurationManager.AppSettings["monitoring_update_rate_ms"];
            int ms;
            return int.TryParse(raw, out ms) && ms > 0 ? ms : 1000;
        }

        private static string[] ResolveAllowedTypes()
        {
            var raw = ConfigurationManager.AppSettings["allowed_types"];
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return Array.ConvertAll(raw.Split(','), t => t.Trim());
        }

        private static string ExportOpcFiles(OpcClient opcClient, string selectedUrl)
        {
            var ddiPrefix    = ConfigurationManager.AppSettings["ddi_prefix"] ?? "";
            var exporter     = new OpcIntegrationExporter(opcClient.Server, ddiPrefix, selectedUrl);
            var timestamp    = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var baseDir      = AppDomain.CurrentDomain.BaseDirectory;
            var allowedTypes = ResolveAllowedTypes();
            var prefixClean  = ddiPrefix.TrimEnd('_').ToLower();

            var exporterPath    = Path.Combine(baseDir, $"treeExporter_{timestamp}.json");
            var commandPath     = Path.Combine(baseDir, $"commands_{timestamp}.json");
            var integrationPath = Path.Combine(baseDir, $"{timestamp}_{prefixClean}.integration.json");
            var ddiPath         = Path.Combine(baseDir, $"{timestamp}_securos.{prefixClean}.ddi");
            var dbiPath         = Path.Combine(baseDir, $"{timestamp}_securos.{prefixClean}.dbi");

            exporter.ExportOpcTreeWithPropertiesToJson(exporterPath);
            exporter.ExportCommandsToJson(commandPath);
            exporter.ExportIntegrationJson(exporterPath, integrationPath, allowedTypes);
            exporter.ExportDdiFile(exporterPath, ddiPath, allowedTypes);
            exporter.ExportDbiFile(exporterPath, dbiPath, allowedTypes);

            return exporterPath;
        }

        // ══════════════════════════════════════════════════════════════════
        // Monitoreo
        // ══════════════════════════════════════════════════════════════════

        private static bool ConfirmCreateIntegrationFiles()
        {
            Console.WriteLine("\n==================================================");
            Console.Write("[APP]\t\t¿Desea generar los archivos de integración? (s/n): ");
            var answer = Console.ReadLine()?.Trim() ?? "";
            return answer.Equals("s", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("si", StringComparison.OrdinalIgnoreCase);
        }

        private static string FindLatestTreeExporterFile()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var files   = Directory.GetFiles(baseDir, "treeExporter_*.json");

            if (files.Length == 0)
            {
                Console.WriteLine("[APP]\t\tNo se encontró ningún archivo treeExporter previo.");
                return null;
            }

            Array.Sort(files);
            var latest = files[files.Length - 1];
            Console.WriteLine($"[APP]\t\tUsando archivo existente: {Path.GetFileName(latest)}");
            return latest;
        }

        private static bool ConfirmStartMonitoring()
        {
            Console.WriteLine("\n==================================================");
            Console.Write("[APP]\t\t¿Desea iniciar el monitoreo? (s/n): ");
            var answer = Console.ReadLine()?.Trim() ?? "";
            return answer.Equals("s", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("si", StringComparison.OrdinalIgnoreCase);
        }

        private static void StartMonitoring(OpcClient opcClient, string exporterPath, SecurOsConnector securOs, int updateRateMs)
        {
            var nodeIdMapByItemName = OpcIntegrationExporter.BuildNodeIdMapFromTreeJson(exporterPath);

            if (!opcClient.StartIntegrationMonitoring(nodeIdMapByItemName, updateRateMs))
            {
                Console.WriteLine("[APP]\t\tNo se pudo inicializar el grupo. Abortando.");
                return;
            }

            opcClient.PanelChanged  += values => OnPanelChanged(values, securOs);
            securOs.MessageReceived += msg    => OnSecurOsMessage(msg);

            Console.WriteLine("\n==================================================");
            Console.WriteLine("[APP]\t\tPresiona cualquier tecla para desconectar...");
            Console.WriteLine("==================================================\n");
            Console.ReadKey();
        }

        private static void OnPanelChanged(ItemValueResult[] itemsValue, SecurOsConnector securOs)
        {
            foreach (var item in itemsValue)
            {
                if (item.Quality.QualityBits == qualityBits.bad)
                    Console.WriteLine($"[PANEL]\t\tDesconectado — ítem: {item.ItemName}");
                else
                    HandleValidItem(item, securOs);
            }
        }

        private static void HandleValidItem(ItemValueResult item, SecurOsConnector securOs)
        {
            Console.WriteLine($"[PANEL]\t\tCambio: {item.ItemName} - {item.ClientHandle} = {item.Value}");
            securOs.Send(item);
        }

        private static void OnSecurOsMessage(SecurOsMessage msg)
        {
            Console.WriteLine($"[SecurOS]\tComando recibido — ObjType: {msg.ObjType} | Id: {msg.Id} | Evento: {msg.EventName} | Params: {msg.Params}");
        }

        // ══════════════════════════════════════════════════════════════════
        // Descubrimiento de Servidor OPC
        // ══════════════════════════════════════════════════════════════════

        private static string DiscoverAndSelectOpcServer(OpcClient opcClient)
        {
            var servers = opcClient.GetAvailableServers();

            if (servers == null || servers.Count == 0)
            {
                Console.WriteLine("[APP]\t\tNo se encontraron servidores OPC disponibles.");
                return null;
            }

            if (servers.Count == 1)
            {
                Console.WriteLine($"[APP]\t\tServidor seleccionado: {servers[0]}");
                return servers[0];
            }

            return SelectServerFromMenu(servers);
        }

        private static string SelectServerFromMenu(IReadOnlyList<string> servers)
        {
            int selectedOption;
            do
            {
                PrintServerMenu(servers);

                if (!int.TryParse(Console.ReadLine()?.Trim(), out selectedOption))
                {
                    Console.WriteLine("[APP]\t\tPor favor ingrese un número válido.");
                    selectedOption = 0;
                }
                else if (selectedOption < 1 || selectedOption > servers.Count)
                {
                    Console.WriteLine("[APP]\t\tOpción seleccionada no existe, intente de nuevo.");
                    selectedOption = 0;
                }

            } while (selectedOption < 1 || selectedOption > servers.Count);

            var selected = servers[selectedOption - 1];
            Console.WriteLine($"[APP]\t\tServidor seleccionado: {selected}");
            return selected;
        }

        private static void PrintServerMenu(IReadOnlyList<string> servers)
        {
            var optionNumber = 1;
            foreach (var url in servers)
                Console.WriteLine($"{optionNumber++}- {url}");
            Console.Write("Seleccione una opción: ");
        }
    }
}
