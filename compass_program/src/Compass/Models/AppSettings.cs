namespace Compass.Models;

public class AppSettings
{
    public string BaseBlockFolder { get; set; } = string.Empty;
    public string[] DefaultLayerNames { get; set; } = new string[0];
    public string JsonConfigName { get; set; } = "drillProps.json";
}
