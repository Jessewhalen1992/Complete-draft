using System;
using System.IO;
#if NET8_0_WINDOWS
using System.Text.Json;
#else
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
#endif

namespace AtsBackgroundBuilder
{
    public sealed class Config
    {
        public double TextHeight { get; set; } = 10.0;
        public int MaxOverlapAttempts { get; set; } = 25;
        public bool PlaceWhenOverlapFails { get; set; } = true;
        public bool UseRegionIntersection { get; set; } = true;

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
#if NET8_0_WINDOWS
                var json = File.ReadAllText(configPath);
                var loaded = JsonSerializer.Deserialize<Config>(json);
                return loaded ?? new Config();
#else
                using (var stream = File.OpenRead(configPath))
                {
                    var serializer = new DataContractJsonSerializer(typeof(Config));
                    var loaded = serializer.ReadObject(stream) as Config;
                    return loaded ?? new Config();
                }
#endif
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
#if NET8_0_WINDOWS
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);
#else
                using (var stream = File.Create(configPath))
                {
                    var serializer = new DataContractJsonSerializer(typeof(Config));
                    serializer.WriteObject(stream, this);
                }
#endif
            }
            catch (Exception ex)
            {
                logger.WriteLine("Config save failed: " + ex.Message);
            }
        }
    }
}
