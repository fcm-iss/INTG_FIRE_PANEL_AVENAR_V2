using INTG_FIRE_PANEL_AVENAR.Application;
using INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;

namespace INTG_FIRE_PANEL_AVENAR.Flows
{
    /// <summary>
    /// Modo de prueba: menú interactivo para enviar a SecurOS comandos CORE de creación,
    /// actualización y eliminación de objetos, pidiendo los parámetros por consola.
    /// Los tipos disponibles se leen del .integration.json desplegado; las respuestas de SecurOS
    /// aparecen en consola como "[SecurOS] Recibido <- ...".
    /// </summary>
    internal class SecurOsCommandTestFlow
    {
        private const string ExitOption = "0";

        private readonly string                  _integrationParentId = ConfigurationManager.AppSettings["idUinP"] ?? "1";
        private readonly List<SecurOsObjectType> _types;

        public SecurOsCommandTestFlow(string integrationJsonPath)
        {
            _types = LoadTypes(integrationJsonPath);
        }

        public void Run()
        {
            using (var securos = SecurOsAdapter.Connect())
            {
                string option;
                while ((option = AskMenuOption(securos)) != ExitOption)
                    ExecuteOption(securos, option);
            }

            Console.WriteLine("[TEST]\t\tModo test finalizado.");
        }

        private static List<SecurOsObjectType> LoadTypes(string integrationJsonPath)
        {
            if (File.Exists(integrationJsonPath))
                return IntegrationStructureReader.ReadObjectTypes(integrationJsonPath);

            Console.WriteLine($"[TEST]\t\tNo se encontró {integrationJsonPath} — el objtype se pedirá como texto libre.");
            return new List<SecurOsObjectType>();
        }

        #region Menú

        private static string AskMenuOption(SecurOsAdapter securos)
        {
            Console.WriteLine("\n==================================================");
            Console.WriteLine("[TEST]\t\tComandos disponibles:");
            Console.WriteLine("         1- CREATE_OBJECT  (crear objeto)");
            Console.WriteLine("         2- UPDATE_OBJECT  (actualizar objeto)");
            Console.WriteLine("         3- DELETE_OBJECT  (eliminar objeto)");
            Console.WriteLine($"         4- Cambiar cabecera CORE (actual: {FormatCoreHeader(securos.CoreTargetId)})");
            Console.WriteLine("         0- Salir");
            Console.Write("Seleccione una opción: ");
            return Console.ReadLine()?.Trim() ?? ExitOption;
        }

        private void ExecuteOption(SecurOsAdapter securos, string option)
        {
            switch (option)
            {
                case "1": SendCreate(securos); break;
                case "2": SendUpdate(securos); break;
                case "3": SendDelete(securos); break;
                case "4": ToggleCoreHeader(securos); break;
                default:  Console.WriteLine("[TEST]\t\tOpción no válida."); break;
            }
        }

        private static void ToggleCoreHeader(SecurOsAdapter securos)
        {
            securos.CoreTargetId = securos.CoreTargetId == "1" ? "" : "1";
            Console.WriteLine($"[TEST]\t\tCabecera CORE: {FormatCoreHeader(securos.CoreTargetId)}");
        }

        private static string FormatCoreHeader(string coreTargetId) => $"CORE|{coreTargetId}|";

        #endregion

        #region Comandos

        private void SendCreate(SecurOsAdapter securos)
        {
            var type     = AskObjectType();
            var objId    = AskRequired("objid");
            var name     = AskRequired("name");
            var parentId = AskParentId(type);
            securos.CreateObject(type.Type, objId, name, parentId);
        }

        private void SendUpdate(SecurOsAdapter securos)
        {
            var type     = AskObjectType();
            var objId    = AskRequired("objid");
            var name     = AskOptional("name nuevo");
            var parentId = AskOptional("parent_id nuevo");
            securos.UpdateObject(type.Type, objId, name, parentId);
        }

        private void SendDelete(SecurOsAdapter securos)
        {
            var type  = AskObjectType();
            var objId = AskRequired("objid");
            securos.DeleteObject(type.Type, objId);
        }

        // Un panel cuelga del punto de integración (idUinP); un ítem cuelga del objid de un panel.
        private string AskParentId(SecurOsObjectType type)
        {
            if (type.IsPanel)
                return AskWithDefault($"parent_id (objid del {type.ParentType})", _integrationParentId);

            var parentHint = string.IsNullOrEmpty(type.ParentType) ? "objid del padre" : $"objid del {type.ParentType}, ej. 1-1";
            return AskRequired($"parent_id ({parentHint})");
        }

        #endregion

        #region Selección de tipo

        private SecurOsObjectType AskObjectType()
        {
            if (_types.Count == 0)
                return new SecurOsObjectType(AskRequired("objtype"), parentType: "", isPanel: false);

            PrintTypes();
            while (true)
            {
                var value = AskRequired("objtype (número de la lista o nombre)");
                var type  = FindType(value);
                if (type != null) return type;
                Console.WriteLine("[TEST]\t\tTipo no encontrado en el .integration.json.");
            }
        }

        private void PrintTypes()
        {
            Console.WriteLine("[TEST]\t\tTipos disponibles:");
            for (int i = 0; i < _types.Count; i++)
            {
                var t    = _types[i];
                var kind = t.IsPanel ? $"panel → padre: {t.ParentType}" : $"ítem  → padre: {t.ParentType}";
                Console.WriteLine($"         {i + 1,2}- {t.Type,-40} [{kind}]");
            }
        }

        private SecurOsObjectType FindType(string value)
        {
            int index;
            if (int.TryParse(value, out index) && index >= 1 && index <= _types.Count)
                return _types[index - 1];

            return _types.Find(t => string.Equals(t.Type, value, StringComparison.OrdinalIgnoreCase));
        }

        #endregion

        #region Lectura de parámetros

        private static string AskRequired(string label)
        {
            while (true)
            {
                var value = AskValue($"{label}: ");
                if (!string.IsNullOrEmpty(value)) return value;
                Console.WriteLine("[TEST]\t\tEste parámetro es obligatorio.");
            }
        }

        private static string AskOptional(string label) => AskValue($"{label} (Enter = no enviar): ");

        private static string AskWithDefault(string label, string defaultValue)
        {
            var value = AskValue($"{label} (Enter = {defaultValue}): ");
            return string.IsNullOrEmpty(value) ? defaultValue : value;
        }

        // '<', '>' y ',' delimitan los parámetros del mensaje IIDK: un valor que los contenga lo rompería.
        private static string AskValue(string prompt)
        {
            while (true)
            {
                Console.Write($"         {prompt}");
                var value = Console.ReadLine()?.Trim() ?? "";
                if (value.IndexOfAny(new[] { '<', '>', ',' }) < 0) return value;
                Console.WriteLine("[TEST]\t\tEl valor no puede contener '<', '>' ni ','.");
            }
        }

        #endregion
    }
}
