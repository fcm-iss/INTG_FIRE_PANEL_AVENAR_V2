using INTG_OPC;
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
            var securOs = SecurOsEventSender.Connect();

            using (var opcClient = new OpcClient())
            {
                var selectedUrl = DiscoverAndSelectOpcServer(opcClient);
                if (string.IsNullOrEmpty(selectedUrl)) return;

                if (!opcClient.Connect(selectedUrl))
                {
                    Console.WriteLine("[APP] No se pudo establecer conexión. Abortando.");
                    return;
                }

                var exporterPath = ExportOpcFiles(opcClient, selectedUrl);

                StartMonitoring(opcClient, exporterPath, securOs);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Exportación OPC
        // ══════════════════════════════════════════════════════════════════

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

        private static void StartMonitoring(OpcClient opcClient, string exporterPath, SecurOsEventSender securOs)
        {
            var nodeIdMapByItemName = OpcIntegrationExporter.BuildNodeIdMapFromTreeJson(exporterPath);

            if (!opcClient.StartIntegrationMonitoring(nodeIdMapByItemName))
            {
                Console.WriteLine("[APP] No se pudo inicializar el grupo. Abortando.");
                return;
            }

            opcClient.PanelChanged += (values) => OnPanelChanged(values, securOs);

            Console.WriteLine("\n==================================================");
            Console.WriteLine("[APP] Presiona cualquier tecla para desconectar...");
            Console.WriteLine("==================================================\n");
            Console.ReadKey();
        }

        private static void OnPanelChanged(ItemValueResult[] itemsValue, SecurOsEventSender securOs)
        {
            foreach (var item in itemsValue)
            {
                if (item.Quality.QualityBits == qualityBits.bad)
                    Console.WriteLine($"[PANEL] Desconectado — ítem: {item.ItemName}");
                else
                    HandleValidItem(item, securOs);
            }
        }

        private static void HandleValidItem(ItemValueResult item, SecurOsEventSender securOs)
        {
            Console.WriteLine($"[PANEL] Cambio: {item.ItemName} - {item.ClientHandle} = {item.Value}");
            securOs.Send(item);
        }

        // ══════════════════════════════════════════════════════════════════
        // Descubrimiento de Servidor OPC
        // ══════════════════════════════════════════════════════════════════

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

        private static void PrintServerMenu(IReadOnlyList<string> servers)
        {
            var optionNumber = 1;
            foreach (var url in servers)
                Console.WriteLine($"{optionNumber++}- {url}");
            Console.Write("Seleccione una opción: ");
        }
    }
}
