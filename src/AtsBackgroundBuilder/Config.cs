/////////////////////////////////////////////////////////////////////

using System;
using System.IO;
using System.Text.Json;

namespace AtsBackgroundBuilder
{
    public sealed class Config
    {
        // -------------------------
        // Label placement
        // -------------------------
        public double TextHeight { get; set; } = 10.0;
        public int MaxOverlapAttempts { get; set; } = 25;
        public bool PlaceWhenOverlapFails { get; set; } = true;
        public bool UseRegionIntersection { get; set; } = true;

        // -------------------------
        // Leaders (circle + line)
        // -------------------------
        public bool EnableLeaders { get; set; } = true;
        public double LeaderCircleRadius { get; set; } = 0.75;

        // -------------------------
        // Lookups
        // -------------------------
        public string LookupFolder { get; set; } = @"C:\AUTOCAD-SETUP CG\CG_LISP\AUTO UPDATE LABELS";
        public string CompanyLookupFile { get; set; } = "CompanyLookup.xlsx";
        public string PurposeLookupFile { get; set; } = "PurposeLookup.xlsx";

        // -------------------------
        // Width measurement / snapping
        // -------------------------
        public string[] WidthRequiredPurposeCodes { get; set; } = new[]
        {
            "PIPELINE",
            "ACCESS",
            "POWERLINE",
            "ACCESS ROAD",
            "VEGETATION CONTROL",
            "FLOW LINE",
            "FRESH WATER",
            "COMMUNICATIONS CABLE",
            "WATER PIPELINE",
            "DRAINAGE AND IRRIGATION",
            "FLOWLINE"
        };

        public double[] AcceptableRowWidths { get; set; } = new[]
        {
            10.50, 10.06, 3.05, 4.57, 6.10, 15.24, 20.12,
            30.18, 30.48, 36.58, 18.29, 9.14, 7.62
        };

        public double WidthSnapTolerance { get; set; } = 0.25;
        public int WidthSampleCount { get; set; } = 7;

        public double VariableWidthAbsTolerance { get; set; } = 0.50;
        public double VariableWidthRelTolerance { get; set; } = 0.15;

        public bool AllowOutsideDispositionForWidthPurposes { get; set; } = true;

        // -------------------------
        // Section index / importer
        // -------------------------
        public bool UseSectionIndex { get; set; } = true;

        // Note: this path is from your existing setup. Adjust if required.
        public string SectionIndexFolder { get; set; } = @"C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\RES MANAGER";

        public bool ImportAdjacentSections { get; set; } = true;

        public bool ImportDispositionShapefiles { get; set; } = true;
        public string DispositionShapefileFolder { get; set; } = "";
        // Backwards-compatible aliases used by some modules (older naming).
        // Prefer ImportDispositionShapefiles / DispositionShapefileFolder in new code.
        public bool DispositionShapefiles
        {
            get => ImportDispositionShapefiles;
            set => ImportDispositionShapefiles = value;
        }

        public string ShapefileFolder
        {
            get => DispositionShapefileFolder;
            set => DispositionShapefileFolder = value ?? string.Empty;
        }

        /// <summary>
        /// Optional buffer distance (drawing units) to expand the section/quarter extents when importing shapefiles.
        /// </summary>
        public double SectionBufferDistance { get; set; } = 0.0;

        public string[] DispositionShapefileNames { get; set; } = new[] { "dispositions.shp" };

        public bool AllowMultiQuarterDispositions { get; set; } = true;

        public static Config Load(string path, Logger logger)
        {
            if (!File.Exists(path))
            {
                var cfg = new Config();
                Save(path, cfg, logger);
                return cfg;
            }

            try
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<Config>(json);

                if (cfg == null)
                    throw new Exception("Config deserialize returned null.");

                return cfg;
            }
            catch (Exception ex)
            {
                logger.WriteLine($"Config load failed ({path}): {ex.Message}");
                var cfg = new Config();
                Save(path, cfg, logger);
                return cfg;
            }
        }

        public static void Save(string path, Config cfg, Logger logger)
        {
            try
            {
                var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                logger.WriteLine($"Config save failed ({path}): {ex.Message}");
            }
        }
    }
}