namespace INTG_FIRE_PANEL_AVENAR.Domain.ValueObjects
{
    /// <summary>Tipo de objeto declarado en el .integration.json y el tipo del que cuelga.</summary>
    public class SecurOsObjectType
    {
        public string Type       { get; }
        public string ParentType { get; }
        public bool   IsPanel    { get; }

        public SecurOsObjectType(string type, string parentType, bool isPanel)
        {
            Type       = type;
            ParentType = parentType;
            IsPanel    = isPanel;
        }
    }
}
