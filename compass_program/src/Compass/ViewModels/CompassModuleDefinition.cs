namespace Compass.ViewModels;

public class CompassModuleDefinition
{
    public CompassModuleDefinition(string id, string displayName, string description, int displayOrder)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        DisplayOrder = displayOrder;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public int DisplayOrder { get; }
}
