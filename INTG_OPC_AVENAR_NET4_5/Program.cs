using INTG_OPC;
using INTG_OPC_AVENAR_NET4_5.Models;
using ISS.OPCMonitor;
using Opc.Da;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.ServiceProcess;
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

                ConfirmAndDeployIntegrationFiles();

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

        private static void ConfirmAndDeployIntegrationFiles()
        {
            Console.WriteLine("\n==================================================");
            Console.Write("[APP]\t\t¿Desea copiar los archivos de integración a SecurOS? (s/n): ");
            var answer = Console.ReadLine()?.Trim() ?? "";
            if (!answer.Equals("s", StringComparison.OrdinalIgnoreCase) &&
                !answer.Equals("si", StringComparison.OrdinalIgnoreCase)) return;

            var baseDir     = ResolveOutputBaseDir();
            var prefixClean = (ConfigurationManager.AppSettings["ddi_prefix"] ?? "").TrimEnd('_').ToLower();
            var intDir      = Path.Combine(baseDir, "Config", "integration");

            var securosDir     = ConfigurationManager.AppSettings["securos_install_dir"]     ?? "";
            var integrationDir = ConfigurationManager.AppSettings["securos_integration_dir"] ?? "";

            var ddiOk  = CopyFileToDirectory(Path.Combine(intDir, $"securos.{prefixClean}.ddi"),      securosDir);
            var dbiOk  = CopyFileToDirectory(Path.Combine(intDir, $"securos.{prefixClean}.dbi"),      securosDir);
            var jsonOk = CopyFileToDirectory(Path.Combine(intDir, $"{prefixClean}.integration.json"), integrationDir);

            if (!ddiOk || !dbiOk || !jsonOk)
            {
                Console.WriteLine("[APP]\t\tAlgunos archivos no se copiaron correctamente — ciclo de servicio cancelado.");
                return;
            }

            Console.WriteLine("\n==================================================");
            Console.Write("[APP]\t\t¿Desea detener el servicio, marcar el DBI y reiniciarlo? (s/n): ");
            var serviceAnswer = Console.ReadLine()?.Trim() ?? "";
            if (!serviceAnswer.Equals("s", StringComparison.OrdinalIgnoreCase) &&
                !serviceAnswer.Equals("si", StringComparison.OrdinalIgnoreCase)) return;

            var dbiDestPath = Path.Combine(securosDir, $"securos.{prefixClean}.dbi");
            var serviceName = ConfigurationManager.AppSettings["securos_service_name"] ?? "";
            StopMarkAndRestartService(serviceName, dbiDestPath);
        }

        private static void StopMarkAndRestartService(string displayName, string dbiPath)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                Console.WriteLine("[APP]\t\tsecuros_service_name no configurado — servicio omitido.");
                return;
            }

            using (var svc = FindServiceByDisplayName(displayName))
            {
                if (svc == null)
                {
                    Console.WriteLine($"[APP]\t\tServicio no encontrado: {displayName}");
                    return;
                }

                if (!StopService(svc))
                {
                    Console.WriteLine("[APP]\t\tNo se pudo detener el servicio — operación cancelada.");
                    return;
                }

                MarkFileAsArchive(dbiPath);
                StartService(svc);
            }
        }

        private static ServiceController FindServiceByDisplayName(string serviceName)
        {
            try   { return new ServiceController(serviceName); }
            catch { return null; }
        }

        private static bool StopService(ServiceController svc)
        {
            svc.Refresh();
            if (svc.Status == ServiceControllerStatus.Stopped)
            {
                Console.WriteLine("[APP]\t\tEl servicio ya estaba detenido.");
                return true;
            }

            try
            {
                Console.WriteLine($"[APP]\t\tDeteniendo servicio: {svc.DisplayName}...");
                svc.Stop();
                svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                svc.Refresh();

                if (svc.Status != ServiceControllerStatus.Stopped)
                {
                    Console.WriteLine($"[APP]\t\tEl servicio no se detuvo — estado actual: {svc.Status}");
                    return false;
                }

                Console.WriteLine("[APP]\t\tServicio detenido correctamente.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[APP]\t\tError al detener el servicio: {ex.Message}");
                return false;
            }
        }

        private static void MarkFileAsArchive(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"[APP]\t\tArchivo no encontrado para marcar: {Path.GetFileName(filePath)}");
                return;
            }

            File.SetAttributes(filePath, File.GetAttributes(filePath) | FileAttributes.Archive);
            Console.WriteLine($"[APP]\t\tMarcado como listo para archivar: {Path.GetFileName(filePath)}");
        }

        private static void StartService(ServiceController svc)
        {
            if (svc.Status == ServiceControllerStatus.Running) return;

            Console.WriteLine($"[APP]\t\tArrancando servicio: {svc.DisplayName}...");
            svc.Start();
            svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            Console.WriteLine("[APP]\t\tServicio en ejecución.");
        }

        private static bool CopyFileToDirectory(string sourcePath, string destDir)
        {
            var fileName = Path.GetFileName(sourcePath);

            if (string.IsNullOrWhiteSpace(destDir))
            {
                Console.WriteLine($"[APP]\t\tRuta de destino no configurada para: {fileName}");
                return false;
            }
            if (!File.Exists(sourcePath))
            {
                Console.WriteLine($"[APP]\t\tArchivo origen no encontrado: {fileName}");
                return false;
            }

            try
            {
                Directory.CreateDirectory(destDir);
                var destPath = Path.Combine(destDir, fileName);
                File.Copy(sourcePath, destPath, overwrite: true);

                if (!File.Exists(destPath))
                {
                    Console.WriteLine($"[APP]\t\tERROR: {fileName} no se encontró en destino tras la copia — verifica permisos de Administrador.");
                    return false;
                }

                Console.WriteLine($"[APP]\t\tCopiado OK: {fileName} → {destDir}");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine($"[APP]\t\tACCESO DENEGADO al copiar {fileName} — ejecuta la aplicación como Administrador.");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[APP]\t\tError al copiar {fileName}: {ex.Message}");
                return false;
            }
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
