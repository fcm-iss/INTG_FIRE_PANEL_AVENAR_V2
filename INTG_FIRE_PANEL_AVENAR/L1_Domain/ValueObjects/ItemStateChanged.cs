namespace INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects
{
    public class ItemStateChanged
    {
        public string ItemName { get; }
        public string NodeId   { get; }
        public object Value    { get; }
        public bool   IsValid  { get; }

        public ItemStateChanged(string itemName, string nodeId, object value, bool isValid)
        {
            ItemName = itemName;
            NodeId   = nodeId;
            Value    = value;
            IsValid  = isValid;
        }
    }
}
