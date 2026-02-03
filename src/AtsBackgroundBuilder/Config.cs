using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AtsBackgroundBuilder
{
    /// <summary>
    /// Central configuration loaded from a JSON file beside the plugin DLL.
    /// - Defaults are written on first run.
    /// - When loading an older config, any missing properties keep their defaults.
    /// </summary>
    public sealed class Config
    {
        // -------------------------
        // Label placement
        // -------------------------
        public double TextHeight { get; set; } = 10.0;
        public string DimensionStyleName { get; set; } = "Compass 5 000";
        public int MaxOverlapAttempts { get; set; } = 25;
        public bool PlaceWhenOverlapFails { get; set; } = true;
        public bool UseRegionIntersection { get; set; } = true;

        // Back-compat: some previous iterations referenced this property name.
        public bool AllowLabelOverlap { get; set; } = false;

        // When false, a single disposition only gets labeled once even if it intersects multiple quarters.
        public bool AllowMultiQuarterDispositions { get; set; } = false;

        // -------------------------
        // Leaders / callouts
        // -------------------------
        public bool EnableLeaders { get; set; } = true;

        /// <summary>
        /// Radius for the "target" circle at the leader start point (0 disables).
        /// Units are drawing units (meters in your workflow).
        /// </summary>
        public double LeaderCircleRadius { get; set; } = 1.0;
        public string LeaderArrowBlockName { get; set; } = "DotBlank";

        // -------------------------
        // Section Index (Compass / Res Manager)
        // -------------------------
        public bool UseSectionIndex { get; set; } = true;
        public string SectionIndexFolder { get; set; } = @"C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\RES MANAGER";

        /// <summary>
        /// Buffer around section extents used to filter imported features.
        /// </summary>
        public double SectionBufferDistance { get; set; } = 100.0;

        // -------------------------
        // Shapefile import
        // -------------------------
        public string ShapefileFolder { get; set; } = @"C:\AUTOCAD-SETUP CG\SHAPE FILES";

        /// <summary>
        /// Shapefiles to import dispositions from (filenames only).
        /// </summary>
        public string[] DispositionShapefiles { get; set; } = new[] { "DAB_APPL.shp" };

        // -------------------------
        // Lookup tables (Excel)
        // -------------------------
        /// <summary>
        /// Folder where CompanyLookup.xlsx and PurposeLookup.xlsx live.
        /// </summary>
        public string LookupFolder { get; set; } = @"C:\AUTOCAD-SETUP CG\CG_LISP\AUTO UPDATE LABELS";

        public string CompanyLookupFile { get; set; } = "CompanyLookup.xlsx";
        public string PurposeLookupFile { get; set; } = "PurposeLookup.xlsx";

        // -------------------------
        // Width measurement / snapping
        // -------------------------
        /// <summary>
        /// How many samples along a corridor to estimate width variability.
        /// </summary>
        public int WidthSampleCount { get; set; } = 15;

        /// <summary>
        /// If max-min width exceeds this absolute tolerance (meters), treat as "Variable Width".
        /// </summary>
        public double VariableWidthAbsTolerance { get; set; } = 0.50;

        /// <summary>
        /// If max-min width exceeds this relative tolerance (ratio), treat as "Variable Width".
        /// </summary>
        public double VariableWidthRelTolerance { get; set; } = 0.05;

        /// <summary>
        /// Acceptable / standardized ROW widths (meters). If a measured width is within WidthSnapTolerance of
        /// one of these, snap to that value in the label.
        /// </summary>
        public double[] AcceptableRowWidths { get; set; } = new[]
        {
            10.50, 10.06, 3.05, 4.57, 6.10, 15.24, 20.00, 20.12,
            30.18, 30.48, 36.58, 18.29, 9.14, 7.62
        };

        public double WidthSnapTolerance { get; set; } = 0.25;

        /// <summary>
        /// For width-required purposes, allow label to be placed inside the quarter but outside the disposition polygon.
        /// </summary>
        public bool AllowOutsideDispositionForWidthPurposes { get; set; } = true;

        /// <summary>
        /// Purpose codes that require width + dimension-style labeling (per your latest notes).
        /// Match is case-insensitive after trimming.
        /// </summary>
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

        // -------------------------
        // Load / Save
        // -------------------------
        public static Config Load(string configPath, Logger logger)
        {
            var defaults = new Config();

            // First run: write defaults.
            if (!File.Exists(configPath))
            {
                defaults.Save(configPath, logger);
                return defaults;
            }

            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Start with defaults; selectively overwrite values that exist in the JSON.
                ApplyJson(defaults, root);

                // If older configs put widths as strings or with commas, try a lenient fix.
                defaults.AcceptableRowWidths = NormalizeWidthArray(defaults.AcceptableRowWidths);

                return defaults;
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

        private static void ApplyJson(Config cfg, JsonElement root)
        {
            // Helper lambdas
            static bool TryBool(JsonElement r, string name, out bool v)
            {
                v = default;
                if (!r.TryGetProperty(name, out var el)) return false;
                if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) { v = el.GetBoolean(); return true; }
                return false;
            }

            static bool TryInt(JsonElement r, string name, out int v)
            {
                v = default;
                if (!r.TryGetProperty(name, out var el)) return false;
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out v)) return true;
                return false;
            }

            static bool TryDouble(JsonElement r, string name, out double v)
            {
                v = default;
                if (!r.TryGetProperty(name, out var el)) return false;
                if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out v)) return true;

                // allow numeric strings
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString() ?? "";
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return true;
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v)) return true;
                }

                return false;
            }

            static bool TryString(JsonElement r, string name, out string? v)
            {
                v = null;
                if (!r.TryGetProperty(name, out var el)) return false;
                if (el.ValueKind == JsonValueKind.String) { v = el.GetString(); return true; }
                return false;
            }

            static bool TryStringArray(JsonElement r, string name, out string[]? v)
            {
                v = null;
                if (!r.TryGetProperty(name, out var el)) return false;
                if (el.ValueKind != JsonValueKind.Array) return false;

                var list = new List<string>();
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                    }
                }

                v = list.ToArray();
                return true;
            }

            static bool TryDoubleArray(JsonElement r, string name, out double[]? v)
            {
                v = null;
                if (!r.TryGetProperty(name, out var el)) return false;
                if (el.ValueKind != JsonValueKind.Array) return false;

                var list = new List<double>();
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var d))
                        list.Add(d);
                    else if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString() ?? "";
                        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dd))
                            list.Add(dd);
                        else if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out dd))
                            list.Add(dd);
                    }
                }

                if (list.Count > 0)
                {
                    v = list.ToArray();
                    return true;
                }

                return false;
            }

            // Doubles
            if (TryDouble(root, nameof(TextHeight), out var d) && d > 0) cfg.TextHeight = d;
            if (TryDouble(root, nameof(SectionBufferDistance), out d) && d >= 0) cfg.SectionBufferDistance = d;
            if (TryDouble(root, nameof(LeaderCircleRadius), out d) && d >= 0) cfg.LeaderCircleRadius = d;

            if (TryDouble(root, nameof(VariableWidthAbsTolerance), out d) && d >= 0) cfg.VariableWidthAbsTolerance = d;
            if (TryDouble(root, nameof(VariableWidthRelTolerance), out d) && d >= 0) cfg.VariableWidthRelTolerance = d;
            if (TryDouble(root, nameof(WidthSnapTolerance), out d) && d >= 0) cfg.WidthSnapTolerance = d;

            // Ints
            if (TryInt(root, nameof(MaxOverlapAttempts), out var i) && i > 0) cfg.MaxOverlapAttempts = i;
            if (TryInt(root, nameof(WidthSampleCount), out i) && i > 0) cfg.WidthSampleCount = i;

            // Bools
            if (TryBool(root, nameof(PlaceWhenOverlapFails), out var b)) cfg.PlaceWhenOverlapFails = b;
            if (TryBool(root, nameof(UseRegionIntersection), out b)) cfg.UseRegionIntersection = b;
            if (TryBool(root, nameof(AllowLabelOverlap), out b)) cfg.AllowLabelOverlap = b;
            if (TryBool(root, nameof(AllowMultiQuarterDispositions), out b)) cfg.AllowMultiQuarterDispositions = b;

            if (TryBool(root, nameof(EnableLeaders), out b)) cfg.EnableLeaders = b;
            if (TryBool(root, nameof(UseSectionIndex), out b)) cfg.UseSectionIndex = b;

            if (TryBool(root, nameof(AllowOutsideDispositionForWidthPurposes), out b))
                cfg.AllowOutsideDispositionForWidthPurposes = b;

            // Strings
            if (TryString(root, nameof(SectionIndexFolder), out var s) && !string.IsNullOrWhiteSpace(s))
                cfg.SectionIndexFolder = s.Trim();

            if (TryString(root, nameof(DimensionStyleName), out s) && !string.IsNullOrWhiteSpace(s))
                cfg.DimensionStyleName = s.Trim();

            if (TryString(root, nameof(LeaderArrowBlockName), out s) && !string.IsNullOrWhiteSpace(s))
                cfg.LeaderArrowBlockName = s.Trim();

            if (TryString(root, nameof(ShapefileFolder), out s) && !string.IsNullOrWhiteSpace(s))
                cfg.ShapefileFolder = s.Trim();

            if (TryString(root, nameof(LookupFolder), out s) && !string.IsNullOrWhiteSpace(s))
                cfg.LookupFolder = s.Trim();

            if (TryString(root, nameof(CompanyLookupFile), out s) && !string.IsNullOrWhiteSpace(s))
                cfg.CompanyLookupFile = s.Trim();

            if (TryString(root, nameof(PurposeLookupFile), out s) && !string.IsNullOrWhiteSpace(s))
                cfg.PurposeLookupFile = s.Trim();

            // Arrays
            if (TryStringArray(root, nameof(DispositionShapefiles), out var sa) && sa != null && sa.Length > 0)
                cfg.DispositionShapefiles = sa;

            if (TryStringArray(root, nameof(WidthRequiredPurposeCodes), out sa) && sa != null && sa.Length > 0)
                cfg.WidthRequiredPurposeCodes = sa;

            if (TryDoubleArray(root, nameof(AcceptableRowWidths), out var da) && da != null && da.Length > 0)
                cfg.AcceptableRowWidths = da;
        }

        private static double[] NormalizeWidthArray(double[] widths)
        {
            // Ensure unique, finite, sorted (helps snapping).
            return widths
                .Where(w => !double.IsNaN(w) && !double.IsInfinity(w) && w > 0)
                .Distinct()
                .OrderBy(w => w)
                .ToArray();
        }
    }
}
