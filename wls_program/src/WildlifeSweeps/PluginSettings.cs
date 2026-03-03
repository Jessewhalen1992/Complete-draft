using System;
using System.IO;
using System.Text.Json;

namespace WildlifeSweeps
{
    public class PluginSettings
    {
        public double GroupOffsetX { get; set; } = 490.0;
        public double GroupOffsetY { get; set; } = 600.0;
        public double GroupSpacingX { get; set; } = 1280.0;
        public string PhotoLayer { get; set; } = "AS-PHOTO";
        public double ImageScale { get; set; } = 1.0;
        public double ImageRotationDegrees { get; set; } = 0.0;
        public int PhotoStartNumber { get; set; } = 1;
        public string NumberOrder { get; set; } = "LeftToRight";
        public string UtmZone { get; set; } = "11";
        public string FindingsLookupPath { get; set; } = string.Empty;
        public bool CompleteFromPhotosIncludeBufferExcludeOutside { get; set; } = false;
        public bool CompleteFromPhotosIncludeBufferIncludeAll { get; set; } = false;
        public bool CompleteFromPhotosIncludeQuarterLinework { get; set; } = false;

        public PluginSettings Clone()
        {
            return (PluginSettings)MemberwiseClone();
        }

        public static string GetDefaultSettingsPath()
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WildlifeSweeps");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "settings.json");
        }

        public void Save(string? path = null)
        {
            var targetPath = string.IsNullOrWhiteSpace(path) ? GetDefaultSettingsPath() : path;
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(targetPath, json);
        }

        public static PluginSettings Load(string? path = null)
        {
            var targetPath = string.IsNullOrWhiteSpace(path) ? GetDefaultSettingsPath() : path;
            if (!File.Exists(targetPath))
            {
                return new PluginSettings();
            }

            var json = File.ReadAllText(targetPath);
            return JsonSerializer.Deserialize<PluginSettings>(json) ?? new PluginSettings();
        }
    }
}
