using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace INTG_OPC
{
    /// <summary>
    /// Tabla persistente nombre de grupo OPC → número, usada en el segmento de grupo del securosId
    /// ("1-1.<grupo>.<instancia>"). Hace que el id no dependa de la posición del grupo en el browse:
    /// si aparece o desaparece un grupo, los demás conservan su número.
    /// Un grupo que no está en la tabla recibe el siguiente número libre y se guarda en el archivo.
    /// </summary>
    internal class OpcGroupIdTable
    {
        private const string FileName = "opc_group_ids.json";

        private readonly string                  _path;
        private readonly Dictionary<string, int> _idByGroup;
        private bool                             _changed;

        private OpcGroupIdTable(string path, Dictionary<string, int> idByGroup)
        {
            _path      = path;
            _idByGroup = idByGroup;
        }

        public static OpcGroupIdTable Load()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", FileName);
            return new OpcGroupIdTable(path, ReadIds(path));
        }

        public int GetOrAssign(string groupName)
        {
            int id;
            if (_idByGroup.TryGetValue(groupName, out id)) return id;

            id = NextFreeId();
            _idByGroup[groupName] = id;
            _changed = true;
            Console.WriteLine($"[OPC_Client]\tGrupo '{groupName}' no está en {FileName} — se le asigna el número {id}.");
            return id;
        }

        public void SaveIfChanged()
        {
            if (!_changed) return;

            var ordered = _idByGroup.OrderBy(kv => kv.Value).ToDictionary(kv => kv.Key, kv => (object)kv.Value);
            File.WriteAllText(_path, new JavaScriptSerializer().Serialize(ordered), new UTF8Encoding(false));
            _changed = false;
            Console.WriteLine($"[OPC_Client]\tTabla de grupos actualizada: {_path}");
        }

        private int NextFreeId() => _idByGroup.Count == 0 ? 1 : _idByGroup.Values.Max() + 1;

        private static Dictionary<string, int> ReadIds(string path)
        {
            var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path))
            {
                Console.WriteLine($"[OPC_Client]\t{FileName} no encontrado en Config\\ — los grupos se numerarán según aparezcan.");
                return ids;
            }

            var json = File.ReadAllText(path, new UTF8Encoding(false));
            foreach (var kv in new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json))
                ids[kv.Key.Trim()] = Convert.ToInt32(kv.Value);
            return ids;
        }
    }
}
