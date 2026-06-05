namespace INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects
{
    public class SecurOsObjectDefinition
    {
        public string ObjType  { get; }
        public string ObjId    { get; }
        public string Name     { get; }
        public string ParentId { get; }
        public bool   IsPanel  { get; }

        public SecurOsObjectDefinition(string objType, string objId, string name, string parentId, bool isPanel = false)
        {
            ObjType  = objType;
            ObjId    = objId;
            Name     = name;
            ParentId = parentId;
            IsPanel  = isPanel;
        }
    }
}
