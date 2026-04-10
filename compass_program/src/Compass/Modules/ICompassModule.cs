namespace Compass.Modules;

public interface ICompassModule
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }

    /// <summary>
    /// Ensures the palette required for the module is created and made visible.
    /// </summary>
    void Show();
}
