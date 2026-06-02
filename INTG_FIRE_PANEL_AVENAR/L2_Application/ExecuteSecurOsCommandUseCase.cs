using INTG_FIRE_PANEL_AVENAR.Domain;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
using System;
using System.Collections.Generic;

namespace INTG_FIRE_PANEL_AVENAR.Application
{
    public class ExecuteSecurOsCommandUseCase
    {
        private readonly Action<string> _enqueueWrite;
        private readonly Dictionary<string, Tuple<string, Dictionary<string, string>>> _commandNodeMap;

        public ExecuteSecurOsCommandUseCase(
            Action<string> enqueueWrite,
            Dictionary<string, Tuple<string, Dictionary<string, string>>> commandNodeMap)
        {
            _enqueueWrite   = enqueueWrite;
            _commandNodeMap = commandNodeMap;
        }

        public void Execute(SecurOsCommand cmd)
        {
            RouteToOpc(cmd);
        }

        private void RouteToOpc(SecurOsCommand cmd)
        {
            var handle = $"{cmd.ObjType}|{cmd.Id}";

            Tuple<string, Dictionary<string, string>> nodeInfo;
            if (!_commandNodeMap.TryGetValue(handle, out nodeInfo))
            {
                Console.WriteLine($"[APP]\t\tNo se encontró elemento OPC para handle: {handle}");
                return;
            }

            string commandXml;
            if (!nodeInfo.Item2.TryGetValue(cmd.EventName, out commandXml))
            {
                Console.WriteLine($"[APP]\t\tComando '{cmd.EventName}' no definido para ítem: {nodeInfo.Item1}");
                return;
            }

            _enqueueWrite(CompleteCommandXml(commandXml, nodeInfo.Item1));
        }

        private static string CompleteCommandXml(string commandXml, string itemName)
        {
            commandXml = commandXml.Replace("\\\"", "\"");

            var idx = commandXml.LastIndexOf("/>");
            if (idx < 0) return commandXml;

            return commandXml.Substring(0, idx)
                + $" Sender=\"BIS\" Adresse=\"{itemName}\""
                + commandXml.Substring(idx);
        }
    }
}
