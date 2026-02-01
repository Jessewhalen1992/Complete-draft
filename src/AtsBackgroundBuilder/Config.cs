using System;
using System.IO;
using System.Text.Json;

namespace AtsBackgroundBuilder
{
    public sealed class Config
    {
        public double TextHeight { get; set; } = 10.0;
        public int MaxOverlapAttempts { get; set; } = 25;
        public bool PlaceWhenOverlapFails { get; set; } = true;
        public bool UseRegionIntersection { get; set; } = true;
        public bool UseSectionIndex { get; set; } = true;
        public string SectionIndexFolder { get; set; } = string.Empty;

        public static Config Load(string configPath, Logger logger)
        {
            if (!File.Exists(configPath))
            {
                var config = new Config();
                config.Save(configPath, logger);
                return config;
            }

            try
            {
                var json = File.ReadAllText(configPath);
                var loaded = JsonSerializer.Deserialize<Config>(json);
                return loaded ?? new Config();
            }
            catch (Exception ex)
            {
                logger.WriteLine("Config load failed, using defaults: " + ex.Message);
                return new Config();
            }
        }

        private void Save(string configPath, Logger logger)
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);
            }
            catch (Exception ex)
            {
                logger.WriteLine("Config save failed: " + ex.Message);
            }
        }
    }
}
