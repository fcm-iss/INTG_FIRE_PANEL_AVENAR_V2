using INTG_FIRE_PANEL_AVENAR.Application;
using INTG_FIRE_PANEL_AVENAR.Domain;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
using INTG_FIRE_PANEL_AVENAR.Infrastructure;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace INTG_FIRE_PANEL_AVENAR.Flows
{
    /// <summary>
    /// INTEGRATION RESET durante el monitoreo: relee el árbol OPC, compara con el activo (OPCTree.json),
    /// aplica las diferencias en SecurOS (DELETE / CREATE / UPDATE), promueve el árbol nuevo como activo,
    /// recarga la suscripción OPC y el mapa de comandos y, al terminar, responde el RESET a SecurOS.
    /// Debe ejecutarse en el hilo STA de la cola OPC.
    /// </summary>
    internal class OpcTreeResetFlow
    {
        private const string RefreshedTreeFileName = "OPCTree.reset.json";
        private const string PreviousTreeFileName  = "OPCTree.previous.json";
        private const string ChangesFileName       = "OPCTree.changes.json";

        private readonly OpcAdapter                   _opc;
        private readonly ISecurOsGateway              _securOs;
        private readonly ExecuteSecurOsCommandUseCase _execCommand;
        private readonly string                       _exporterPath;
        private readonly int                          _updateRateMs;
        private readonly string                       _ddiPrefix           = ConfigurationManager.AppSettings["ddi_prefix"] ?? "";
        private readonly string                       _integrationParentId = ConfigurationManager.AppSettings["idUinP"] ?? "1";
        private int                                   _refreshPending;

        public OpcTreeResetFlow(OpcAdapter opc, ISecurOsGateway securOs, ExecuteSecurOsCommandUseCase execCommand,
                                string exporterPath, int updateRateMs)
        {
            _opc          = opc;
            _securOs      = securOs;
            _execCommand  = execCommand;
            _exporterPath = exporterPath;
            _updateRateMs = updateRateMs;
        }

        public void Enqueue(BlockingCollection<Action> opcQueue)
        {
            // Varios RESET seguidos producen una sola relectura: el browse completo es costoso.
            if (Interlocked.CompareExchange(ref _refreshPending, 1, 0) != 0)
            {
                Console.WriteLine("[RESET]\t\tRESET recibido — ya hay una relectura del árbol OPC pendiente.");
                return;
            }

            try
            {
                opcQueue.Add(Run);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref _refreshPending, 0);
            }
        }

        private void Run()
        {
            try
            {
                if (SyncTreeWithSecurOs())
                    RespondToReset();
                else
                    Console.WriteLine("[RESET]\t\tLa actualización no terminó — no se responde el RESET a SecurOS.");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshPending, 0);
            }
        }

        // Relee el árbol y, si cambió, aplica las diferencias en SecurOS y en el monitoreo.
        // Devuelve true solo si todo quedó actualizado (sin cambios cuenta como actualizado).
        private bool SyncTreeWithSecurOs()
        {
            try
            {
                Console.WriteLine("[RESET]\t\tRESET recibido — releyendo árbol OPC...");
                
                var refreshedPath = ExportRefreshedTree();
                if (!HasPanels(refreshedPath))
                {
                    Console.WriteLine("[RESET]\t\tLa relectura no trajo ningún panel (¿servidor OPC sin respuesta?) — " +
                                      "se descarta: no se borra nada en SecurOS ni se reemplaza OPCTree.json.");
                    return false;
                }

                var changes = new DetectOpcTreeChangesUseCase(_ddiPrefix).Execute(_exporterPath, refreshedPath);
                WriteChangesReport(changes);
                if (!changes.HasChanges) return true;

                new ApplyOpcTreeChangesUseCase(_securOs, _integrationParentId).Execute(changes);
                PromoteRefreshedTree(refreshedPath);
                return ReloadMonitoring();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RESET]\t\tError al procesar el RESET: {ex.Message}");
                return false;
            }
        }

        // Último paso del RESET: envía los objetos del árbol activo (OPCTree.json), que a esta altura
        // ya es el promovido o, si no hubo cambios, equivale al releído.
        private void RespondToReset()
        {
            _securOs.SendResetResponse(OpcTreeReader.ExtractObjectDefinitionsFromTree(_exporterPath, _ddiPrefix));
        }

        private string ExportRefreshedTree()
        {
            var serverUrl     = OpcTreeReader.ReadServerUrlFromTreeJson(_exporterPath);
            var refreshedPath = PathNextToActiveTree(RefreshedTreeFileName);
            _opc.BuildSecurOsFileExporter(_ddiPrefix, serverUrl).ExportOpcTreeWithPropertiesToJson(refreshedPath);
            return refreshedPath;
        }

        // Un árbol sin paneles es una lectura fallida, no un panel vacío: compararlo contra el activo
        // daría todos los objetos como eliminados y se borrarían en SecurOS.
        private bool HasPanels(string treePath) =>
            OpcTreeReader.ExtractObjectDefinitionsFromTree(treePath, _ddiPrefix).Any(d => d.IsPanel);

        private void WriteChangesReport(OpcTreeChanges changes)
        {
            var reportPath = PathNextToActiveTree(ChangesFileName);
            var report = new Dictionary<string, object>
            {
                { "createdAt", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") },
                { "added",     changes.Added },
                { "removed",   changes.Removed },
                { "renamed",   changes.Renamed }
            };
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(report);
            File.WriteAllText(reportPath, json, new UTF8Encoding(false));
            Console.WriteLine($"[RESET]\t\tReporte de cambios: {reportPath}");
        }

        // El árbol nuevo pasa a ser el activo, para que el próximo RESET compare contra el estado ya aplicado.
        private void PromoteRefreshedTree(string refreshedPath)
        {
            File.Copy(_exporterPath, PathNextToActiveTree(PreviousTreeFileName), overwrite: true);
            File.Copy(refreshedPath, _exporterPath, overwrite: true);
            Console.WriteLine($"[RESET]\t\tÁrbol activo actualizado (anterior en {PreviousTreeFileName}).");
        }

        // Suscribe los ítems nuevos, deja de monitorear los eliminados y actualiza los comandos disponibles.
        private bool ReloadMonitoring()
        {
            _execCommand.ReplaceCommandNodeMap(OpcTreeReader.BuildCommandNodeMapFromTreeJson(_exporterPath));

            var nodeIdMap = OpcTreeReader.BuildNodeIdMapFromTreeJson(_exporterPath);
            var reloaded  = _opc.RestartMonitoring(nodeIdMap, _updateRateMs);
            Console.WriteLine(reloaded
                ? "[RESET]\t\tSuscripción OPC recargada con el árbol nuevo."
                : "[RESET]\t\tNo se pudo recargar la suscripción OPC — reinicie la integración.");
            return reloaded;
        }

        private string PathNextToActiveTree(string fileName) =>
            Path.Combine(Path.GetDirectoryName(_exporterPath), fileName);
    }
}
