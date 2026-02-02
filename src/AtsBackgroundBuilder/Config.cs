// FILE: C:\Users\Work Test 2\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\Config.cs
/////////////////////////////////////////////////////////////////////

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
        public string SectionIndexFolder { get; set; } = "C:\\AUTOCAD-SETUP CG\\CG_LISP\\COMPASS\\RES MANAGER";
        public double SectionBufferDistance { get; set; } = 100.0;
        public string ShapefileFolder { get; set; } = "C:\\AUTOCAD-SETUP CG\\SHAPE FILES";
        public string[] DispositionShapefiles { get; set; } = new[] { "DAB_APPL.shp" };

        public static Config Load(string configPath, Logger logger)
        {
            var defaults = new Config();
            if (!File.Exists(configPath))
            {
                defaults.Save(configPath, logger);
                return defaults;
            }

            try
            {
                var json = File.ReadAllText(configPath);
                var loaded = JsonSerializer.Deserialize<Config>(json);
                return MergeDefaults(defaults, loaded);
            }
            catch (Exception ex)
            {
                logger.WriteLine("Config load failed, using defaults: " + ex.Message);
                return defaults;
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

        private static Config MergeDefaults(Config defaults, Config? loaded)
        {
            if (loaded == null)
            {
                return defaults;
            }

            defaults.TextHeight = loaded.TextHeight;
            defaults.MaxOverlapAttempts = loaded.MaxOverlapAttempts;
            defaults.PlaceWhenOverlapFails = loaded.PlaceWhenOverlapFails;
            defaults.UseRegionIntersection = loaded.UseRegionIntersection;
            defaults.UseSectionIndex = loaded.UseSectionIndex;
            defaults.SectionBufferDistance = loaded.SectionBufferDistance;

            if (!string.IsNullOrWhiteSpace(loaded.SectionIndexFolder))
            {
                defaults.SectionIndexFolder = loaded.SectionIndexFolder;
            }

            if (!string.IsNullOrWhiteSpace(loaded.ShapefileFolder))
            {
                defaults.ShapefileFolder = loaded.ShapefileFolder;
            }

            if (loaded.DispositionShapefiles != null && loaded.DispositionShapefiles.Length > 0)
            {
                defaults.DispositionShapefiles = loaded.DispositionShapefiles;
            }

            return defaults;
        }
    }
}

/////////////////////////////////////////////////////////////////////
