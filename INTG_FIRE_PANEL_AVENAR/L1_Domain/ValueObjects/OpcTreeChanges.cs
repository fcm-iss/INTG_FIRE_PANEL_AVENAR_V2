using System.Collections.Generic;

namespace INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects
{
    public class OpcTreeChanges
    {
        public List<SecurOsObjectDefinition> Added   { get; }
        public List<SecurOsObjectDefinition> Removed { get; }
        public List<RenamedObject>           Renamed { get; }

        public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Renamed.Count > 0;

        public OpcTreeChanges(List<SecurOsObjectDefinition> added,
                              List<SecurOsObjectDefinition> removed,
                              List<RenamedObject> renamed)
        {
            Added   = added;
            Removed = removed;
            Renamed = renamed;
        }
    }
}
