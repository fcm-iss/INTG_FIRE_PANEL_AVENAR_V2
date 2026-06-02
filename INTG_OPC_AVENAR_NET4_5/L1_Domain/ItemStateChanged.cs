namespace INTG_OPC_AVENAR_NET4_5.Domain
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
