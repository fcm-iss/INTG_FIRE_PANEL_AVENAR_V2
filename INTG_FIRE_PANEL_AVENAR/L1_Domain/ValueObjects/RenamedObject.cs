namespace INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects
{
    public class RenamedObject
    {
        public string ObjType { get; }
        public string ObjId   { get; }
        public string OldName { get; }
        public string NewName { get; }

        public RenamedObject(string objType, string objId, string oldName, string newName)
        {
            ObjType = objType;
            ObjId   = objId;
            OldName = oldName;
            NewName = newName;
        }
    }
}
