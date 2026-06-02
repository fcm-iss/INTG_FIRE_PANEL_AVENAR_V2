namespace INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects
{
    public class SecurOsCommand
    {
        public string ObjType   { get; }
        public string Id        { get; }
        public string EventName { get; }
        public string Params    { get; }

        public SecurOsCommand(string objType, string id, string eventName, string @params)
        {
            ObjType   = objType;
            Id        = id;
            EventName = eventName;
            Params    = @params;
        }
    }
}
