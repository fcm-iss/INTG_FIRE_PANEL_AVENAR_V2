using INTG_OPC;
using INTG_OPC_AVENAR_NET4_5.Models;
using ISS.OPCMonitor;
using Opc.Da;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Threading.Tasks;

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
                    : FindExistingTreeExporterFile();

                if (string.IsNullOrEmpty(exporterPath))
                {
                    Console.WriteLine("[APP]\t\tNo hay archivo de árbol disponible. Monitoreo no disponible.");
                    return;
                }

                if (!ConfirmStartMonitoring()) return;

                using (var securOs = SecurOsConnector.Connect())
                    StartMonitoring(opcClient, exporterPath, securOs, ResolveUpdateRateMs());
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Exportación OPC
        // ══════════════════════════════════════════════════════════════════

        private static string ResolveOutputBaseDir()
        {
            var configured = ConfigurationManager.AppSettings["output_base_dir"];
            if (string.IsNullOrWhiteSpace(configured))
                return AppDomain.CurrentDomain.BaseDirectory;

            return Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configured));
        }

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
            var baseDir      = ResolveOutputBaseDir();
            var allowedTypes = ResolveAllowedTypes();
            var prefixClean  = ddiPrefix.TrimEnd('_').ToLower();

            var treeDir        = Path.Combine(baseDir, "Config", "tree");
            var integrationDir = Path.Combine(baseDir, "Config", "integration");

            var exporterPath    = Path.Combine(treeDir,        "treeExporter.json");
            var commandPath     = Path.Combine(treeDir,        "commands.json");
            var integrationPath = Path.Combine(integrationDir, $"{prefixClean}.integration.json");
            var ddiPath         = Path.Combine(integrationDir, $"securos.{prefixClean}.ddi");
            var dbiPath         = Path.Combine(integrationDir, $"securos.{prefixClean}.dbi");

            if (!ConfirmOverwriteIfAnyExists(new[] { exporterPath, commandPath, integrationPath, ddiPath, dbiPath }))
            {
                Console.WriteLine("[APP]\t\tExportación cancelada. Usando archivos existentes.");
                return File.Exists(exporterPath) ? exporterPath : null;
            }

            EnsureDirectoryExists(treeDir);
            EnsureDirectoryExists(integrationDir);

            exporter.ExportOpcTreeWithPropertiesToJson(exporterPath);
            exporter.ExportCommandsToJson(commandPath);
            exporter.ExportIntegrationJson(exporterPath, integrationPath, allowedTypes);
            exporter.ExportDdiFile(exporterPath, ddiPath, allowedTypes);
            exporter.ExportDbiFile(exporterPath, dbiPath, allowedTypes);

            return exporterPath;
        }

        private static bool ConfirmOverwriteIfAnyExists(string[] paths)
        {
            bool anyExists = false;
            foreach (var p in paths)
            {
                if (!File.Exists(p)) continue;
                if (!anyExists)
                {
                    Console.WriteLine("\n[APP]\t\tYa existen los siguientes archivos:");
                    anyExists = true;
                }
                Console.WriteLine($"         - {Path.GetFileName(p)}");
            }

            if (!anyExists) return true;

            Console.Write("[APP]\t\t¿Desea sobreescribirlos? (s/n): ");
            var answer = Console.ReadLine()?.Trim() ?? "";
            return answer.Equals("s", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("si", StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
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

        private static string FindExistingTreeExporterFile()
        {
            var path = Path.Combine(ResolveOutputBaseDir(), "Config", "tree", "treeExporter.json");

            if (!File.Exists(path))
            {
                Console.WriteLine("[APP]\t\tNo se encontró treeExporter.json en Config\\tree\\");
                return null;
            }

            Console.WriteLine("[APP]\t\tUsando archivo existente: Config\\tree\\treeExporter.json");
            return path;
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
            var commandNodeMap      = OpcIntegrationExporter.BuildCommandNodeMapFromTreeJson(exporterPath);

            if (!opcClient.StartIntegrationMonitoring(nodeIdMapByItemName, updateRateMs))
            {
                Console.WriteLine("[APP]\t\tNo se pudo inicializar el grupo. Abortando.");
                return;
            }

            var writeQueue = new BlockingCollection<string>();

            opcClient.PanelChanged  += values => OnPanelChanged(values, securOs);
            securOs.MessageReceived += msg    => OnSecurOsMessage(msg, commandNodeMap, xml => writeQueue.TryAdd(xml));

            Console.WriteLine("\n==================================================");
            Console.WriteLine("[APP]\t\tPresiona cualquier tecla para desconectar...");
            Console.WriteLine("==================================================\n");

            Task.Run(() => { Console.ReadKey(true); writeQueue.CompleteAdding(); });

            foreach (var xml in writeQueue.GetConsumingEnumerable())
                opcClient.WriteCommand(xml);
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

        private static void OnSecurOsMessage(
            SecurOsMessage msg,
            Dictionary<string, Tuple<string, Dictionary<string, string>>> commandNodeMap,
            Action<string> enqueueWrite)
        {
            Console.WriteLine($"[SecurOS]\tComando recibido — ObjType: {msg.ObjType} | Id: {msg.Id} | Evento: {msg.EventName} | Params: {msg.Params}");
            RouteSecurOsCommandToOpc(msg, commandNodeMap, enqueueWrite);
        }

        private static void RouteSecurOsCommandToOpc(SecurOsMessage msg,
            Dictionary<string, Tuple<string, Dictionary<string, string>>> commandNodeMap,
            Action<string> enqueueWrite)
        {
            var handle = $"{msg.ObjType}|{msg.Id}";
            Tuple<string, Dictionary<string, string>> nodeInfo;
            if (!commandNodeMap.TryGetValue(handle, out nodeInfo))
            {
                Console.WriteLine($"[APP]\t\tNo se encontró elemento OPC para handle: {handle}");
                return;
            }

            string commandXml;
            if (!nodeInfo.Item2.TryGetValue(msg.EventName, out commandXml))
            {
                Console.WriteLine($"[APP]\t\tComando '{msg.EventName}' no definido para ítem: {nodeInfo.Item1}");
                return;
            }

            enqueueWrite(EnrichCommandXml(commandXml, nodeInfo.Item1));
        }

        private static string EnrichCommandXml(string commandXml, string itemName)
        {
            commandXml = commandXml.Replace("\\\"", "\"");

            var idx = commandXml.LastIndexOf("/>");
            if (idx < 0) return commandXml;

            return commandXml.Substring(0, idx)
                + $" Sender=\"BIS\" Adresse=\"{itemName}\""
                + commandXml.Substring(idx);
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
