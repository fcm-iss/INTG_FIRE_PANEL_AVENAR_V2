using INTG_FIRE_PANEL_AVENAR.Application;
using INTG_FIRE_PANEL_AVENAR.Domain;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
using INTG_FIRE_PANEL_AVENAR.Infrastructure;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace INTG_FIRE_PANEL_AVENAR
{
    internal class App
    {
        private enum AppMode { Integration, Monitor, CreateSecurOsObjects }

        public void Run(string[] args)
        {
            var mode = Array.Exists(args, a => a == "--integration") ? AppMode.Integration
                     : Array.Exists(args, a => a == "--create-securos-objects") ? AppMode.CreateSecurOsObjects
                     : AppMode.Monitor;
            //debug
            //habilita una de los modos para pruebas sin argumentos
            //mode = AppMode.Integration;
            // mode = AppMode.CreateSecurOsObjects;

            using (var opcManager = new OpcAdapter())
            {
                if (mode == AppMode.Integration)
                    RunIntegrationFlow(opcManager);
                else if (mode == AppMode.CreateSecurOsObjects)
                    RunCreateSecurOsObjectsFlow();
                else
                    RunMonitorFlow(opcManager);
            }
        }

        #region Integration Flow
        private static void RunIntegrationFlow(OpcAdapter opc)
        {
            var connectedUrl = DiscoverAndConnect(opc);
            if (connectedUrl == null) return;

            var exporterPath = ExportOpcFiles(opc, connectedUrl);
            if (string.IsNullOrEmpty(exporterPath)) return;

            bool serviceRestarted = ConfirmAndDeployIntegrationFiles();
            ConfirmAndCreateObjectsInSecurOs(exporterPath, serviceRestarted);
        }

        private static void ConfirmAndCreateObjectsInSecurOs(string treeFilePath, bool waitForRestart)
        {
            Console.WriteLine("\n==================================================");

            if (waitForRestart)
                WaitForSecurOsRestart();

            FlushConsoleInputBuffer();
            Console.Write("[APP]\t\t¿Desea crear los objetos en SecurOS? (s/n): ");
            var answer = Console.ReadLine()?.Trim() ?? "";
            Console.WriteLine($"[APP]\t\tRespuesta captada: {answer}");

            if (!answer.Equals("s", StringComparison.OrdinalIgnoreCase) &&
                !answer.Equals("si", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[APP]\t\tCreación de objetos omitida.");
                return;
            }

            Console.WriteLine("[APP]\t\tConectando a SecurOS...");
            using (var securos = SecurOsAdapter.Connect())
            {
                Console.WriteLine("[APP]\t\tCreando objetos...");
                CreateObjectsInSecurOs(securos, treeFilePath);
            }
        }

        private static void WaitForSecurOsRestart()
        {
            const int DelaySeconds = 30;
            Console.WriteLine($"[APP]\t\tEsperando {DelaySeconds}s para que SecurOS reinicie e importe el DDI/DBI...");
            for (int i = DelaySeconds; i > 0; i--)
            {
                Console.Write($"\r[APP]\t\t{i,2}s restantes...   ");
                System.Threading.Thread.Sleep(1000);
            }
            Console.WriteLine("\r[APP]\t\tListo.                          ");
        }

        private static void CreateObjectsInSecurOs(SecurOsAdapter securos, string treeFilePath)
        {
            var ddiPrefix = ConfigurationManager.AppSettings["ddi_prefix"] ?? "";
            Console.WriteLine($"[APP]\t\tPrefijo: {ddiPrefix}");
            try
            {
                securos.CreateObjectsInSecurOs(treeFilePath, ddiPrefix);
            }
            catch (Exception ex)
            {
                LogError($"Error al crear objetos en SecurOS: {ex.Message}");
            }
        }
        #endregion

        #region Create SecurOs Objects Flow
        private static void RunCreateSecurOsObjectsFlow()
        {
            var treeFilePath = FindExistingTreeFile();
            if (string.IsNullOrEmpty(treeFilePath)) return;

            var ddiPrefix = ConfigurationManager.AppSettings["ddi_prefix"] ?? "";
            try
            {
                using (var securos = SecurOsAdapter.Connect())
                    securos.CreateObjectsInSecurOs(treeFilePath, ddiPrefix);

                Console.WriteLine("[APP]\t\tObjetos creados exitosamente.");
            }
            catch (Exception ex)
            {
                LogError($"Error al crear objetos en SecurOS: {ex.Message}");
            }
        }
        #endregion

        #region Monitor Flow
        private static void RunMonitorFlow(OpcAdapter opc)
        {
            var exporterPath = FindExistingTreeFile();
            if (string.IsNullOrEmpty(exporterPath)) return;

            if (!ConnectFromTreeFile(opc, exporterPath)) return;

            using (var securosManager = SecurOsAdapter.Connect())
                StartMonitoring(opc, securosManager, exporterPath);
        }

        private static bool ConnectFromTreeFile(OpcAdapter opc, string exporterPath)
        {
            var serverUrl = OpcTreeReader.ReadServerUrlFromTreeJson(exporterPath);
            if (string.IsNullOrEmpty(serverUrl))
            {
                LogError("Campo 'server' no encontrado en OPCTree.json — el archivo fue generado con una versión anterior. Ejecute primero con --integration para regenerarlo.");
                return false;
            }
            if (!opc.Connect(serverUrl))
            {
                LogError($"No se pudo conectar al servidor OPC '{serverUrl}'. Verifique que el servidor esté activo.");
                return false;
            }
            return true;
        }
        #endregion

        // ══════════════════════════════════════════════════════════════════
        // Exportación OPC
        // ══════════════════════════════════════════════════════════════════
        #region Exportación OPC
        private static string ResolveOutputBaseDir()
        {
            var configured = ConfigurationManager.AppSettings["output_base_dir"];
            if (string.IsNullOrWhiteSpace(configured))
                return AppDomain.CurrentDomain.BaseDirectory;

            return Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configured));
        }

        private static string[] ResolveAllowedTypes()
        {
            var raw = ConfigurationManager.AppSettings["allowed_types"];
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return Array.ConvertAll(raw.Split(','), t => t.Trim());
        }

        private static string ExportOpcFiles(OpcAdapter opc, string selectedUrl)
        {
            var ddiPrefix = ConfigurationManager.AppSettings["ddi_prefix"] ?? "";
            var exporter = opc.BuildSecurOsFileExporter(ddiPrefix, selectedUrl);
            var baseDir = ResolveOutputBaseDir();
            var allowedTypes = ResolveAllowedTypes();
            var prefixClean = ddiPrefix.TrimEnd('_').ToLower();

            var treeDir = Path.Combine(baseDir, "Config", "tree");
            var integrationDir = Path.Combine(baseDir, "Config", "integration");

            var exporterPath = Path.Combine(treeDir, "OPCTree.json");
            var commandPath = Path.Combine(treeDir, "commands.json");
            var integrationPath = Path.Combine(integrationDir, $"{prefixClean}.integration.json");
            var ddiPath = Path.Combine(integrationDir, $"securos.{prefixClean}.ddi");
            var dbiPath = Path.Combine(integrationDir, $"securos.{prefixClean}.dbi");

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
        #endregion

        // ══════════════════════════════════════════════════════════════════
        // Despliegue de archivos
        // ══════════════════════════════════════════════════════════════════
        #region Despliegue de archivos
        private static bool ConfirmAndDeployIntegrationFiles()
        {
            Console.WriteLine("\n==================================================");
            Console.Write("[APP]\t\t¿Desea copiar los archivos de integración a SecurOS? (s/n): ");
            var answer = Console.ReadLine()?.Trim() ?? "";
            if (!answer.Equals("s", StringComparison.OrdinalIgnoreCase) &&
                !answer.Equals("si", StringComparison.OrdinalIgnoreCase)) return false;

            var baseDir = ResolveOutputBaseDir();
            var prefixClean = (ConfigurationManager.AppSettings["ddi_prefix"] ?? "").TrimEnd('_').ToLower();
            var intDir = Path.Combine(baseDir, "Config", "integration");

            var securosDir = ConfigurationManager.AppSettings["securos_install_dir"] ?? "";
            var integrationDir = ConfigurationManager.AppSettings["securos_integration_dir"] ?? "";

            var ddiOk = CopyFileToDirectory(Path.Combine(intDir, $"securos.{prefixClean}.ddi"), securosDir);
            var dbiOk = CopyFileToDirectory(Path.Combine(intDir, $"securos.{prefixClean}.dbi"), securosDir);
            var jsonOk = CopyFileToDirectory(Path.Combine(intDir, $"{prefixClean}.integration.json"), integrationDir);

            if (!ddiOk || !dbiOk || !jsonOk)
            {
                Console.WriteLine("[APP]\t\tAlgunos archivos no se copiaron correctamente — ciclo de servicio cancelado.");
                return false;
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  --------------------------------------------------------------");
            Console.WriteLine("   [!]  ATENCION: En este paso el servicio SecurOS se detendra");
            Console.WriteLine("  --------------------------------------------------------------");
            Console.WriteLine("   > Detendra el servicio SecurOS");
            Console.WriteLine("   > Marcara el DBI como pendiente de importar");
            Console.WriteLine("   > Reiniciara el servicio");
            Console.WriteLine("  --------------------------------------------------------------");
            Console.ResetColor();
            Console.Write("\n[APP]\t\t¿Desea continuar? (s/n): ");
            var serviceAnswer = Console.ReadLine()?.Trim() ?? "";
            if (!serviceAnswer.Equals("s", StringComparison.OrdinalIgnoreCase) &&
                !serviceAnswer.Equals("si", StringComparison.OrdinalIgnoreCase)) return false;

            var dbiDestPath = Path.Combine(securosDir, "securos.dbi");
            var serviceName = ConfigurationManager.AppSettings["securos_service_name"] ?? "";
            StopMarkAndRestartService(serviceName, dbiDestPath);
            return true;
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
            try { return new ServiceController(serviceName); }
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
        #endregion

        // ══════════════════════════════════════════════════════════════════
        // Monitoreo
        // ══════════════════════════════════════════════════════════════════
        #region Monitoreo
        private static int ResolveUpdateRateMs()
        {
            var raw = ConfigurationManager.AppSettings["monitoring_update_rate_ms"];
            int ms;
            return int.TryParse(raw, out ms) && ms > 0 ? ms : 1000;
        }

        private static void StartMonitoring(OpcAdapter opc, ISecurOsGateway securOs, string exporterPath)
        {
            var nodeIdMap = OpcTreeReader.BuildNodeIdMapFromTreeJson(exporterPath);
            var commandNodeMap = OpcTreeReader.BuildCommandNodeMapFromTreeJson(exporterPath);

            if (!opc.StartMonitoring(nodeIdMap, ResolveUpdateRateMs()))
            {
                Console.WriteLine("[APP]\t\tNo se pudo inicializar el grupo. Abortando.");
                return;
            }

            // La cola garantiza que las operaciones OPC (WriteCommand, browse del árbol) se ejecutan en el hilo STA principal,
            // ya que los objetos COM de OPC DA solo pueden llamarse desde el hilo donde fueron creados.
            var opcQueue = new BlockingCollection<Action>();
            var syncState = new SyncItemStateUseCase(securOs);
            var execCommand = new ExecuteSecurOsCommandUseCase(xml => opcQueue.TryAdd(() => opc.WriteCommand(xml)), commandNodeMap);

            opc.StateChanged += changes => syncState.Execute(changes);
            securOs.CommandReceived += cmd => execCommand.Execute(cmd);
            securOs.ResetRequested += () => EnqueueTreeRefresh(opcQueue, opc, exporterPath);

            RegisterShutdownHandlers(opcQueue);

            Console.WriteLine("\n==================================================");
            Console.WriteLine("[APP]\t\tMonitoreo activo. Ctrl+C o cerrar ventana para detener.");
            Console.WriteLine("==================================================\n");

            foreach (var opcAction in opcQueue.GetConsumingEnumerable())
                opcAction();

            Console.WriteLine("[APP]\t\tMonitoreo detenido. Desconectando...");
        }

        private static void RegisterShutdownHandlers(BlockingCollection<Action> writeQueue)
        {
            void SignalShutdown()
            {
                if (!writeQueue.IsAddingCompleted)
                    writeQueue.CompleteAdding();
            }

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\n[APP]\t\tCtrl+C detectado — deteniendo monitoreo...");
                SignalShutdown();
            };

            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                Console.WriteLine("[APP]\t\tCierre detectado — desconectando OPC y SecurOS...");
                SignalShutdown();
            };
        }
        #endregion

        // ══════════════════════════════════════════════════════════════════
        // Relectura del árbol OPC (INTEGRATION RESET)
        // ══════════════════════════════════════════════════════════════════
        #region Relectura del árbol OPC
        private static int _treeRefreshPending;

        private static void EnqueueTreeRefresh(BlockingCollection<Action> opcQueue, OpcAdapter opc, string exporterPath)
        {
            // Varios RESET seguidos producen una sola relectura: el browse completo es costoso.
            if (Interlocked.CompareExchange(ref _treeRefreshPending, 1, 0) != 0)
            {
                Console.WriteLine("[APP]\t\tRESET recibido — ya hay una relectura del árbol OPC pendiente.");
                return;
            }

            try
            {
                opcQueue.Add(() => RefreshTreeAndReportChanges(opc, exporterPath));
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref _treeRefreshPending, 0);
            }
        }

        private static void RefreshTreeAndReportChanges(OpcAdapter opc, string exporterPath)
        {
            try
            {
                Console.WriteLine("[APP]\t\tRESET recibido — releyendo árbol OPC...");
                var ddiPrefix     = ConfigurationManager.AppSettings["ddi_prefix"] ?? "";
                var refreshedPath = ExportRefreshedTree(opc, exporterPath, ddiPrefix);
                var changes       = new DetectOpcTreeChangesUseCase(ddiPrefix).Execute(exporterPath, refreshedPath);
                WriteChangesReport(changes, Path.Combine(Path.GetDirectoryName(exporterPath), "OPCTree.changes.json"));
            }
            catch (Exception ex)
            {
                LogError($"Error al releer el árbol OPC: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _treeRefreshPending, 0);
            }
        }

        private static string ExportRefreshedTree(OpcAdapter opc, string exporterPath, string ddiPrefix)
        {
            var serverUrl     = OpcTreeReader.ReadServerUrlFromTreeJson(exporterPath);
            var refreshedPath = Path.Combine(Path.GetDirectoryName(exporterPath), "OPCTree.reset.json");
            opc.BuildSecurOsFileExporter(ddiPrefix, serverUrl).ExportOpcTreeWithPropertiesToJson(refreshedPath);
            return refreshedPath;
        }

        private static void WriteChangesReport(OpcTreeChanges changes, string reportPath)
        {
            var report = new Dictionary<string, object>
            {
                { "createdAt", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") },
                { "added",     changes.Added },
                { "removed",   changes.Removed },
                { "renamed",   changes.Renamed }
            };
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(report);
            File.WriteAllText(reportPath, json, new UTF8Encoding(false));
            Console.WriteLine($"[APP]\t\tReporte de cambios: {reportPath}");
        }
        #endregion

        // ══════════════════════════════════════════════════════════════════
        // Descubrimiento de Servidor OPC
        // ══════════════════════════════════════════════════════════════════
        #region Descubrimiento de Servidor OPC
        private static string DiscoverAndConnect(OpcAdapter opc)
        {
            var url = DiscoverAndSelectOpcServer(opc);
            if (string.IsNullOrEmpty(url)) return null;

            if (opc.Connect(url)) return url;

            Console.WriteLine("[APP]\t\tNo se pudo establecer conexión con el servidor OPC. Abortando.");
            return null;
        }

        private static string DiscoverAndSelectOpcServer(IOpcGateway opc)
        {
            var servers = opc.GetAvailableServers();

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
        #endregion

        // ══════════════════════════════════════════════════════════════════
        // Utilidades
        // ══════════════════════════════════════════════════════════════════
        #region Utilidades
        private static string FindExistingTreeFile()
        {
            var path = Path.Combine(ResolveOutputBaseDir(), "Config", "tree", "OPCTree.json");

            if (!File.Exists(path))
            {
                LogError("OPCTree.json no encontrado en Config\\tree\\. Ejecute primero con --integration para generarlo.");
                return null;
            }

            Console.WriteLine("[APP]\t\tUsando archivo existente: Config\\tree\\OPCTree.json");
            return path;
        }

        private static void FlushConsoleInputBuffer()
        {
            while (Console.KeyAvailable)
                Console.ReadKey(true);
        }

        private static void LogError(string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var line = $"[{timestamp}] [ERROR] {message}";

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[APP]\t\t{line}");
            Console.ResetColor();

            try
            {
                var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
                File.AppendAllText(logPath, line + Environment.NewLine);
            }
            catch { }
        }
        #endregion
    }
}
