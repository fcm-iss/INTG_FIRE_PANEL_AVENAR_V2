using INTG_FIRE_PANEL_AVENAR.Domain;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
using System;
using System.Collections.Generic;

namespace INTG_FIRE_PANEL_AVENAR.Application
{
    public class ExecuteSecurOsCommandUseCase
    {
        private readonly Action<string> _enqueueWrite;
        // volatile: se reemplaza desde el hilo OPC tras un RESET y se lee desde el hilo de eventos de SecurOS.
        private volatile Dictionary<string, (string itemName, Dictionary<string, string> commands)> _commandNodeMap;

        public ExecuteSecurOsCommandUseCase(
            Action<string> enqueueWrite,
            Dictionary<string, (string itemName, Dictionary<string, string> commands)> commandNodeMap)
        {
            _enqueueWrite   = enqueueWrite;
            _commandNodeMap = commandNodeMap;
        }

        public void ReplaceCommandNodeMap(Dictionary<string, (string itemName, Dictionary<string, string> commands)> commandNodeMap)
        {
            _commandNodeMap = commandNodeMap;
        }

        public void Execute(SecurOsCommand cmd)
        {
            var securosKey = $"{cmd.ObjType}|{cmd.Id}";

            if (!_commandNodeMap.TryGetValue(securosKey, out var node))
            {
                Console.WriteLine($"[APP]\t\tNo se encontró elemento OPC para: {securosKey}");
                return;
            }

            if (!node.commands.TryGetValue(cmd.EventName, out var commandXml))
            {
                Console.WriteLine($"[APP]\t\tComando '{cmd.EventName}' no definido para ítem: {node.itemName}");
                return;
            }

            _enqueueWrite(CompleteCommandXml(commandXml, node.itemName));
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
