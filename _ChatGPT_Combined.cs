// AUTO-GENERATED MERGE FILE FOR REVIEW
// Generated: 2026-02-03 13:13:03

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\Config.cs
/////////////////////////////////////////////////////////////////////

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

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\ExcelLookup.cs
/////////////////////////////////////////////////////////////////////

/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.IO;

namespace AtsBackgroundBuilder
{
    public sealed class LookupEntry
    {
        public LookupEntry(string value, string extra)
        {
            Value = value;
            Extra = extra;
        }

        public string Value { get; }
        public string Extra { get; }
    }

    /// <summary>
    /// Simple XLSX lookup reader:
    /// - Column 1: key
    /// - Column 2: value
    /// - Column 3 (optional): extra
    ///
    /// This loader uses HDR=NO to support headerless sheets (your current CompanyLookup.xlsx / PurposeLookup.xlsx).
    /// If the sheet has headers, typical header rows are auto-skipped.
    /// </summary>
    public sealed class ExcelLookup
    {
        private readonly Dictionary<string, LookupEntry> _lookup = new Dictionary<string, LookupEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _values = new List<string>();

        public bool IsLoaded { get; private set; }

        public static ExcelLookup Load(string xlsxPath, Logger logger)
        {
            var lookup = new ExcelLookup();
            if (!File.Exists(xlsxPath))
            {
                logger.WriteLine("Lookup not found: " + xlsxPath);
                return lookup;
            }

            try
            {
#if NET8_0_WINDOWS
                lookup.LoadWithOleDb(xlsxPath, logger);
#else
                lookup.LoadWithOleDb(xlsxPath, logger);
#endif
                lookup.IsLoaded = true;
            }
            catch (Exception ex)
            {
                logger.WriteLine("Lookup load failed: " + ex.Message);
            }

            return lookup;
        }

        public LookupEntry? Lookup(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (_lookup.TryGetValue(key.Trim(), out var entry))
            {
                return entry;
            }

            return null;
        }

        public IReadOnlyList<string> Values => _values;

        private void LoadWithOleDb(string xlsxPath, Logger logger)
        {
            // HDR=NO so first row is treated as data (lookup files are headerless)
            var connString =
                $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={xlsxPath};Extended Properties=\"Excel 12.0 Xml;HDR=NO;IMEX=1\";";

            using (var conn = new OleDbConnection(connString))
            {
                conn.Open();

                // Discover first worksheet name
                var schema = conn.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null);
                if (schema == null || schema.Rows.Count == 0)
                {
                    logger.WriteLine("No worksheets found in: " + xlsxPath);
                    return;
                }

                var sheetName = schema.Rows[0]["TABLE_NAME"].ToString();
                if (string.IsNullOrWhiteSpace(sheetName))
                {
                    logger.WriteLine("Could not determine worksheet name in: " + xlsxPath);
                    return;
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT * FROM [{sheetName}]";

                    using (var adapter = new OleDbDataAdapter(cmd))
                    {
                        var dt = new DataTable();
                        adapter.Fill(dt);

                        if (dt.Columns.Count < 2)
                        {
                            logger.WriteLine("Lookup has fewer than 2 columns: " + xlsxPath);
                            return;
                        }

                        foreach (DataRow row in dt.Rows)
                        {
                            var key = row[0]?.ToString()?.Trim();
                            var value = row[1]?.ToString()?.Trim();
                            var extra = dt.Columns.Count >= 3 ? row[2]?.ToString()?.Trim() : "";

                            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                            {
                                continue;
                            }

                            if (IsHeaderRow(key, value))
                            {
                                continue;
                            }

                            if (!_lookup.ContainsKey(key))
                            {
                                _lookup[key] = new LookupEntry(value, extra ?? "");
                            }

                            if (!_values.Contains(value))
                            {
                                _values.Add(value);
                            }
                        }
                    }
                }
            }

            logger.WriteLine($"Loaded lookup ({_lookup.Count} rows): {xlsxPath}");
        }

        private static bool IsHeaderRow(string key, string value)
        {
            // CompanyLookup typical headers
            if (key.Equals("key", StringComparison.OrdinalIgnoreCase) && value.Equals("value", StringComparison.OrdinalIgnoreCase))
                return true;

            // PurposeLookup typical headers
            if (key.Equals("purpcd", StringComparison.OrdinalIgnoreCase))
                return true;

            // Generic
            if (key.Equals("company", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }
}

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\GeometryUtils.cs
/////////////////////////////////////////////////////////////////////

/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public static class GeometryUtils
    {
        public static bool ExtentsIntersect(Extents3d a, Extents3d b)
{
    // AutoCAD's Extents3d does not provide an IsDisjoint helper in all versions.
    // Treat touching as intersecting.
    return !(
        a.MaxPoint.X < b.MinPoint.X || a.MinPoint.X > b.MaxPoint.X ||
        a.MaxPoint.Y < b.MinPoint.Y || a.MinPoint.Y > b.MaxPoint.Y ||
        a.MaxPoint.Z < b.MinPoint.Z || a.MinPoint.Z > b.MaxPoint.Z
    );
}

public static bool ExtentsIntersect(Extents2d a, Extents2d b)
{
    // Treat touching as intersecting.
    return !(
        a.MaxPoint.X < b.MinPoint.X || a.MinPoint.X > b.MaxPoint.X ||
        a.MaxPoint.Y < b.MinPoint.Y || a.MinPoint.Y > b.MaxPoint.Y
    );
}

        /// <summary>
        /// Compute intersection regions between two closed polylines.
        /// Caller owns disposal of returned regions.
        /// </summary>
        public static bool TryIntersectRegions(Polyline subject, Polyline clip, out List<Region> regions)
        {
            regions = new List<Region>();

            Region? subjectRegion = null;
            Region? clipRegion = null;

            try
            {
                subjectRegion = CreateRegion(subject);
                clipRegion = CreateRegion(clip);

                if (subjectRegion == null || clipRegion == null)
                    return false;

                subjectRegion.BooleanOperation(BooleanOperationType.BoolIntersect, clipRegion);

                // Note: BooleanOperation modifies the subject region in place.
                regions.Add(subjectRegion);
                subjectRegion = null; // prevent disposal here; caller will dispose

                return regions.Count > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                clipRegion?.Dispose();
                subjectRegion?.Dispose();
            }
        }

        private static Region? CreateRegion(Polyline polyline)
        {
            try
            {
                var curves = new DBObjectCollection();
                curves.Add((Curve)polyline.Clone());

                var regions = Region.CreateFromCurves(curves);
                if (regions == null || regions.Count == 0)
                    return null;

                // Keep first region; dispose extras
                var first = (Region)regions[0];
                for (int i = 1; i < regions.Count; i++)
                {
                    regions[i]?.Dispose();
                }
                return first;
            }
            catch
            {
                return null;
            }
        }

        public static IEnumerable<Point2d> GetSpiralOffsets(Point2d center, double step, int maxPoints)
        {
            // Simple spiral: center, then 8 directions at increasing radius
            yield return center;

            int yielded = 1;
            int ring = 1;

            while (yielded < maxPoints)
            {
                for (int i = 0; i < 8 && yielded < maxPoints; i++)
                {
                    double angle = (Math.PI / 4.0) * i;
                    yield return new Point2d(
                        center.X + Math.Cos(angle) * step * ring,
                        center.Y + Math.Sin(angle) * step * ring
                    );
                    yielded++;
                }
                ring++;
            }
        }

        public static Point2d GetSafeInteriorPoint(Polyline polyline)
        {
            // Try extents center first
            var ext = polyline.GeometricExtents;
            var center = new Point2d(
                (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0
            );

            if (IsPointInsidePolyline(polyline, center))
                return center;

            // Spiral search
            double step = Math.Max(polyline.Length / 200.0, 1.0);
            for (int r = 1; r <= 50; r++)
            {
                for (int i = 0; i < 8; i++)
                {
                    double angle = (Math.PI / 4.0) * i;
                    var p = new Point2d(
                        center.X + Math.Cos(angle) * step * r,
                        center.Y + Math.Sin(angle) * step * r
                    );
                    if (IsPointInsidePolyline(polyline, p))
                        return p;
                }
            }

            return center;
        }

        public static bool IsPointInsidePolyline(Polyline polyline, Point2d point)
{
    if (polyline == null) return false;
    if (!polyline.Closed) return false;

    int n = polyline.NumberOfVertices;
    if (n < 3) return false;

    bool inside = false;

    for (int i = 0, j = n - 1; i < n; j = i++)
    {
        var pi = polyline.GetPoint2dAt(i);
        var pj = polyline.GetPoint2dAt(j);

        // Consider points on the boundary as inside.
        if (IsPointOnSegment(point, pj, pi, 1e-9))
            return true;

        bool intersects = ((pi.Y > point.Y) != (pj.Y > point.Y)) &&
                          (point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y + 0.0) + pi.X);

        if (intersects)
            inside = !inside;
    }

    return inside;
}

private static bool IsPointOnSegment(Point2d p, Point2d a, Point2d b, double tol)
{
    var ab = b - a;
    var ap = p - a;

    double cross = ab.X * ap.Y - ab.Y * ap.X;
    if (Math.Abs(cross) > tol) return false;

    double dot = ap.X * ab.X + ap.Y * ab.Y;
    if (dot < -tol) return false;

    double len2 = ab.X * ab.X + ab.Y * ab.Y;
    if (dot > len2 + tol) return false;

    return true;
}

        // --------------------------------------------------------------------
        // Width measurement utilities (for ROW-style disposition polygons)
        // --------------------------------------------------------------------

        public readonly struct WidthMeasurement
        {
            public WidthMeasurement(double medianWidth, double minWidth, double maxWidth, bool isVariable, bool usedSamples)
            {
                MedianWidth = medianWidth;
                MinWidth = minWidth;
                MaxWidth = maxWidth;
                IsVariable = isVariable;
                UsedSamples = usedSamples;
            }

            public double MedianWidth { get; }
            public double MinWidth { get; }
            public double MaxWidth { get; }
            public bool IsVariable { get; }
            public bool UsedSamples { get; }
        }

        /// <summary>
        /// Measures the "corridor width" of a closed polyline by sampling cross-sections perpendicular to its principal axis.
        /// If sampling fails, falls back to an oriented bounding width.
        /// </summary>
        public static WidthMeasurement MeasureCorridorWidth(
            Polyline corridor,
            int sampleCount,
            double variableAbsTol,
            double variableRelTol)
        {
            if (corridor == null) throw new ArgumentNullException(nameof(corridor));
            if (sampleCount < 1) sampleCount = 7;

            // Determine principal axes
            if (!TryGetPrincipalAxes(corridor, out var origin, out var major, out var minor))
            {
                origin = GetSafeInteriorPoint(corridor);
                major = Vector2d.XAxis;
                minor = Vector2d.YAxis;
            }

            // Compute ranges in principal space (using vertices)
            GetPrincipalRanges(corridor, origin, major, minor, out double minT, out double maxT, out double minS, out double maxS);

            // Center offset along minor axis
            double centerS = (minS + maxS) / 2.0;

            // Big length for intersection lines
            double big = Math.Max(maxT - minT, maxS - minS);
            if (big <= 0) big = 10.0;
            big *= 4.0;

            var widths = new List<double>(sampleCount);
            bool usedSamples = false;

            // Sample positions (skip ends)
            for (int i = 1; i <= sampleCount; i++)
            {
                double frac = (double)i / (sampleCount + 1);
                double t = minT + frac * (maxT - minT);

                var pCenter = origin + (major * t) + (minor * centerS);

                if (TryCrossSectionWidth(corridor, pCenter, minor, big, out var w))
                {
                    widths.Add(w);
                    usedSamples = true;
                }
            }

            if (widths.Count == 0)
            {
                // Fallback: oriented bounding width
                double fallback = maxS - minS;
                if (fallback < 0) fallback = -fallback;
                return new WidthMeasurement(fallback, fallback, fallback, false, false);
            }

            widths.Sort();
            double median = widths[widths.Count / 2];
            double minW = widths[0];
            double maxW = widths[widths.Count - 1];
            double range = maxW - minW;

            bool isVariable = range > Math.Max(variableAbsTol, median * variableRelTol);

            return new WidthMeasurement(median, minW, maxW, isVariable, usedSamples);
        }

        public static double SnapWidthToAcceptable(double measured, IReadOnlyList<double> acceptable, double tolerance)
        {
            if (acceptable == null || acceptable.Count == 0) return measured;
            if (tolerance < 0) tolerance = 0;

            double best = measured;
            double bestDiff = double.MaxValue;

            for (int i = 0; i < acceptable.Count; i++)
            {
                var w = acceptable[i];
                double diff = Math.Abs(measured - w);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = w;
                }
            }

            return bestDiff <= tolerance ? best : measured;
        }

        private static bool TryGetPrincipalAxes(Polyline pl, out Point2d origin, out Vector2d major, out Vector2d minor)
        {
            origin = new Point2d(0, 0);
            major = Vector2d.XAxis;
            minor = Vector2d.YAxis;

            int n = pl.NumberOfVertices;
            if (n < 3) return false;

            double meanX = 0, meanY = 0;
            for (int i = 0; i < n; i++)
            {
                var p = pl.GetPoint2dAt(i);
                meanX += p.X;
                meanY += p.Y;
            }
            meanX /= n;
            meanY /= n;

            origin = new Point2d(meanX, meanY);

            double sxx = 0, syy = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                var p = pl.GetPoint2dAt(i);
                double dx = p.X - meanX;
                double dy = p.Y - meanY;
                sxx += dx * dx;
                syy += dy * dy;
                sxy += dx * dy;
            }

            if (Math.Abs(sxy) < 1e-9 && Math.Abs(sxx - syy) < 1e-9)
                return false;

            double angle = 0.5 * Math.Atan2(2.0 * sxy, sxx - syy);
            major = new Vector2d(Math.Cos(angle), Math.Sin(angle));
            if (major.Length < 1e-9) return false;
            major = major.GetNormal();

            minor = new Vector2d(-major.Y, major.X); // 90 deg
            if (minor.Length < 1e-9) return false;
            minor = minor.GetNormal();

            return true;
        }

        private static void GetPrincipalRanges(
            Polyline pl,
            Point2d origin,
            Vector2d major,
            Vector2d minor,
            out double minT,
            out double maxT,
            out double minS,
            out double maxS)
        {
            minT = double.PositiveInfinity;
            maxT = double.NegativeInfinity;
            minS = double.PositiveInfinity;
            maxS = double.NegativeInfinity;

            int n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                var p = pl.GetPoint2dAt(i);
                var d = p - origin;
                double t = d.DotProduct(major);
                double s = d.DotProduct(minor);

                if (t < minT) minT = t;
                if (t > maxT) maxT = t;
                if (s < minS) minS = s;
                if (s > maxS) maxS = s;
            }

            if (!IsFinite(minT) || !IsFinite(maxT))
            {
                minT = 0;
                maxT = 0;
            }

            if (!IsFinite(minS) || !IsFinite(maxS))
            {
                minS = 0;
                maxS = 0;
            }
        }

        private static bool TryCrossSectionWidth(Polyline corridor, Point2d center, Vector2d direction, double halfLength, out double width)
        {
            width = 0;

            try
            {
                var dir = direction;
                if (dir.Length < 1e-9) return false;
                dir = dir.GetNormal();

                // Build a long line segment through the corridor
                var p1 = new Point3d(center.X - dir.X * halfLength, center.Y - dir.Y * halfLength, corridor.Elevation);
                var p2 = new Point3d(center.X + dir.X * halfLength, center.Y + dir.Y * halfLength, corridor.Elevation);

                using (var line = new Line(p1, p2))
                {
                    var pts = new Point3dCollection();
                    corridor.IntersectWith(line, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);

                    if (pts.Count < 2)
                        return false;

                    // Deduplicate + project along direction (relative to center)
                    var proj = new List<double>(pts.Count);
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var p = pts[i];
                        double s = (p.X - center.X) * dir.X + (p.Y - center.Y) * dir.Y;

                        bool dup = false;
                        for (int j = 0; j < proj.Count; j++)
                        {
                            if (Math.Abs(proj[j] - s) < 1e-6) { dup = true; break; }
                        }
                        if (!dup) proj.Add(s);
                    }

                    if (proj.Count < 2)
                        return false;

                    proj.Sort();
                    width = proj[proj.Count - 1] - proj[0];
                    if (width < 0) width = -width;

                    return width > 1e-6;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsFinite(double v)
        {
            return !(double.IsNaN(v) || double.IsInfinity(v));
        }

    }
}

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\LabelPlacer.cs
/////////////////////////////////////////////////////////////////////

/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace AtsBackgroundBuilder
{
    public sealed class LabelPlacer
    {
        private readonly Database _database;
        private readonly Editor _editor;
        private readonly LayerManager _layerManager;
        private readonly Config _config;
        private readonly Logger _logger;

        public LabelPlacer(Database database, Editor editor, LayerManager layerManager, Config config, Logger logger)
        {
            _database = database;
            _editor = editor;
            _layerManager = layerManager;
            _config = config;
            _logger = logger;
        }

        public PlacementResult PlaceLabels(List<QuarterInfo> quarters, List<DispositionInfo> dispositions, string currentClient)
        {
            var result = new PlacementResult();
            var processedDispositionIds = new HashSet<ObjectId>();

            using (var transaction = _database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(_database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Track extents of labels placed so far so we can avoid overlaps
                var placedLabelExtents = new List<Extents3d>();

                foreach (var quarter in quarters)
                {
                    using (var quarterClone = (Polyline)quarter.Polyline.Clone())
                    {
                        foreach (var disposition in dispositions)
                        {
                            if (!_config.AllowMultiQuarterDispositions && processedDispositionIds.Contains(disposition.ObjectId))
                                continue;

                            if (processedDispositionIds.Contains(disposition.ObjectId))
                                result.MultiQuarterProcessed++;

                            // Quick reject by extents
                            if (!GeometryUtils.ExtentsIntersect(quarter.Polyline.GeometricExtents, disposition.Polyline.GeometricExtents))
                                continue;

                            // Label layer mapping check
                            if (string.IsNullOrWhiteSpace(disposition.TextLayerName))
                            {
                                result.SkippedNoLayerMapping++;
                                continue;
                            }

                            // Ensure label layer exists
                            EnsureLayerInTransaction(transaction, _database, disposition.TextLayerName);

                            using (var dispClone = (Polyline)disposition.Polyline.Clone())
                            {
                                // Leader target point (inside quarter âˆ© disposition whenever possible)
                                var target = GetTargetPoint(quarterClone, dispClone, disposition.SafePoint);

                                // Candidate label points around the target
                                var candidates = GetCandidateLabelPoints(
                                        quarterClone,
                                        dispClone,
                                        target,
                                        disposition.AllowLabelOutsideDisposition,
                                        _config.TextHeight,
                                        _config.MaxOverlapAttempts)
                                    .ToList();

                                if (candidates.Count == 0)
                                {
                                    // If we cannot find any point meeting the inside checks, fall back to target
                                    candidates.Add(target);
                                }

                                bool placed = false;
                                Point2d lastCandidate = candidates[candidates.Count - 1];

                                foreach (var pt in candidates)
                                {
                                    lastCandidate = pt;

                                    var mtext = CreateLabel(pt, disposition.LabelText, disposition.TextLayerName);

                                    modelSpace.AppendEntity(mtext);
                                    transaction.AddNewlyCreatedDBObject(mtext, true);

                                    var bounds = mtext.GeometricExtents;
                                    bool overlaps = placedLabelExtents.Any(b => GeometryUtils.ExtentsIntersect(b, bounds));

                                    if (overlaps)
                                    {
                                        mtext.Erase();
                                        continue;
                                    }

                                    // Success
                                    placedLabelExtents.Add(bounds);
                                    placed = true;
                                    result.LabelsPlaced++;

                                    if (_config.EnableLeaders && disposition.AddLeader)
                                    {
                                        CreateLeader(transaction, modelSpace, target, pt, disposition.TextLayerName);
                                    }

                                    break;
                                }

                                if (!placed && _config.PlaceWhenOverlapFails && candidates.Count > 0)
                                {
                                    // Forced placement at last candidate
                                    var mtext = CreateLabel(lastCandidate, disposition.LabelText, disposition.TextLayerName);
                                    modelSpace.AppendEntity(mtext);
                                    transaction.AddNewlyCreatedDBObject(mtext, true);

                                    placedLabelExtents.Add(mtext.GeometricExtents);
                                    result.LabelsPlaced++;
                                    result.OverlapForced++;

                                    if (_config.EnableLeaders && disposition.AddLeader)
                                    {
                                        CreateLeader(transaction, modelSpace, target, lastCandidate, disposition.TextLayerName);
                                    }

                                    placed = true;
                                }

                                if (!placed)
                                {
                                    // Not counted in legacy result, but keep debug output
                                    _logger.WriteLine($"Could not place label for disposition {disposition.ObjectId}");
                                }

                                processedDispositionIds.Add(disposition.ObjectId);
                            }
                        }
                    }
                }

                transaction.Commit();
            }

            return result;
        }

        private MText CreateLabel(Point2d point, string labelText, string layerName)
        {
            return new MText
            {
                Location = new Point3d(point.X, point.Y, 0),
                TextHeight = _config.TextHeight,
                Contents = labelText,
                Layer = layerName,
                ColorIndex = 7,
                Attachment = AttachmentPoint.BottomLeft
            };
        }

        private void CreateLeader(Transaction tr, BlockTableRecord modelSpace, Point2d target, Point2d labelPoint, string layerName)
        {
            // Circle at target (optional)
            if (_config.LeaderCircleRadius > 1e-6)
            {
                var circle = new Circle(new Point3d(target.X, target.Y, 0), Vector3d.ZAxis, _config.LeaderCircleRadius)
                {
                    Layer = layerName,
                    ColorIndex = 7
                };
                modelSpace.AppendEntity(circle);
                tr.AddNewlyCreatedDBObject(circle, true);
            }

            // Line from circle edge to label point
            var start = new Point3d(target.X, target.Y, 0);
            var end = new Point3d(labelPoint.X, labelPoint.Y, 0);

            var vec = end - start;
            if (vec.Length < 1e-6)
                return;

            if (_config.LeaderCircleRadius > 1e-6)
            {
                var dir = vec.GetNormal();
                start = start + dir * _config.LeaderCircleRadius;
            }

            if ((end - start).Length < 1e-6)
                return;

            var line = new Line(start, end)
            {
                Layer = layerName,
                ColorIndex = 7
            };
            modelSpace.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);
        }
        private static void EnsureLayerInTransaction(Transaction tr, Database db, string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
                return;

            var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (layerTable.Has(layerName))
                return;

            layerTable.UpgradeOpen();

            var layer = new LayerTableRecord
            {
                Name = layerName,
                IsPlottable = true
            };

            layerTable.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
        }


        private static IEnumerable<Point2d> GetCandidateLabelPoints(
            Polyline quarter,
            Polyline disposition,
            Point2d target,
            bool allowOutsideDisposition,
            double step,
            int maxPoints)
        {
            var spiral = GeometryUtils.GetSpiralOffsets(target, step, maxPoints).ToList();

            if (!allowOutsideDisposition)
            {
                foreach (var p in spiral)
                {
                    if (PointInPolyline(quarter, p) && PointInPolyline(disposition, p))
                        yield return p;
                }
                yield break;
            }

            // Prefer points in quarter but outside the disposition first (PDF-style callouts)
            var inside = new List<Point2d>();
            foreach (var p in spiral)
            {
                if (!PointInPolyline(quarter, p))
                    continue;

                if (PointInPolyline(disposition, p))
                    inside.Add(p);
                else
                    yield return p;
            }

            foreach (var p in inside)
                yield return p;
        }

        private Point2d GetTargetPoint(Polyline quarter, Polyline disposition, Point2d fallback)
        {
            // Best: centroid of intersection region(s)
            if (_config.UseRegionIntersection && GeometryUtils.TryIntersectRegions(disposition, quarter, out var regions))
            {
                foreach (var region in regions)
                {
                    using (region)
                    {
                        var c = GetRegionCentroidSafe(region);
                        if (PointInPolyline(quarter, c) && PointInPolyline(disposition, c))
                            return c;
                    }
                }
            }

            // If safe point lies in this quarter
            if (PointInPolyline(quarter, fallback) && PointInPolyline(disposition, fallback))
                return fallback;

            // Try extents overlap center
            var overlap = GetExtentsOverlapCenter(quarter.GeometricExtents, disposition.GeometricExtents);
            if (PointInPolyline(quarter, overlap) && PointInPolyline(disposition, overlap))
                return overlap;

            // Closest point on disposition to a known interior point of quarter
            var qInterior = GeometryUtils.GetSafeInteriorPoint(quarter);
            try
            {
                var closest = disposition.GetClosestPointTo(new Point3d(qInterior.X, qInterior.Y, 0), false);
                var cp = new Point2d(closest.X, closest.Y);
                if (PointInPolyline(quarter, cp))
                    return cp;
            }
            catch { }

            return fallback;
        }

        private static Point2d GetRegionCentroidSafe(Region region)
        {
            try
            {
                var centroid = Point3d.Origin;
                var normal = Vector3d.ZAxis;
                var axes = Vector3d.XAxis;
                region.AreaProperties(ref centroid, ref normal, ref axes);

                if (IsFinite(centroid.X) && IsFinite(centroid.Y))
                    return new Point2d(centroid.X, centroid.Y);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // ignore
            }
            catch
            {
                // ignore
            }

            try
            {
                var ext = region.GeometricExtents;
                return new Point2d(
                    (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                    (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0
                );
            }
            catch
            {
                return new Point2d(0, 0);
            }
        }

        private static Point2d GetExtentsOverlapCenter(Extents3d a, Extents3d b)
        {
            double minX = Math.Max(a.MinPoint.X, b.MinPoint.X);
            double maxX = Math.Min(a.MaxPoint.X, b.MaxPoint.X);

            double minY = Math.Max(a.MinPoint.Y, b.MinPoint.Y);
            double maxY = Math.Min(a.MaxPoint.Y, b.MaxPoint.Y);

            return new Point2d((minX + maxX) / 2.0, (minY + maxY) / 2.0);
        }


        private static bool IsFinite(double v)
        {
            return !(double.IsNaN(v) || double.IsInfinity(v));
        }

        private static bool PointInPolyline(Polyline pl, Point2d pt)
        {
            bool inside = false;
            int n = pl.NumberOfVertices;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var pi = pl.GetPoint2dAt(i);
                var pj = pl.GetPoint2dAt(j);

                bool intersect = ((pi.Y > pt.Y) != (pj.Y > pt.Y)) &&
                                 (pt.X < (pj.X - pi.X) * (pt.Y - pi.Y) /
                                     ((pj.Y - pi.Y) == 0 ? 1e-12 : (pj.Y - pi.Y)) + pi.X);

                if (intersect) inside = !inside;
            }
            return inside;
        }
    }

    public sealed class QuarterInfo
    {
        public QuarterInfo(Polyline polyline)
        {
            Polyline = polyline;
            Bounds = polyline.GeometricExtents;
        }

        public Polyline Polyline { get; }
        public Extents3d Bounds { get; }
    }

    public sealed class DispositionInfo
    {
        public DispositionInfo(ObjectId objectId, Polyline polyline, string labelText, string layerName, string textLayerName, Point2d safePoint)
        {
            ObjectId = objectId;
            Polyline = polyline;
            Bounds = polyline.GeometricExtents;
            LabelText = labelText;
            LayerName = layerName;
            TextLayerName = textLayerName;
            SafePoint = safePoint;
        }

        public ObjectId ObjectId { get; }
        public Polyline Polyline { get; }
        public Extents3d Bounds { get; }

        public string LabelText { get; }
        public string LayerName { get; }
        public string TextLayerName { get; }
        public Point2d SafePoint { get; }

        // For width-required purposes, allow label to be placed in the quarter (not necessarily in the disposition)
        public bool AllowLabelOutsideDisposition { get; set; }

        // Draw leader entities (circle + line) from target to label
        public bool AddLeader { get; set; }
    }

    public sealed class PlacementResult
    {
        public int LabelsPlaced { get; set; }
        public int SkippedNoLayerMapping { get; set; }
        public int OverlapForced { get; set; }
        public int MultiQuarterProcessed { get; set; }
    }
}

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\LayerManager.cs
/////////////////////////////////////////////////////////////////////

/////////////////////////////////////////////////////////////////////

using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Colors;

namespace AtsBackgroundBuilder
{
    public sealed class LayerManager
    {
        private readonly Database _database;

        public LayerManager(Database database)
        {
            _database = database;
        }

        public ObjectId EnsureLayer(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
            {
                return _database.Clayer;
            }

            using (var transaction = _database.TransactionManager.StartTransaction())
            {
                var table = (LayerTable)transaction.GetObject(_database.LayerTableId, OpenMode.ForRead);
                if (table.Has(layerName))
                {
                    return table[layerName];
                }

                table.UpgradeOpen();
                var record = new LayerTableRecord
                {
                    Name = layerName,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, 7)
                };
                var id = table.Add(record);
                transaction.AddNewlyCreatedDBObject(record, true);
                transaction.Commit();
                return id;
            }
        }

        public static string NormalizeSuffix(string? suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix))
            {
                return string.Empty;
            }

            var normalized = suffix.Trim();
            if (normalized.StartsWith("-", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(1);
            }

            return normalized.Trim();
        }
    }
}

/////////////////////////////////////////////////////////////////////

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\OdHelpers.cs
/////////////////////////////////////////////////////////////////////

/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Gis.Map;
using Autodesk.Gis.Map.ObjectData;
using Autodesk.Gis.Map.Utilities;

namespace AtsBackgroundBuilder
{
    public static class OdHelpers
    {
        public static Dictionary<string, string>? ReadObjectData(ObjectId objectId, Logger logger)
        {
            try
            {
                var tables = HostMapApplicationServices.Application.ActiveProject.ODTables;
                var tableNames = tables.GetTableNames();
                if (tableNames == null || tableNames.Count == 0)
                {
                    return null;
                }

                foreach (var tableName in tableNames)
                {
                    Autodesk.Gis.Map.ObjectData.Table table = tables[tableName];
                    var records = GetRecordsForObject(table, objectId, logger);
                    if (records == null || records.Count == 0)
                    {
                        continue;
                    }

                    var record = records[0];
                    var fieldDefinitions = table.FieldDefinitions;
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < record.Count; i++)
                    {
                        var field = record[i];
                        var fieldName = fieldDefinitions[i].Name;
                        dict[fieldName] = MapValueToString(field);
                    }

                    return dict;
                }
            }
            catch (Exception ex)
            {
                logger.WriteLine("OD read failed: " + ex.Message);
            }

            return null;
        }

        private static Records? GetRecordsForObject(Autodesk.Gis.Map.ObjectData.Table table, ObjectId objectId, Logger logger)
        {
            try
            {
                return table.GetObjectTableRecords(0, objectId, Autodesk.Gis.Map.Constants.OpenMode.OpenForRead, false);
            }
            catch (Exception ex)
            {
                logger.WriteLine("OD GetObjectRecords failed: " + ex.Message);
                return null;
            }
        }

        private static string MapValueToString(MapValue value)
        {
            switch (value.Type)
            {
                case Autodesk.Gis.Map.Constants.DataType.Integer:
                    return value.Int32Value.ToString();
                case Autodesk.Gis.Map.Constants.DataType.Real:
                    return value.DoubleValue.ToString();
                case Autodesk.Gis.Map.Constants.DataType.Character:
                    return value.StrValue ?? string.Empty;
                default:
                    return value.ToString();
            }
        }
    }
}

/////////////////////////////////////////////////////////////////////

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\Plugin.cs
/////////////////////////////////////////////////////////////////////

/////////////////////////////////////////////////////////////////////

using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public class Plugin : IExtensionApplication
    {
        public void Initialize()
        {
        }

        public void Terminate()
        {
        }

        [CommandMethod("ATSBUILD")]
        public void AtsBuild()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var editor = doc.Editor;
            var database = doc.Database;

            var logger = new Logger();
            var dllFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory;
            logger.Initialize(Path.Combine(dllFolder, "AtsBackgroundBuilder.log"));

            var configPath = Path.Combine(dllFolder, "Config.json");
            var config = Config.Load(configPath, logger);

            var companyLookupPath = ResolveLookupPath(config.LookupFolder, config.CompanyLookupFile, dllFolder, "CompanyLookup.xlsx");
            var purposeLookupPath = ResolveLookupPath(config.LookupFolder, config.PurposeLookupFile, dllFolder, "PurposeLookup.xlsx");

            var companyLookup = ExcelLookup.Load(companyLookupPath, logger);
            var purposeLookup = ExcelLookup.Load(purposeLookupPath, logger);

            var sectionDrawResult = TryPromptAndBuildSections(editor, database, config, logger);
            var quarterPolylines = sectionDrawResult.QuarterPolylineIds;
            if (quarterPolylines.Count == 0)
            {
                editor.WriteMessage("\nNo quarter polylines selected.");
                return;
            }

            var dispositionPolylines = new List<ObjectId>();
            var importSummary = ShapefileImporter.ImportShapefiles(
                database,
                editor,
                logger,
                config,
                sectionDrawResult.SectionPolylineIds,
                dispositionPolylines);
            if (dispositionPolylines.Count == 0)
            {
                editor.WriteMessage("\nNo disposition polylines imported from shapefiles.");
                return;
            }

            var currentClient = PromptForClient(editor, companyLookup);
            if (string.IsNullOrWhiteSpace(currentClient))
            {
                editor.WriteMessage("\nCurrent client is required.");
                return;
            }

            var textHeight = PromptForDouble(editor, "Text height", config.TextHeight, 1.0, 100.0);
            var maxAttempts = PromptForInt(editor, "Max overlap attempts", config.MaxOverlapAttempts, 1, 200);
            config.TextHeight = textHeight;
            config.MaxOverlapAttempts = maxAttempts;

            var layerManager = new LayerManager(database);
            var dispositions = new List<DispositionInfo>();
            var result = new SummaryResult();

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                foreach (var id in dispositionPolylines)
                {
                    var polyline = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (polyline == null || !polyline.Closed)
                    {
                        result.SkippedNotClosed++;
                        continue;
                    }

                    // Work with an in-memory clone after the transaction ends.
                    var clone = (Polyline)polyline.Clone();

                    // Default output layers (can be overridden by client/foreign logic later).
                    string lineLayer = polyline.Layer;
                    string textLayer = polyline.Layer;

                    result.TotalDispositions++;
                    var od = OdHelpers.ReadObjectData(id, logger);
                    if (od == null)
                    {
                        result.SkippedNoOd++;
                        continue;
                    }

                    var dispNum = od.TryGetValue("DISP_NUM", out var dispRaw) ? dispRaw : string.Empty;
                    var company = od.TryGetValue("COMPANY", out var companyRaw) ? companyRaw : string.Empty;
                    var purpose = od.TryGetValue("PURPCD", out var purposeRaw) ? purposeRaw : string.Empty;

                    var mappedCompany = MapValue(companyLookup, company, company);
                    var mappedPurpose = MapValue(purposeLookup, purpose, purpose);
                    var purposeExtra = purposeLookup.Lookup(purpose)?.Extra ?? string.Empty;

                    var dispNumFormatted = FormatDispNum(dispNum);
                    var safePoint = GeometryUtils.GetSafeInteriorPoint(clone);

                    var requiresWidth = PurposeRequiresWidth(purpose, config);
                    string labelText;

                    if (requiresWidth)
                    {
                        var measurement = GeometryUtils.MeasureCorridorWidth(
                            clone,
                            config.WidthSampleCount,
                            config.VariableWidthAbsTolerance,
                            config.VariableWidthRelTolerance);

                        if (measurement.IsVariable)
                        {
                            labelText = mappedCompany + "\\P" + "Variable Width" + "\\P" + ToTitleCaseWords(purpose) + "\\P" + dispNumFormatted;
                        }
                        else
                        {
                            var snapped = GeometryUtils.SnapWidthToAcceptable(measurement.MedianWidth, config.AcceptableRowWidths, config.WidthSnapTolerance);
                            var widthText = snapped.ToString("0.00", CultureInfo.InvariantCulture);
                            labelText = mappedCompany + "\\P" + widthText + " " + mappedPurpose + "\\P" + dispNumFormatted;
                        }
                    }
                    else
                    {
                        labelText = mappedCompany + "\\P" + mappedPurpose + "\\P" + dispNumFormatted;
                    }

                    var info = new DispositionInfo(id, clone, labelText, lineLayer, textLayer, safePoint)
                    {
                        AllowLabelOutsideDisposition = requiresWidth && config.AllowOutsideDispositionForWidthPurposes,
                        AddLeader = requiresWidth
                    };

                    dispositions.Add(info);
                }

                transaction.Commit();
            }

            var quarters = new List<QuarterInfo>();
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                foreach (var id in quarterPolylines)
                {
                    var polyline = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (polyline == null || !polyline.Closed)
                    {
                        continue;
                    }

                    quarters.Add(new QuarterInfo((Polyline)polyline.Clone()));
                }
                transaction.Commit();
            }

            var placer = new LabelPlacer(database, editor, layerManager, config, logger);
            var placement = placer.PlaceLabels(quarters, dispositions, currentClient);

            result.LabelsPlaced = placement.LabelsPlaced;
            result.SkippedNoLayerMapping += placement.SkippedNoLayerMapping;
            result.OverlapForced = placement.OverlapForced;
            result.MultiQuarterProcessed = placement.MultiQuarterProcessed;
            result.ImportedDispositions = importSummary.ImportedDispositions;
            result.DedupedDispositions = importSummary.DedupedDispositions;
            result.FilteredDispositions = importSummary.FilteredDispositions;
            result.ImportFailures = importSummary.ImportFailures;

            editor.WriteMessage("\nATSBUILD complete.");
            editor.WriteMessage("\nTotal dispositions: " + result.TotalDispositions);
            editor.WriteMessage("\nLabels placed: " + result.LabelsPlaced);
            editor.WriteMessage("\nSkipped (no OD): " + result.SkippedNoOd);
            editor.WriteMessage("\nSkipped (not closed): " + result.SkippedNotClosed);
            editor.WriteMessage("\nNo layer mapping: " + result.SkippedNoLayerMapping);
            editor.WriteMessage("\nOverlap forced: " + result.OverlapForced);
            editor.WriteMessage("\nMulti-quarter processed: " + result.MultiQuarterProcessed);
            editor.WriteMessage("\nImported dispositions: " + result.ImportedDispositions);
            editor.WriteMessage("\nFiltered out: " + result.FilteredDispositions);
            editor.WriteMessage("\nDeduped: " + result.DedupedDispositions);
            editor.WriteMessage("\nImport failures: " + result.ImportFailures);

            logger.WriteLine("ATSBUILD summary");
            logger.WriteLine("Total dispositions: " + result.TotalDispositions);
            logger.WriteLine("Labels placed: " + result.LabelsPlaced);
            logger.WriteLine("Skipped (no OD): " + result.SkippedNoOd);
            logger.WriteLine("Skipped (not closed): " + result.SkippedNotClosed);
            logger.WriteLine("No layer mapping: " + result.SkippedNoLayerMapping);
            logger.WriteLine("Overlap forced: " + result.OverlapForced);
            logger.WriteLine("Multi-quarter processed: " + result.MultiQuarterProcessed);
            logger.WriteLine("Imported dispositions: " + result.ImportedDispositions);
            logger.WriteLine("Filtered out: " + result.FilteredDispositions);
            logger.WriteLine("Deduped: " + result.DedupedDispositions);
            logger.WriteLine("Import failures: " + result.ImportFailures);
            logger.Dispose();
        }

        private static SectionDrawResult TryPromptAndBuildSections(Editor editor, Database database, Config config, Logger logger)
        {
            if (config.UseSectionIndex)
            {
                var requests = PromptForSectionRequests(editor);
                if (requests.Count > 0)
                {
                    var result = DrawSectionsFromRequests(editor, database, requests, config, logger);
                    if (result.QuarterPolylineIds.Count == 0)
                    {
                        var searchFolders = BuildSectionIndexSearchFolders(config);
                        var zones = new HashSet<int>();
                        foreach (var request in requests)
                        {
                            zones.Add(request.Key.Zone);
                        }

                        var zoneList = string.Join(", ", zones);
                        editor.WriteMessage(
                            $"\nNo section outlines found in index. " +
                            $"Verify the section index files for zone(s) {zoneList} exist in {string.Join("; ", searchFolders)} " +
                            "(Master_Sections.index_Z<zone>.jsonl/.csv or Master_Sections.index.jsonl/.csv). " +
                            "See AtsBackgroundBuilder.log for details.");
                    }

                    return result;
                }
            }

            editor.WriteMessage("\nSection input required.");
            return new SectionDrawResult(new List<ObjectId>(), new List<ObjectId>(), false);
        }

        private static List<SectionRequest> PromptForSectionRequests(Editor editor)
        {
            var requests = new List<SectionRequest>();
            var zone = PromptForInt(editor, "Enter zone", 11, 1, 60);

            var addAnother = true;
            while (addAnother)
            {
                var quarter = PromptForQuarter(editor);
                if (quarter == QuarterSelection.None)
                {
                    break;
                }

                if (!TryPromptString(editor, "Enter section", out var section) ||
                    !TryPromptString(editor, "Enter township", out var township) ||
                    !TryPromptString(editor, "Enter range", out var range) ||
                    !TryPromptString(editor, "Enter meridian", out var meridian))
                {
                    break;
                }

                requests.Add(new SectionRequest(quarter, new SectionKey(zone, section, township, range, meridian)));

                var moreOptions = new PromptKeywordOptions("Add another section?")
                {
                    AllowNone = true
                };
                moreOptions.Keywords.Add("Yes");
                moreOptions.Keywords.Add("No");
                moreOptions.Keywords.Default = "No";

                var moreResult = editor.GetKeywords(moreOptions);
                addAnother = moreResult.Status == PromptStatus.OK &&
                             string.Equals(moreResult.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
            }

            return requests;
        }

        private static string PromptForClient(Editor editor, ExcelLookup lookup)
        {
            var values = lookup.Values;
            if (values.Count > 0 && values.Count <= 20)
            {
                var options = new PromptKeywordOptions("Select current client")
                {
                    AllowNone = false
                };

                foreach (var value in values)
                {
                    options.Keywords.Add(value);
                }

                var result = editor.GetKeywords(options);
                if (result.Status == PromptStatus.OK)
                {
                    return result.StringResult;
                }
            }

            if (values.Count > 0)
            {
                editor.WriteMessage("\nAvailable clients: " + string.Join(", ", values));
            }

            var prompt = new PromptStringOptions("Enter current client name: ")
            {
                AllowSpaces = true
            };
            var input = editor.GetString(prompt);
            return input.Status == PromptStatus.OK ? input.StringResult : string.Empty;
        }

        private static double PromptForDouble(Editor editor, string message, double defaultValue, double min, double max)
        {
            var options = new PromptDoubleOptions(message + " [" + defaultValue + "]: ")
            {
                DefaultValue = defaultValue,
                AllowNone = true
            };

            var result = editor.GetDouble(options);
            if (result.Status != PromptStatus.OK)
            {
                return defaultValue;
            }

            var value = result.Value;
            if (value < min || value > max)
            {
                editor.WriteMessage($"\nValue out of range. Using nearest allowed value ({min} - {max}).");
                return Math.Min(Math.Max(value, min), max);
            }

            return value;
        }

        private static int PromptForInt(Editor editor, string message, int defaultValue, int min, int max)
        {
            var options = new PromptIntegerOptions(message + " [" + defaultValue + "]: ")
            {
                DefaultValue = defaultValue,
                AllowNone = true,
                LowerLimit = min,
                UpperLimit = max
            };

            var result = editor.GetInteger(options);
            return result.Status == PromptStatus.OK ? result.Value : defaultValue;
        }

        private static string MapValue(ExcelLookup lookup, string key, string fallback)
        {
            var entry = lookup.Lookup(key);
            return entry == null || string.IsNullOrWhiteSpace(entry.Value) ? fallback : entry.Value;
        }

        private static string FormatDispNum(string dispNum)
        {
            var regex = new Regex("^([A-Z]{3})(\\d+)");
            var match = regex.Match(dispNum ?? string.Empty);
            if (!match.Success)
            {
                return dispNum;
            }

            return match.Groups[1].Value + " " + match.Groups[2].Value;
        }

        private static string ResolveLookupPath(string lookupFolder, string configuredFileName, string dllFolder, string defaultFileName)
        {
            // If the configured name is an absolute path, use it directly.
            if (!string.IsNullOrWhiteSpace(configuredFileName) && Path.IsPathRooted(configuredFileName) && File.Exists(configuredFileName))
                return configuredFileName;

            var fileName = string.IsNullOrWhiteSpace(configuredFileName) ? defaultFileName : configuredFileName;

            var candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(lookupFolder))
                candidates.Add(Path.Combine(lookupFolder, fileName));

            if (!string.IsNullOrWhiteSpace(dllFolder))
                candidates.Add(Path.Combine(dllFolder, fileName));

            // Fall back to current working directory if needed
            candidates.Add(Path.Combine(Environment.CurrentDirectory, fileName));

            foreach (var p in candidates)
            {
                try
                {
                    if (File.Exists(p))
                        return p;
                }
                catch
                {
                    // ignore
                }
            }

            // If nothing exists, return the first candidate so the logger prints a useful "not found" path.
            return candidates.FirstOrDefault() ?? Path.Combine(dllFolder ?? "", fileName);
        }

        private static bool PurposeRequiresWidth(string purpose, Config config)
        {
            if (string.IsNullOrWhiteSpace(purpose))
                return false;

            var norm = NormalizePurposeCode(purpose);
            var list = config.WidthRequiredPurposeCodes ?? Array.Empty<string>();

            foreach (var item in list)
            {
                if (NormalizePurposeCode(item) == norm)
                    return true;
            }

            return false;
        }

        private static string NormalizePurposeCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            // Trim, uppercase, and collapse all whitespace to single spaces
            var s = value.Trim().ToUpperInvariant();
            var chars = new List<char>(s.Length);
            bool prevSpace = false;

            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!prevSpace)
                    {
                        chars.Add(' ');
                        prevSpace = true;
                    }
                }
                else
                {
                    chars.Add(ch);
                    prevSpace = false;
                }
            }

            return new string(chars.ToArray());
        }

        private static string ToTitleCaseWords(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            // TitleCase, then common-word cleanup ("and" stays lower-case)
            var lower = NormalizePurposeCode(value).ToLowerInvariant();
            var title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lower);

            title = title.Replace(" And ", " and ");
            title = title.Replace(" Of ", " of ");
            title = title.Replace(" The ", " the ");
            return title;
        }

        private static IEnumerable<Polyline> GenerateQuarters(Polyline section)
        {
            var extents = section.GeometricExtents;
            var minX = extents.MinPoint.X;
            var minY = extents.MinPoint.Y;
            var maxX = extents.MaxPoint.X;
            var maxY = extents.MaxPoint.Y;
            var midX = (minX + maxX) / 2.0;
            var midY = (minY + maxY) / 2.0;

            yield return CreateRectangle(minX, minY, midX, midY);
            yield return CreateRectangle(midX, minY, maxX, midY);
            yield return CreateRectangle(minX, midY, midX, maxY);
            yield return CreateRectangle(midX, midY, maxX, maxY);
        }

        private static Polyline CreateRectangle(double minX, double minY, double maxX, double maxY)
        {
            var polyline = new Polyline(4)
            {
                Closed = true
            };

            polyline.AddVertexAt(0, new Point2d(minX, minY), 0, 0, 0);
            polyline.AddVertexAt(1, new Point2d(maxX, minY), 0, 0, 0);
            polyline.AddVertexAt(2, new Point2d(maxX, maxY), 0, 0, 0);
            polyline.AddVertexAt(3, new Point2d(minX, maxY), 0, 0, 0);

            return polyline;
        }

        private static SectionDrawResult DrawSectionsFromRequests(Editor editor, Database database, List<SectionRequest> requests, Config config, Logger logger)
        {
            var quarterIds = new List<ObjectId>();
            var sectionIds = new List<ObjectId>();
            var createdSections = new Dictionary<string, SectionBuildResult>(StringComparer.OrdinalIgnoreCase);
            var searchFolders = BuildSectionIndexSearchFolders(config);

            foreach (var request in requests)
            {
                var keyId = BuildSectionKeyId(request.Key);
                if (!createdSections.TryGetValue(keyId, out var buildResult))
                {
                    if (!TryLoadSectionOutline(searchFolders, request.Key, logger, out var outline))
                    {
                        continue;
                    }

                    buildResult = DrawSectionFromIndex(editor, database, outline, request.Key);
                    createdSections[keyId] = buildResult;
                    sectionIds.Add(buildResult.SectionPolylineId);
                }

                if (request.Quarter == QuarterSelection.All)
                {
                    foreach (var quarterId in buildResult.QuarterPolylineIds.Values)
                    {
                        quarterIds.Add(quarterId);
                    }
                }
                else if (buildResult.QuarterPolylineIds.TryGetValue(request.Quarter, out var quarterId))
                {
                    quarterIds.Add(quarterId);
                }
            }

            return new SectionDrawResult(quarterIds, sectionIds, true);
        }

        private static SectionBuildResult DrawSectionFromIndex(Editor editor, Database database, SectionOutline outline, SectionKey key)
        {
            var quarterIds = new Dictionary<QuarterSelection, ObjectId>();
            ObjectId sectionId;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                EnsureLayer(database, transaction, "L-USEC");
                EnsureLayer(database, transaction, "L-QSEC");

                var sectionPolyline = new Polyline(outline.Vertices.Count)
                {
                    Closed = outline.Closed,
                    Layer = "L-USEC",
                    ColorIndex = 256
                };

                for (var i = 0; i < outline.Vertices.Count; i++)
                {
                    var vertex = outline.Vertices[i];
                    sectionPolyline.AddVertexAt(i, vertex, 0, 0, 0);
                }

                sectionId = modelSpace.AppendEntity(sectionPolyline);
                transaction.AddNewlyCreatedDBObject(sectionPolyline, true);

                if (TryBuildQuarterMap(sectionPolyline, out var quarterMap, out var anchors))
                {
                    foreach (var quarter in quarterMap)
                    {
                        quarter.Value.Layer = "L-QSEC";
                        quarter.Value.ColorIndex = 256;
                        var quarterId = modelSpace.AppendEntity(quarter.Value);
                        transaction.AddNewlyCreatedDBObject(quarter.Value, true);
                        quarterIds[quarter.Key] = quarterId;
                    }

                    var qv = new Line(new Point3d(anchors.Top.X, anchors.Top.Y, 0), new Point3d(anchors.Bottom.X, anchors.Bottom.Y, 0))
                    {
                        Layer = "L-QSEC",
                        ColorIndex = 256
                    };
                    var qh = new Line(new Point3d(anchors.Left.X, anchors.Left.Y, 0), new Point3d(anchors.Right.X, anchors.Right.Y, 0))
                    {
                        Layer = "L-QSEC",
                        ColorIndex = 256
                    };
                    modelSpace.AppendEntity(qv);
                    transaction.AddNewlyCreatedDBObject(qv, true);
                    modelSpace.AppendEntity(qh);
                    transaction.AddNewlyCreatedDBObject(qh, true);

                    var center = new Point3d(
                        0.5 * (anchors.Top.X + anchors.Bottom.X),
                        0.5 * (anchors.Left.Y + anchors.Right.Y),
                        0);
                    InsertSectionLabelBlock(modelSpace, blockTable, transaction, editor, center, key);
                }

                transaction.Commit();
            }

            return new SectionBuildResult(sectionId, quarterIds);
        }

        private static QuarterSelection PromptForQuarter(Editor editor)
        {
            var options = new PromptKeywordOptions("Select quarter")
            {
                AllowNone = false
            };
            options.Keywords.Add("NW");
            options.Keywords.Add("NE");
            options.Keywords.Add("SW");
            options.Keywords.Add("SE");
            options.Keywords.Add("ALL");

            var result = editor.GetKeywords(options);
            if (result.Status != PromptStatus.OK)
            {
                return QuarterSelection.None;
            }

            switch (result.StringResult.ToUpperInvariant())
            {
                case "NW":
                    return QuarterSelection.NorthWest;
                case "NE":
                    return QuarterSelection.NorthEast;
                case "SW":
                    return QuarterSelection.SouthWest;
                case "SE":
                    return QuarterSelection.SouthEast;
                case "ALL":
                    return QuarterSelection.All;
                default:
                    return QuarterSelection.None;
            }
        }

        private static bool TryPromptString(Editor editor, string message, out string value)
        {
            value = string.Empty;
            var options = new PromptStringOptions(message + ": ")
            {
                AllowSpaces = true
            };

            var result = editor.GetString(options);
            if (result.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(result.StringResult))
            {
                return false;
            }

            value = result.StringResult;
            return true;
        }

        private static bool TryPromptInt(Editor editor, string message, out int value)
        {
            value = 0;
            var options = new PromptIntegerOptions(message + ": ")
            {
                AllowNone = false
            };

            var result = editor.GetInteger(options);
            if (result.Status != PromptStatus.OK)
            {
                return false;
            }

            value = result.Value;
            return true;
        }

        private static bool TryBuildQuarterMap(Polyline section, out Dictionary<QuarterSelection, Polyline> quarterMap, out QuarterAnchors anchors)
        {
            quarterMap = new Dictionary<QuarterSelection, Polyline>();

            if (!TryGetQuarterAnchors(section, out anchors))
            {
                anchors = GetFallbackAnchors(section);
            }

            var outline = GetPolylinePoints(section);
            if (outline.Count < 3)
            {
                return false;
            }

            if (TryBuildQuarterPolylines(outline, anchors, out quarterMap))
            {
                return true;
            }

            quarterMap = GenerateQuarterMapFromExtents(section);
            return quarterMap.Count > 0;
        }

        private static bool TryBuildQuarterPolylines(
            IReadOnlyList<Point2d> outline,
            QuarterAnchors anchors,
            out Dictionary<QuarterSelection, Polyline> quarterMap)
        {
            quarterMap = new Dictionary<QuarterSelection, Polyline>();

            var northLineStart = anchors.Left;
            var northLineEnd = anchors.Right;
            var westLineStart = anchors.Top;
            var westLineEnd = anchors.Bottom;

            var northSign = GetSideSign(northLineStart, northLineEnd, anchors.Top);
            if (northSign == 0)
            {
                northSign = GetSideSign(northLineStart, northLineEnd, anchors.Bottom);
            }

            var westSign = GetSideSign(westLineStart, westLineEnd, anchors.Left);
            if (westSign == 0)
            {
                westSign = GetSideSign(westLineStart, westLineEnd, anchors.Right);
            }

            if (northSign == 0 || westSign == 0)
            {
                return false;
            }

            var north = ClipPolygon(outline, northLineStart, northLineEnd, northSign);
            var south = ClipPolygon(outline, northLineStart, northLineEnd, -northSign);
            var northwest = ClipPolygon(north, westLineStart, westLineEnd, westSign);
            var northeast = ClipPolygon(north, westLineStart, westLineEnd, -westSign);
            var southwest = ClipPolygon(south, westLineStart, westLineEnd, westSign);
            var southeast = ClipPolygon(south, westLineStart, westLineEnd, -westSign);

            if (!TryAddQuarter(quarterMap, QuarterSelection.NorthWest, northwest) ||
                !TryAddQuarter(quarterMap, QuarterSelection.NorthEast, northeast) ||
                !TryAddQuarter(quarterMap, QuarterSelection.SouthWest, southwest) ||
                !TryAddQuarter(quarterMap, QuarterSelection.SouthEast, southeast))
            {
                return false;
            }

            return true;
        }

        private static bool TryAddQuarter(Dictionary<QuarterSelection, Polyline> quarterMap, QuarterSelection selection, List<Point2d> points)
        {
            var polyline = BuildPolylineFromPoints(points);
            if (polyline == null)
            {
                return false;
            }

            quarterMap[selection] = polyline;
            return true;
        }

        private static Dictionary<QuarterSelection, Polyline> GenerateQuarterMapFromExtents(Polyline section)
        {
            var extents = section.GeometricExtents;
            var minX = extents.MinPoint.X;
            var minY = extents.MinPoint.Y;
            var maxX = extents.MaxPoint.X;
            var maxY = extents.MaxPoint.Y;
            var midX = (minX + maxX) / 2.0;
            var midY = (minY + maxY) / 2.0;

            return new Dictionary<QuarterSelection, Polyline>
            {
                { QuarterSelection.SouthWest, CreateRectangle(minX, minY, midX, midY) },
                { QuarterSelection.SouthEast, CreateRectangle(midX, minY, maxX, midY) },
                { QuarterSelection.NorthWest, CreateRectangle(minX, midY, midX, maxY) },
                { QuarterSelection.NorthEast, CreateRectangle(midX, midY, maxX, maxY) }
            };
        }

        private static QuarterAnchors GetFallbackAnchors(Polyline section)
        {
            var extents = section.GeometricExtents;
            var minX = extents.MinPoint.X;
            var minY = extents.MinPoint.Y;
            var maxX = extents.MaxPoint.X;
            var maxY = extents.MaxPoint.Y;
            var midX = (minX + maxX) / 2.0;
            var midY = (minY + maxY) / 2.0;

            return new QuarterAnchors(
                new Point2d(midX, maxY),
                new Point2d(midX, minY),
                new Point2d(minX, midY),
                new Point2d(maxX, midY));
        }

        private static List<Point2d> GetPolylinePoints(Polyline section)
        {
            var points = new List<Point2d>(section.NumberOfVertices);
            for (var i = 0; i < section.NumberOfVertices; i++)
            {
                points.Add(section.GetPoint2dAt(i));
            }

            return points;
        }

        private static double GetSideSign(Point2d lineStart, Point2d lineEnd, Point2d point)
        {
            var lineDir = lineEnd - lineStart;
            var toPoint = point - lineStart;
            var cross = lineDir.X * toPoint.Y - lineDir.Y * toPoint.X;
            if (Math.Abs(cross) < 1e-9)
            {
                return 0;
            }

            return Math.Sign(cross);
        }

        private static List<Point2d> ClipPolygon(IReadOnlyList<Point2d> polygon, Point2d lineStart, Point2d lineEnd, double keepSign)
        {
            var output = new List<Point2d>();
            if (polygon.Count == 0)
            {
                return output;
            }

            var prev = polygon[polygon.Count - 1];
            var prevSide = SignedSide(lineStart, lineEnd, prev);
            var prevInside = IsInside(prevSide, keepSign);

            foreach (var current in polygon)
            {
                var currentSide = SignedSide(lineStart, lineEnd, current);
                var currentInside = IsInside(currentSide, keepSign);

                if (currentInside)
                {
                    if (!prevInside)
                    {
                        if (TryIntersectSegmentWithLine(prev, current, lineStart, lineEnd, out var intersection))
                        {
                            output.Add(intersection);
                        }
                    }

                    output.Add(current);
                }
                else if (prevInside)
                {
                    if (TryIntersectSegmentWithLine(prev, current, lineStart, lineEnd, out var intersection))
                    {
                        output.Add(intersection);
                    }
                }

                prev = current;
                prevSide = currentSide;
                prevInside = currentInside;
            }

            return output;
        }

        private static bool IsInside(double side, double keepSign)
        {
            if (keepSign > 0)
            {
                return side >= -1e-9;
            }

            return side <= 1e-9;
        }

        private static double SignedSide(Point2d lineStart, Point2d lineEnd, Point2d point)
        {
            var lineDir = lineEnd - lineStart;
            var toPoint = point - lineStart;
            return lineDir.X * toPoint.Y - lineDir.Y * toPoint.X;
        }

        private static bool TryIntersectSegmentWithLine(
            Point2d segmentStart,
            Point2d segmentEnd,
            Point2d lineStart,
            Point2d lineEnd,
            out Point2d intersection)
        {
            intersection = default;
            var p = segmentStart;
            var r = segmentEnd - segmentStart;
            var q = lineStart;
            var s = lineEnd - lineStart;

            var cross = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(cross) < 1e-9)
            {
                return false;
            }

            var qmp = q - p;
            var t = (qmp.X * s.Y - qmp.Y * s.X) / cross;
            intersection = new Point2d(p.X + t * r.X, p.Y + t * r.Y);
            return true;
        }

        private static Polyline? BuildPolylineFromPoints(List<Point2d> points)
        {
            if (points.Count < 3)
            {
                return null;
            }

            var cleaned = new List<Point2d>();
            foreach (var point in points)
            {
                if (cleaned.Count == 0 || point.GetDistanceTo(cleaned[cleaned.Count - 1]) > 1e-6)
                {
                    cleaned.Add(point);
                }
            }

            if (cleaned.Count < 3)
            {
                return null;
            }

            if (cleaned[0].GetDistanceTo(cleaned[cleaned.Count - 1]) < 1e-6)
            {
                cleaned.RemoveAt(cleaned.Count - 1);
            }

            var polyline = new Polyline(cleaned.Count)
            {
                Closed = true
            };

            for (var i = 0; i < cleaned.Count; i++)
            {
                polyline.AddVertexAt(i, cleaned[i], 0, 0, 0);
            }

            return polyline;
        }

        private static bool TryGetQuarterAnchors(Polyline section, out QuarterAnchors anchors)
        {
            anchors = default;
            if (section.NumberOfVertices < 3)
            {
                return false;
            }

            var vertices = new List<Point3d>(section.NumberOfVertices);
            for (var i = 0; i < section.NumberOfVertices; i++)
            {
                var point = section.GetPoint2dAt(i);
                vertices.Add(new Point3d(point.X, point.Y, 0));
            }

            if (!TryGetQuarterAnchorsByEdgeMedianVertexChain(vertices, out var topV, out var bottomV, out var leftV, out var rightV))
            {
                return false;
            }

            anchors = new QuarterAnchors(
                new Point2d(topV.X, topV.Y),
                new Point2d(bottomV.X, bottomV.Y),
                new Point2d(leftV.X, leftV.Y),
                new Point2d(rightV.X, rightV.Y));
            return true;
        }

        private static bool TryGetQuarterAnchorsByEdgeMedianVertexChain(
            List<Point3d> verts,
            out Point3d topV,
            out Point3d bottomV,
            out Point3d leftV,
            out Point3d rightV)
        {
            topV = bottomV = leftV = rightV = Point3d.Origin;
            if (verts == null || verts.Count < 3)
            {
                return false;
            }

            var n = verts.Count;
            var edges = new List<EdgeInfo>(n);
            for (var i = 0; i < n; i++)
            {
                var a = verts[i];
                var b = verts[(i + 1) % n];
                var v = b - a;
                var len = v.Length;
                if (len <= 1e-9)
                {
                    continue;
                }

                var u = new Vector3d(v.X / len, v.Y / len, 0);
                var mid = new Point3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, 0);
                edges.Add(new EdgeInfo { Index = i, A = a, B = b, U = u, Mid = mid, Len = len });
            }

            if (edges.Count == 0)
            {
                return false;
            }

            const double degTol = 12.0;
            var cosTol = Math.Cos(degTol * Math.PI / 180.0);
            var topEdge = default(EdgeInfo);
            var bestTopY = double.MinValue;
            foreach (var edge in edges)
            {
                var horiz = Math.Abs(edge.U.DotProduct(Vector3d.XAxis));
                var avgY = (edge.A.Y + edge.B.Y) * 0.5;
                if (horiz >= cosTol && avgY > bestTopY)
                {
                    bestTopY = avgY;
                    topEdge = edge;
                }
            }

            if (bestTopY == double.MinValue)
            {
                topEdge = edges.OrderByDescending(edge => edge.Len).First();
            }

            var east = topEdge.U.GetNormal();
            if (east.Length <= 1e-12)
            {
                return false;
            }

            var north = east.RotateBy(Math.PI / 2.0, Vector3d.ZAxis).GetNormal();

            var minE = double.MaxValue;
            var maxE = double.MinValue;
            var minN = double.MaxValue;
            var maxN = double.MinValue;
            for (var i = 0; i < n; i++)
            {
                var dp = verts[i] - Point3d.Origin;
                var pe = dp.DotProduct(east);
                var pn = dp.DotProduct(north);
                minE = Math.Min(minE, pe);
                maxE = Math.Max(maxE, pe);
                minN = Math.Min(minN, pn);
                maxN = Math.Max(maxN, pn);
            }

            var spanE = Math.Max(1e-6, maxE - minE);
            var spanN = Math.Max(1e-6, maxN - minN);
            var bandTol = Math.Max(5.0, 0.01 * Math.Max(spanE, spanN));

            var eChains = BuildChainsClosest(edges, east, north);
            var nChains = BuildChainsClosest(edges, north, east);
            if (eChains.Count == 0 || nChains.Count == 0)
            {
                return false;
            }

            bool TouchesTop(ChainInfo ch)
            {
                foreach (var idx in ChainVertexIndices(ch, n))
                {
                    if (maxN - AxisProj(verts[idx], north) <= bandTol)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TouchesBottom(ChainInfo ch)
            {
                foreach (var idx in ChainVertexIndices(ch, n))
                {
                    if (AxisProj(verts[idx], north) - minN <= bandTol)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TouchesLeft(ChainInfo ch)
            {
                foreach (var idx in ChainVertexIndices(ch, n))
                {
                    if (AxisProj(verts[idx], east) - minE <= bandTol)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TouchesRight(ChainInfo ch)
            {
                foreach (var idx in ChainVertexIndices(ch, n))
                {
                    if (maxE - AxisProj(verts[idx], east) <= bandTol)
                    {
                        return true;
                    }
                }

                return false;
            }

            var top = eChains.Where(TouchesTop).DefaultIfEmpty(eChains.OrderByDescending(c => c.Score).First()).First();
            var bottom = eChains.Where(TouchesBottom).DefaultIfEmpty(eChains.OrderBy(c => c.Score).First()).First();
            var left = nChains.Where(TouchesLeft).DefaultIfEmpty(nChains.OrderBy(c => c.Score).First()).First();
            var right = nChains.Where(TouchesRight).DefaultIfEmpty(nChains.OrderByDescending(c => c.Score).First()).First();

            var targetE = 0.5 * (minE + maxE);
            topV = ChainVertexNearestAxisValue(verts, top, east, targetE);
            bottomV = ChainVertexNearestAxisValue(verts, bottom, east, targetE);

            var targetN = 0.5 * (minN + maxN);
            leftV = ChainVertexNearestAxisValue(verts, left, north, targetN);
            rightV = ChainVertexNearestAxisValue(verts, right, north, targetN);

            var Emid = 0.5 * (AxisProj(leftV, east) + AxisProj(rightV, east));
            var Nmid = 0.5 * (AxisProj(topV, north) + AxisProj(bottomV, north));
            if (Math.Abs(Emid - 0.5 * (minE + maxE)) > 0.25 * spanE ||
                Math.Abs(Nmid - 0.5 * (minN + maxN)) > 0.25 * spanN)
            {
                Point3d FromEN(double e, double nCoord) =>
                    new Point3d(east.X * e + north.X * nCoord, east.Y * e + north.Y * nCoord, 0);

                topV = FromEN(0.5 * (minE + maxE), maxN);
                bottomV = FromEN(0.5 * (minE + maxE), minN);
                leftV = FromEN(minE, 0.5 * (minN + maxN));
                rightV = FromEN(maxE, 0.5 * (minN + maxN));
            }

            return true;
        }

        private static List<ChainInfo> BuildChainsClosest(List<EdgeInfo> edges, Vector3d primary, Vector3d other)
        {
            var chains = new List<ChainInfo>();
            var inChain = false;
            var start = -1;
            var sumProj = 0.0;
            var cnt = 0;
            var totLen = 0.0;

            for (var i = 0; i < edges.Count; i++)
            {
                var edge = edges[i];
                var de = Math.Abs(edge.U.DotProduct(primary));
                var dn = Math.Abs(edge.U.DotProduct(other));
                var isPrimary = de >= dn;

                if (isPrimary)
                {
                    if (!inChain)
                    {
                        inChain = true;
                        start = edge.Index;
                        sumProj = 0.0;
                        cnt = 0;
                        totLen = 0.0;
                    }

                    sumProj += (edge.Mid - Point3d.Origin).DotProduct(other);
                    cnt++;
                    totLen += edge.Len;
                }
                else if (inChain)
                {
                    chains.Add(new ChainInfo
                    {
                        Start = start,
                        SegCount = cnt,
                        Score = cnt > 0 ? sumProj / cnt : 0.0,
                        TotalLen = totLen
                    });
                    inChain = false;
                }
            }

            if (inChain)
            {
                chains.Add(new ChainInfo
                {
                    Start = start,
                    SegCount = cnt,
                    Score = cnt > 0 ? sumProj / cnt : 0.0,
                    TotalLen = totLen
                });
            }

            if (chains.Count >= 2)
            {
                var first = chains[0];
                var last = chains[chains.Count - 1];
                if (first.Start == 0 && (last.Start + last.SegCount == edges.Count))
                {
                    var totalSeg = last.SegCount + first.SegCount;
                    var totalLen = last.TotalLen + first.TotalLen;
                    var avgScore = totalSeg > 0
                        ? (last.Score * last.SegCount + first.Score * first.SegCount) / totalSeg
                        : 0.0;
                    var merged = new ChainInfo
                    {
                        Start = last.Start,
                        SegCount = totalSeg,
                        Score = avgScore,
                        TotalLen = totalLen
                    };
                    chains[0] = merged;
                    chains.RemoveAt(chains.Count - 1);
                }
            }

            return chains;
        }

        private static IEnumerable<int> ChainVertexIndices(ChainInfo chain, int vertexCount)
        {
            for (var k = 0; k <= chain.SegCount; k++)
            {
                yield return (chain.Start + k) % vertexCount;
            }
        }

        private static double AxisProj(Point3d point, Vector3d axis)
        {
            return (point - Point3d.Origin).DotProduct(axis);
        }

        private static Point3d ChainVertexNearestAxisValue(List<Point3d> verts, ChainInfo chain, Vector3d axis, double target)
        {
            var bestIdx = chain.Start % verts.Count;
            var best = double.MaxValue;

            foreach (var idx in ChainVertexIndices(chain, verts.Count))
            {
                var distance = Math.Abs(AxisProj(verts[idx], axis) - target);
                if (distance < best)
                {
                    best = distance;
                    bestIdx = idx;
                }
            }

            return verts[bestIdx];
        }

        private static IReadOnlyList<string> BuildSectionIndexSearchFolders(Config config)
        {
            var folders = new List<string>();
            AddFolder(folders, config.SectionIndexFolder);
            AddFolder(folders, new Config().SectionIndexFolder);
            AddFolder(folders, Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory);
            return folders;
        }

        private static void AddFolder(List<string> folders, string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(folder);
            }
        }

        private static bool TryLoadSectionOutline(
            IReadOnlyList<string> searchFolders,
            SectionKey key,
            Logger logger,
            out SectionOutline outline)
        {
            outline = null;
            var checkedAny = false;
            foreach (var folder in searchFolders)
            {
                if (!FolderHasSectionIndex(folder, key.Zone))
                {
                    continue;
                }

                checkedAny = true;
                if (SectionIndexReader.TryLoadSectionOutline(folder, key, logger, out outline))
                {
                    return true;
                }
            }

            if (!checkedAny)
            {
                logger.WriteLine($"No section index file found for zone {key.Zone}. Searched: {string.Join("; ", searchFolders)}");
            }

            return false;
        }

        private static bool FolderHasSectionIndex(string folder, int zone)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }

            var jsonl = Path.Combine(folder, $"Master_Sections.index_Z{zone}.jsonl");
            var csv = Path.Combine(folder, $"Master_Sections.index_Z{zone}.csv");
            var jsonlFallback = Path.Combine(folder, "Master_Sections.index.jsonl");
            var csvFallback = Path.Combine(folder, "Master_Sections.index.csv");
            return File.Exists(jsonl) || File.Exists(csv) || File.Exists(jsonlFallback) || File.Exists(csvFallback);
        }

        private static void EnsureLayer(Database database, Transaction transaction, string layerName)
        {
            var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (table.Has(layerName))
            {
                return;
            }

            table.UpgradeOpen();
            var record = new LayerTableRecord
            {
                Name = layerName
            };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static void InsertSectionLabelBlock(
            BlockTableRecord modelSpace,
            BlockTable blockTable,
            Transaction transaction,
            Editor editor,
            Point3d position,
            SectionKey key)
        {
            const string blockName = "L-SECLBL";

            if (!blockTable.Has(blockName))
            {
                editor?.WriteMessage($"\nBUILDSEC: Block '{blockName}' not found; skipped section label.");
                return;
            }

            var blockId = blockTable[blockName];
            var blockRef = new BlockReference(position, blockId)
            {
                ScaleFactors = new Scale3d(1.0)
            };
            modelSpace.AppendEntity(blockRef);
            transaction.AddNewlyCreatedDBObject(blockRef, true);

            var blockDef = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            if (blockDef.HasAttributeDefinitions)
            {
                foreach (ObjectId id in blockDef)
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition definition))
                    {
                        continue;
                    }

                    if (definition.Constant)
                    {
                        continue;
                    }

                    var reference = new AttributeReference();
                    reference.SetAttributeFromBlock(definition, blockRef.BlockTransform);
                    blockRef.AttributeCollection.AppendAttribute(reference);
                    transaction.AddNewlyCreatedDBObject(reference, true);
                }
            }

            SetBlockAttribute(blockRef, transaction, "SEC", key.Section);
            SetBlockAttribute(blockRef, transaction, "TWP", key.Township);
            SetBlockAttribute(blockRef, transaction, "RGE", key.Range);
            SetBlockAttribute(blockRef, transaction, "MER", key.Meridian);
        }

        private static void SetBlockAttribute(BlockReference blockRef, Transaction transaction, string tag, string value)
        {
            if (blockRef == null || string.IsNullOrWhiteSpace(tag))
            {
                return;
            }

            foreach (ObjectId id in blockRef.AttributeCollection)
            {
                if (!(transaction.GetObject(id, OpenMode.ForWrite) is AttributeReference attr))
                {
                    continue;
                }

                if (string.Equals(attr.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    attr.TextString = value ?? string.Empty;
                }
            }
        }

        private struct QuarterAnchors
        {
            public QuarterAnchors(Point2d top, Point2d bottom, Point2d left, Point2d right)
            {
                Top = top;
                Bottom = bottom;
                Left = left;
                Right = right;
            }

            public Point2d Top { get; }
            public Point2d Bottom { get; }
            public Point2d Left { get; }
            public Point2d Right { get; }
        }

        private struct EdgeInfo
        {
            public int Index;
            public Point3d A;
            public Point3d B;
            public Point3d Mid;
            public Vector3d U;
            public double Len;
        }

        private struct ChainInfo
        {
            public int Start;
            public int SegCount;
            public double Score;
            public double TotalLen;
        }

        private static string BuildSectionKeyId(SectionKey key)
        {
            return $"Z{key.Zone}_SEC{key.Section}_TWP{key.Township}_RGE{key.Range}_MER{key.Meridian}";
        }
    }

    public sealed class SectionDrawResult
    {
        public SectionDrawResult(List<ObjectId> quarterPolylineIds, List<ObjectId> sectionPolylineIds, bool generatedFromIndex)
        {
            QuarterPolylineIds = quarterPolylineIds;
            SectionPolylineIds = sectionPolylineIds;
            GeneratedFromIndex = generatedFromIndex;
        }

        public List<ObjectId> QuarterPolylineIds { get; }
        public List<ObjectId> SectionPolylineIds { get; }
        public bool GeneratedFromIndex { get; }
    }

    public sealed class SectionBuildResult
    {
        public SectionBuildResult(ObjectId sectionPolylineId, Dictionary<QuarterSelection, ObjectId> quarterPolylineIds)
        {
            SectionPolylineId = sectionPolylineId;
            QuarterPolylineIds = quarterPolylineIds;
        }

        public ObjectId SectionPolylineId { get; }
        public Dictionary<QuarterSelection, ObjectId> QuarterPolylineIds { get; }
    }

    public sealed class SectionRequest
    {
        public SectionRequest(QuarterSelection quarter, SectionKey key)
        {
            Quarter = quarter;
            Key = key;
        }

        public QuarterSelection Quarter { get; }
        public SectionKey Key { get; }
    }

    public enum QuarterSelection
    {
        None,
        NorthWest,
        NorthEast,
        SouthWest,
        SouthEast,
        All
    }

    public sealed class SummaryResult
    {
        public int TotalDispositions { get; set; }
        public int LabelsPlaced { get; set; }
        public int SkippedNoOd { get; set; }
        public int SkippedNotClosed { get; set; }
        public int SkippedNoLayerMapping { get; set; }
        public int OverlapForced { get; set; }
        public int MultiQuarterProcessed { get; set; }
        public int ImportedDispositions { get; set; }
        public int FilteredDispositions { get; set; }
        public int DedupedDispositions { get; set; }
        public int ImportFailures { get; set; }
    }

    public sealed class Logger : IDisposable
    {
        private StreamWriter? _writer;

        public void Initialize(string path)
        {
            try
            {
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream) { AutoFlush = true };
                WriteLine("---- ATSBUILD " + DateTime.Now + " ----");
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Logger init failed for {path}: {ex.Message}");
                var fallbackPath = BuildFallbackLogPath(path);
                try
                {
                    var stream = new FileStream(fallbackPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(stream) { AutoFlush = true };
                    WriteLine("---- ATSBUILD " + DateTime.Now + " ----");
                    WriteLine($"Logger initialized with fallback path: {fallbackPath}");
                }
                catch (IOException fallbackEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Logger fallback init failed for {fallbackPath}: {fallbackEx.Message}");
                }
            }
        }

        public void WriteLine(string message)
        {
            _writer?.WriteLine(message);
        }

        public void Dispose()
        {
            _writer?.Dispose();
        }

        private static string BuildFallbackLogPath(string path)
        {
            var directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
            var baseName = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            return Path.Combine(directory, $"{baseName}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}");
        }
    }
}

/////////////////////////////////////////////////////////////////////

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\REFERENCE ONLY\ats-builder-refernce_ChatGPT_Combined.cs
/////////////////////////////////////////////////////////////////////

// AUTO-GENERATED MERGE FILE FOR REVIEW
// Generated: 2026-01-31 22:45:39

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Work Test 2\Desktop\SURF DEV\ResidenceSync\Properties\AssemblyInfo.cs
/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using Autodesk.Gis.Map;
using Autodesk.Gis.Map.Constants;
using Autodesk.Gis.Map.ObjectData;
using Autodesk.Gis.Map.Project;
using Autodesk.Gis.Map.Utilities;
using ResidenceSync.Properties;
using ResidenceSync.UI;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using DbOpenMode = Autodesk.AutoCAD.DatabaseServices.OpenMode;
using OdDataType = Autodesk.Gis.Map.Constants.DataType;
using OdOpenMode = Autodesk.Gis.Map.Constants.OpenMode;
using OdTable = Autodesk.Gis.Map.ObjectData.Table;
[assembly: CommandClass(typeof(ResidenceSync.ResidenceSyncCommands))]
[assembly: CommandClass(typeof(RSUiCommands))]

[assembly: AssemblyTitle("ResidenceSync")]
[assembly: AssemblyDescription("Residence synchronization tools for AutoCAD.")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("ResidenceSync")]
[assembly: AssemblyProduct("ResidenceSync")]
[assembly: AssemblyCopyright("Copyright Â© ResidenceSync 2024")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]

[assembly: Guid("2d6e1d6c-7235-4fbb-8bb5-5a1f9deecb3a")]

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Work Test 2\Desktop\SURF DEV\ResidenceSync\ResidenceSyncCommands.cs
/////////////////////////////////////////////////////////////////////

// (using directives consolidated at top of file)


namespace ResidenceSync
{
    public class ResidenceSyncCommands
    {
        private const string MASTER_FILES_DIRECTORY = @"M:\Drafting\_SHARED FILES\_CG_SHARED";
        private const string PRIMARY_INDEX_DIRECTORY = @"C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\RES MANAGER";
        private const string FALLBACK_INDEX_DIRECTORY = @"C:\AUTOCAD-SETUP\Lisp_2000\COMPASS\RES MANAGER";

        private enum CoordinateZone
        {
            Zone11 = 11,
            Zone12 = 12
        }

        private static string FormatZoneNumber(CoordinateZone zone)
            => ((int)zone).ToString(CultureInfo.InvariantCulture);

        private static string GetMasterResidencesPath(CoordinateZone zone)
            => Path.Combine(MASTER_FILES_DIRECTORY, $"Master_Residences_Z{FormatZoneNumber(zone)}.dwg");

        private static string GetMasterSectionsPath(CoordinateZone zone)
            => Path.Combine(MASTER_FILES_DIRECTORY, $"Master_Sections_Z{FormatZoneNumber(zone)}.dwg");

        private static string GetSectionsIndexDirectory()
            => Directory.Exists(PRIMARY_INDEX_DIRECTORY) ? PRIMARY_INDEX_DIRECTORY : FALLBACK_INDEX_DIRECTORY;

        private static string GetMasterSectionsIndexJsonPath(CoordinateZone zone)
            => Path.Combine(GetSectionsIndexDirectory(), $"Master_Sections.index_Z{FormatZoneNumber(zone)}.jsonl");

        private static string GetMasterSectionsIndexCsvPath(CoordinateZone zone)
            => Path.Combine(GetSectionsIndexDirectory(), $"Master_Sections.index_Z{FormatZoneNumber(zone)}.csv");

        private static bool TryConvertToZone(int zoneNumber, out CoordinateZone zone)
        {
            switch (zoneNumber)
            {
                case 11:
                    zone = CoordinateZone.Zone11;
                    return true;
                case 12:
                    zone = CoordinateZone.Zone12;
                    return true;
                default:
                    zone = default;
                    return false;
            }
        }
        private const string PREFERRED_OD_TABLE = "SECTIONS";
        private const string RESIDENCE_LAYER = "Z-RESIDENCE";

        // Tolerances (metres, WCS)
        private const double DEDUPE_TOL = 0.25;  // merge if within 25 cm
        private const double REPLACE_TOL = 3.0;   // replace if within 3 m
        private const double ERASE_TOL = 0.001;  // polygon test epsilon (ray-cast denom guard)
        private const double TRANSFORM_VALIDATION_TOL = 0.75; // scaled push must align within < 1 m


        // ----- OD field alias sets (used for reading/writing across mixed tables) -----
        private static readonly string[] JOB_ALIASES = { "JOB_NUM", "JOBNUM", "JOB", "JOB_NUMBER" };
        private static readonly string[] DESC_ALIASES = { "DESCRIPTION", "DESC", "DESCRIP", "DESCR" };
        private static readonly string[] NOTES_ALIASES = { "NOTES", "NOTE", "COMMENTS", "COMMENT", "REMARKS" };



        // Recognized residence block names (case-insensitive)
        private static readonly HashSet<string> RES_BLOCK_NAMES =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "res_other", "res_occ", "res_abd", "AS-RESIDENCE", "RES_OTHER", "RES_OCC", "RES_ABD" };

        // Canonical residence OD table names (use the colon form)
        private const string RES_OD_CREATE_DEFAULT = "Block:res_other";
        private const string RES_OD_PRIMARY_TABLE = "Block:res_other";



        private static ObjectId EnsureLayerGetId(Database db, string layerName, Transaction tr)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, DbOpenMode.ForRead);
            if (!lt.Has(layerName))
            {
                lt.UpgradeOpen();
                var ltr = new LayerTableRecord { Name = layerName };
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
                return ltr.ObjectId;
            }
            return lt[layerName];
        }

        // Normalizes table name for comparison: lower-case, remove any ':' after "block"
        private static string NormOdTableName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.Trim();
            if (s.StartsWith("OD:", StringComparison.OrdinalIgnoreCase)) s = s.Substring(3);
            return s; // keep â€œBlock:res_otherâ€ distinct from â€œBlockres_otherâ€
        }

        // =========================================================================
        // RESINDEXV â€” Build vertex index (JSONL) from Master_Sections.dwg
        // =========================================================================
        [CommandMethod("ResidenceSync", "RESINDEXV", CommandFlags.Modal)]
        public void BuildVertexIndex()
        {
            Editor ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;

            if (!PromptZone(ed, out CoordinateZone zone)) return;

            string masterSectionsPath = GetMasterSectionsPath(zone);
            if (!File.Exists(masterSectionsPath))
            {
                ed?.WriteMessage($"\nRESINDEXV: Master sections DWG not found: {masterSectionsPath}");
                return;
            }

            string outJsonPath = GetMasterSectionsIndexJsonPath(zone);
            string outCsvPath = GetMasterSectionsIndexCsvPath(zone);

            DocumentCollection docs = AcadApp.DocumentManager;
            Document master = GetOpenDocumentByPath(docs, masterSectionsPath);
            bool openedHere = false;
            if (master == null)
            {
                master = docs.Open(masterSectionsPath, false);
                openedHere = true;
            }

            try
            {
                using (master.LockDocument())
                {
                    var project = HostMapApplicationServices.Application?.Projects?.GetProject(master);
                    if (project == null)
                    {
                        ed?.WriteMessage("\nRESINDEXV: Map 3D project unavailable.");
                        return;
                    }

                    Tables tables = project.ODTables;
                    var searchOrder = BuildOdTableSearchOrder(tables);

                    // Pick "best" outline per section key: largest AABB area; store all vertices
                    var bestByKey = new Dictionary<string, (Aabb2d aabb, ObjectId entId)>(StringComparer.OrdinalIgnoreCase);

                    using (Transaction tr = master.TransactionManager.StartTransaction())
                    {
                        var bt = (BlockTable)tr.GetObject(master.Database.BlockTableId, DbOpenMode.ForRead);
                        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], DbOpenMode.ForRead);

                        int scanned = 0;
                        foreach (ObjectId id in ms)
                        {
                            var ent = tr.GetObject(id, DbOpenMode.ForRead) as Entity;
                            if (!IsPolylineEntity(ent)) continue;

                            if (!TryGetEntityAabb(master.Database, id, tr, out var aabb)) continue;

                            foreach (string tn in searchOrder)
                            {
                                if (string.IsNullOrWhiteSpace(tn)) continue;
                                using (OdTable t = tables[tn])
                                {
                                    if (t == null) continue;
                                    var defs = t.FieldDefinitions;

                                    using (Records recs = t.GetObjectTableRecords(0, ent.ObjectId, OdOpenMode.OpenForRead, true))
                                    {
                                        if (recs == null || recs.Count == 0) continue;

                                        foreach (Record rec in recs)
                                        {
                                            string sec = ReadOd(defs, rec, new[] { "SEC", "SECTION" }, MapValueToString);
                                            string twp = ReadOd(defs, rec, new[] { "TWP", "TOWNSHIP" }, MapValueToString);
                                            string rge = ReadOd(defs, rec, new[] { "RGE", "RANGE" }, MapValueToString);
                                            string mer = ReadOd(defs, rec, new[] { "MER", "MERIDIAN", "M" }, MapValueToString);

                                            string normSec = NormStr(sec);
                                            string normTwp = NormStr(twp);
                                            string normRge = NormStr(rge);
                                            string normMer = NormStr(mer);

                                            if (string.IsNullOrWhiteSpace(normSec) ||
                                                string.IsNullOrWhiteSpace(normTwp) ||
                                                string.IsNullOrWhiteSpace(normRge) ||
                                                string.IsNullOrWhiteSpace(normMer))
                                                continue;

                                            if (!MeridianMatchesZone(zone, normMer))
                                                continue;

                                            string key = $"{normSec}|{normTwp}|{normRge}|{normMer}";

                                            double area = (aabb.MaxX - aabb.MinX) * (aabb.MaxY - aabb.MinY);
                                            if (!bestByKey.TryGetValue(key, out var cur) ||
                                                area > (cur.aabb.MaxX - cur.aabb.MinX) * (cur.aabb.MaxY - cur.aabb.MinY))
                                            {
                                                bestByKey[key] = (aabb, id);
                                            }
                                        }
                                    }
                                }
                            }

                            scanned++;
                            if ((scanned % 10000) == 0) ed?.WriteMessage($"\nRESINDEXV: scanned {scanned} entities...");
                        }

                        tr.Commit();
                    }

                    // Write JSONL + CSV with all vertices of the chosen polyline per key
                    Directory.CreateDirectory(Path.GetDirectoryName(outJsonPath) ?? "");
                    Directory.CreateDirectory(Path.GetDirectoryName(outCsvPath) ?? "");
                    using (var swJson = new StreamWriter(new FileStream(outJsonPath, FileMode.Create, FileAccess.Write, FileShare.Read)))
                    using (var swCsv = new StreamWriter(new FileStream(outCsvPath, FileMode.Create, FileAccess.Write, FileShare.Read)))
                    using (Transaction tr = master.TransactionManager.StartTransaction())
                    {
                        swCsv.WriteLine("ZONE,SEC,TWP,RGE,MER,minx,miny,maxx,maxy");

                        foreach (var kv in bestByKey)
                        {
                            var ent = tr.GetObject(kv.Value.entId, DbOpenMode.ForRead) as Entity;
                            if (ent == null) continue;

                            // Extract ordered vertices (WCS; flatten Z)
                            var verts = new List<Point3d>();
                            bool closed = false;

                            if (ent is Polyline pl)
                            {
                                for (int i = 0; i < pl.NumberOfVertices; i++) verts.Add(pl.GetPoint3dAt(i));
                                closed = pl.Closed;
                            }
                            else if (ent is Polyline2d pl2)
                            {
                                foreach (ObjectId vId in pl2)
                                {
                                    var v = (Vertex2d)tr.GetObject(vId, DbOpenMode.ForRead);
                                    verts.Add(v.Position);
                                }
                                closed = pl2.Closed;
                            }
                            else if (ent is Polyline3d pl3)
                            {
                                foreach (ObjectId vId in pl3)
                                {
                                    var v = (PolylineVertex3d)tr.GetObject(vId, DbOpenMode.ForRead);
                                    verts.Add(new Point3d(v.Position.X, v.Position.Y, 0));
                                }
                                closed = pl3.Closed;
                            }
                            else
                            {
                                continue;
                            }

                            var aabb = kv.Value.aabb;
                            string[] parts = kv.Key.Split('|'); // SEC|TWP|RGE|MER
                            string sec = parts.Length > 0 ? parts[0] : string.Empty;
                            string twp = parts.Length > 1 ? parts[1] : string.Empty;
                            string rge = parts.Length > 2 ? parts[2] : string.Empty;
                            string mer = parts.Length > 3 ? parts[3] : string.Empty;

                            var ic = CultureInfo.InvariantCulture;
                            swCsv.WriteLine(string.Format(ic, "{0},{1},{2},{3},{4},{5},{6},{7},{8}",
                                (int)zone, sec, twp, rge, mer,
                                aabb.MinX, aabb.MinY, aabb.MaxX, aabb.MaxY));

                            // Build JSON (no external deps)
                            var sb = new System.Text.StringBuilder(256 + verts.Count * 24);
                            sb.Append('{');
                            sb.AppendFormat(ic, "\"ZONE\":{0},\"SEC\":\"{1}\",\"TWP\":\"{2}\",\"RGE\":\"{3}\",\"MER\":\"{4}\",",
                                (int)zone, sec, twp, rge, mer);
                            sb.AppendFormat(ic, "\"AABB\":{{\"minx\":{0},\"miny\":{1},\"maxx\":{2},\"maxy\":{3}}},",
                                aabb.MinX, aabb.MinY, aabb.MaxX, aabb.MaxY);
                            sb.AppendFormat("\"Closed\":{0},", closed ? "true" : "false");
                            sb.Append("\"Verts\":[");
                            for (int i = 0; i < verts.Count; i++)
                            {
                                if (i > 0) sb.Append(',');
                                var p = verts[i];
                                sb.AppendFormat(ic, "[{0},{1}]", p.X, p.Y);
                            }
                            sb.Append("]}");

                            swJson.WriteLine(sb.ToString());
                        }
                    }

                    ed?.WriteMessage($"\nRESINDEXV: Wrote {bestByKey.Count} section outline(s) â†’ {outJsonPath} (JSON) and {outCsvPath} (CSV).");
                }
            }
            catch (System.Exception ex)
            {
                ed?.WriteMessage($"\nRESINDEXV: Failed: {ex.Message}");
            }
            finally
            {
                if (openedHere)
                {
                    try { master.CloseAndDiscard(); } catch { /* ignore */ }
                }
            }
        }

        // =========================================================================
        // BUILDSEC â€” Rebuild one section exactly as scanned + insert residences
        // =========================================================================
        [CommandMethod("ResidenceSync", "BUILDSEC", CommandFlags.Modal)]
        public void BuildSectionFromVertices()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            if (!ConfirmUtm(ed))
            {
                return;
            }

            if (!PromptSectionKey(ed, out SectionKey key)) return;

            string idxPath = GetMasterSectionsIndexJsonPath(key.Zone);
            if (!File.Exists(idxPath))
            {
                ed.WriteMessage($"\nBUILDSEC: Vertex index not found for Zone {FormatZoneNumber(key.Zone)}. Run RESINDEXV first.");
                return;
            }

            VertexIndexRecord rec;
            if (!TryReadSectionFromJsonl(idxPath, key, out rec))
            {
                ed.WriteMessage("\nBUILDSEC: Section not found in vertex index.");
                return;
            }

            using (doc.LockDocument())
            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                EnsureLayer(doc.Database, "L-USEC", tr);
                EnsureLayer(doc.Database, "L-QSEC", tr);

                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, DbOpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], DbOpenMode.ForWrite);

                // Outline from vertices (at master coords)
                var pl = new Polyline(rec.verts.Count);
                for (int i = 0; i < rec.verts.Count; i++)
                {
                    var p = rec.verts[i];
                    pl.AddVertexAt(i, new Point2d(p.X, p.Y), 0, 0, 0);
                }
                pl.Closed = rec.closed;
                pl.Layer = "L-USEC";
                // Ensure the polyline uses ByLayer color so it doesn't inherit a previous commandâ€™s color
                pl.ColorIndex = 256;
                ms.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);

                if (TryGetQuarterAnchorsByEdgeMedianVertexChain(rec.verts,
                        out Point3d topV, out Point3d botV,
                        out Point3d leftV, out Point3d rightV))
                {
                    // Create quarter lines and ensure they use ByLayer color (ColorIndex 256)
                    var qv = new Line(topV, botV) { Layer = "L-QSEC", ColorIndex = 256 };
                    var qh = new Line(leftV, rightV) { Layer = "L-QSEC", ColorIndex = 256 };
                    ms.AppendEntity(qv); tr.AddNewlyCreatedDBObject(qv, true);
                    ms.AppendEntity(qh); tr.AddNewlyCreatedDBObject(qh, true);

                    // Insert section label block at the intersection of quarter lines
                    var center = new Point3d(
                        0.5 * (topV.X + botV.X),
                        0.5 * (leftV.Y + rightV.Y),
                        0);
                    InsertSectionLabelBlock(ms, bt, tr, ed, center, key);
                }

                tr.Commit();
            }

            ed.WriteMessage("\nBUILDSEC: Section drawn from vertex index at master coordinates.");
        }

        private static bool ConfirmUtm(Editor ed)
        {
            var opts = new PromptKeywordOptions("\nARE YOU IN UTM? [Yes/No]: ", "Yes No")
            {
                AllowArbitraryInput = false,
                AllowNone = false
            };

            opts.Keywords.Default = "Yes";

            var res = ed.GetKeywords(opts);
            if (res.Status != PromptStatus.OK)
            {
                return false;
            }

            if (string.Equals(res.StringResult, "No", StringComparison.OrdinalIgnoreCase))
            {
                ed.WriteMessage("\nBUILDSEC: Command cancelled (not in UTM).");
                return false;
            }

            return true;
        }

        private static void InsertSectionLabelBlock(
            BlockTableRecord ms,
            BlockTable bt,
            Transaction tr,
            Editor ed,
            Point3d position,
            SectionKey key)
        {
            const string blockName = "L-SECLBL";

            if (!bt.Has(blockName))
            {
                ed?.WriteMessage($"\nBUILDSEC: Block '{blockName}' not found; skipped section label.");
                return;
            }

            var btrId = bt[blockName];
            var br = new BlockReference(position, btrId)
            {
                ScaleFactors = new Scale3d(1.0)
            };
            ms.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);

            var btr = (BlockTableRecord)tr.GetObject(btrId, DbOpenMode.ForRead);
            if (btr.HasAttributeDefinitions)
            {
                foreach (ObjectId id in btr)
                {
                    if (!(tr.GetObject(id, DbOpenMode.ForRead) is AttributeDefinition ad)) continue;
                    if (ad.Constant) continue;

                    var ar = new AttributeReference();
                    ar.SetAttributeFromBlock(ad, br.BlockTransform);
                    br.AttributeCollection.AppendAttribute(ar);
                    tr.AddNewlyCreatedDBObject(ar, true);
                }
            }

            SetBlockAttribute(br, "SEC", key.Section);
            SetBlockAttribute(br, "TWP", key.Township);
            SetBlockAttribute(br, "RGE", key.Range);
            SetBlockAttribute(br, "MER", key.Meridian);
        }

        // =========================================================================
        // PULLRESV â€” Pull residence blocks (with OD) from master into this DWG for a section
        // =========================================================================
        [CommandMethod("ResidenceSync", "PULLRESV", CommandFlags.Modal)]
        public void PullResidencesForSection()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            if (!PromptSectionKey(ed, out SectionKey key)) return;

            string idxPath = GetMasterSectionsIndexJsonPath(key.Zone);
            if (!TryReadSectionFromJsonl(idxPath, key, out VertexIndexRecord rec))
            {
                ed.WriteMessage($"\nPULLRESV: Section not found in vertex index for Zone {FormatZoneNumber(key.Zone)}. Run RESINDEXV first.");
                return;
            }

            string masterResidencesPath = GetMasterResidencesPath(key.Zone);

            if (!File.Exists(masterResidencesPath))
            {
                ed.WriteMessage($"\nPULLRESV: Master residences DWG not found: {masterResidencesPath}.");
                return;
            }

            using (doc.LockDocument())
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                EnsureLayer(doc.Database, RESIDENCE_LAYER, tr);

                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, DbOpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], DbOpenMode.ForWrite);

                int inserted = 0;

                // Open master db and collect recognized residence blocks inside the section polygon
                using (var masterDb = new Database(false, true))
                {
                    masterDb.ReadDwgFile(masterResidencesPath, FileOpenMode.OpenForReadAndAllShare, false, null);
                    masterDb.CloseInput(true);

                    // Collect only recognized residence BLOCKS (ignore legacy DBPOINTs here)
                    ObjectIdCollection srcIds = new ObjectIdCollection();
                    using (var trM = masterDb.TransactionManager.StartTransaction())
                    {
                        var btM = (BlockTable)trM.GetObject(masterDb.BlockTableId, DbOpenMode.ForRead);
                        var msM = (BlockTableRecord)trM.GetObject(btM[BlockTableRecord.ModelSpace], DbOpenMode.ForRead);

                        foreach (ObjectId id in msM)
                        {
                            var ent = trM.GetObject(id, DbOpenMode.ForRead) as Entity;
                            if (!(ent is BlockReference brM)) continue;

                            // filter by block name
                            string bn = GetEffectiveBlockName(brM, trM);
                            if (!RES_BLOCK_NAMES.Contains(bn)) continue;

                            // inside keyed section?
                            if (PointInPolygon2D(rec.verts, brM.Position.X, brM.Position.Y))
                                srcIds.Add(id);
                        }
                        trM.Commit();
                    }

                    if (srcIds.Count > 0)
                    {
                        // Clone with OD into this current drawing
                        var idMap = new IdMapping();
                        doc.Database.WblockCloneObjects(srcIds, ms.ObjectId, idMap, DuplicateRecordCloning.Replace, false);

                        // Force cloned items onto Z-RESIDENCE
                        ObjectId resLayerId = ForceLayerVisible(doc.Database, RESIDENCE_LAYER, tr);
                        foreach (IdPair p in idMap)
                        {
                            if (!p.IsCloned) continue;
                            var ent = tr.GetObject(p.Value, DbOpenMode.ForWrite) as Entity;
                            if (ent == null) continue;
                            ent.LayerId = resLayerId; // safer than name
                            inserted++;
                        }
                    }
                }

                tr.Commit();
                ed.WriteMessage($"\nPULLRESV: Inserted {inserted} residence block(s) with OD.");
            }

            ed.Regen();
        }

        [CommandMethod("ResidenceSync", "PUSHRESS", CommandFlags.Modal)]
        public void PushResidencesFromScaledSection()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            if (!PromptSectionKey(ed, out SectionKey key)) return;

            string masterResidencesPath = GetMasterResidencesPath(key.Zone);
            if (IsMasterPointsOpen(masterResidencesPath))
            {
                ed.WriteMessage($"\nPUSHRESS: '{Path.GetFileName(masterResidencesPath)}' is open. Close it and try again.");
                return;
            }

            if (!File.Exists(masterResidencesPath))
            {
                ed.WriteMessage($"\nPUSHRESS: Master residences DWG not found: {masterResidencesPath}.");
                return;
            }

            string idxPath = GetMasterSectionsIndexJsonPath(key.Zone);
            if (!TryReadSectionFromJsonl(idxPath, key, out VertexIndexRecord rec))
            {
                ed.WriteMessage($"\nPUSHRESS: Section not found in vertex index for Zone {FormatZoneNumber(key.Zone)}. Run RESINDEXV first.");
                return;
            }

            if (!TryGetSectionTopCorners(rec.verts, out Point3d masterTopLeft, out Point3d masterTopRight))
            {
                ed.WriteMessage("\nPUSHRESS: Unable to determine top edge in master section outline.");
                return;
            }

            var tlRes = ed.GetPoint(new PromptPointOptions("\nPick TOP LEFT of the section in scaled linework: ") { AllowNone = false });
            if (tlRes.Status != PromptStatus.OK) return;
            var trRes = ed.GetPoint(new PromptPointOptions("\nPick TOP RIGHT of the section in scaled linework: ")
            {
                UseBasePoint = true,
                BasePoint = tlRes.Value,
                AllowNone = false
            });
            if (trRes.Status != PromptStatus.OK) return;

            Matrix3d ucsToWcs = ed.CurrentUserCoordinateSystem.Inverse();
            Point3d localTL_W = tlRes.Value.TransformBy(ucsToWcs);
            Point3d localTR_W = trRes.Value.TransformBy(ucsToWcs);

            Vector3d vLocalW = localTR_W - localTL_W;
            Vector3d vMaster = masterTopRight - masterTopLeft;
            double lenLocal = vLocalW.Length, lenMaster = vMaster.Length;
            if (lenLocal < 1e-9 || lenMaster < 1e-9) { ed.WriteMessage("\nPUSHRESS: Degenerate corner picks."); return; }

            double scale = lenMaster / lenLocal;
            double angLoc = Math.Atan2(vLocalW.Y, vLocalW.X);
            double angMas = Math.Atan2(vMaster.Y, vMaster.X);
            double dTheta = angMas - angLoc;

            Point3d trProjected = TransformScaledPoint(localTR_W, localTL_W, masterTopLeft, scale, dTheta);
            double err = trProjected.DistanceTo(new Point3d(masterTopRight.X, masterTopRight.Y, 0));
            if (err > TRANSFORM_VALIDATION_TOL)
            {
                ed.WriteMessage($"\nPUSHRESS: Corner picks donâ€™t align (err {err:F3} m > {TRANSFORM_VALIDATION_TOL:F3} m).");
                return;
            }
            ed.WriteMessage($"\nPUSHRESS: using scale={scale:F6}, rot={(dTheta * 180.0 / Math.PI):F3}Â°, TR err={err:F3} m.");

            var sel = ed.GetSelection(
                new PromptSelectionOptions { MessageForAdding = "\nSelect residence blocks to push (res_other/res_occ/res_abd): " },
                new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT,POINT") })
            );
            if (sel.Status != PromptStatus.OK || sel.Value == null || sel.Value.Count == 0)
            {
                ed.WriteMessage("\nPUSHRESS: Nothing selected.");
                return;
            }

            string jobFromThisDwg = Path.GetFileNameWithoutExtension(doc.Name) ?? "";

            // Build items (attributes only)
            var items = CollectPushItemsFromSelection(
                doc, sel.Value, localTL_W, masterTopLeft, scale, dTheta, jobFromThisDwg, out int missingAttr, Matrix3d.Identity);

            if (items.Count == 0) { ed.WriteMessage("\nPUSHRESS: No usable items."); return; }

            var result = UpsertResidenceBlocksInMaster(items, jobFromThisDwg, masterResidencesPath, ed);
            ed.WriteMessage($"\nPUSHRESS: Finished â€” moved {result.moved}, inserted {result.inserted}. (Attrs only; master saved off-screen.)");
        }
        // Treat â€œinside OR on boundary within tolâ€ as inside.


        [CommandMethod("ResidenceSync", "SURFDEV", CommandFlags.Modal)]
        public void BuildSurfaceDevelopment()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            // 1) Center section
            if (!PromptSectionKey(ed, out SectionKey centerKey)) return;

            // 2) Grid size
            string gridSizeKey = "5x5";
            {
                var gridOpts = new PromptKeywordOptions("\nPick grid size [3x3/5x5/7x7/9x9]: ")
                {
                    AllowNone = true,
                    AllowArbitraryInput = false
                };
                gridOpts.Keywords.Add("3x3");
                gridOpts.Keywords.Add("5x5");
                gridOpts.Keywords.Add("7x7");
                gridOpts.Keywords.Add("9x9");
                gridOpts.Keywords.Default = "5x5";

                var gridRes = ed.GetKeywords(gridOpts);
                if (gridRes.Status == PromptStatus.Cancel) return;
                if (gridRes.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(gridRes.StringResult))
                    gridSizeKey = gridRes.StringResult;
                else if (gridRes.Status == PromptStatus.None)
                    gridSizeKey = gridOpts.Keywords.Default ?? "5x5";
            }

            int gridN = 5;
            if (!string.IsNullOrWhiteSpace(gridSizeKey))
            {
                var p = gridSizeKey.Trim();
                int x = p.IndexOf('x');
                string nStr = (x > 0) ? p.Substring(0, x) : p;
                if (int.TryParse(nStr, out int n) && (n == 3 || n == 5 || n == 7 || n == 9))
                    gridN = n;
            }

            // 3) Map scale
            var pko = new PromptKeywordOptions("\nPick scale [50k/30k/25k/20k]: ")
            {
                AllowNone = true,
                AllowArbitraryInput = false
            };
            pko.Keywords.Add("50k");
            pko.Keywords.Add("30k");
            pko.Keywords.Add("25k");
            pko.Keywords.Add("20k");
            pko.Keywords.Default = "50k";
            var kres = ed.GetKeywords(pko);
            if (kres.Status == PromptStatus.Cancel) return;
            string scaleKey = ((kres.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(kres.StringResult))
                ? kres.StringResult
                : (pko.Keywords.Default ?? "50k")).ToLowerInvariant();

            // 4) Surveyed vs Unsurveyed â†’ outline layer
            var pko2 = new PromptKeywordOptions("\nIs the development Surveyed or Unsurveyed? [Surveyed/Unsurveyed]: ")
            { AllowNone = true };
            pko2.Keywords.Add("Surveyed");
            pko2.Keywords.Add("Unsurveyed");
            pko2.Keywords.Default = "Unsurveyed";
            var kres2 = ed.GetKeywords(pko2);
            bool isSurveyed = (kres2.Status == PromptStatus.OK &&
                               string.Equals(kres2.StringResult, "Surveyed", StringComparison.OrdinalIgnoreCase));
            string outlineLayer = isSurveyed ? "L-SEC" : "L-USEC";

            // 5) Insert residences?
            var pko3 = new PromptKeywordOptions("\nInsert residence objects (blocks/points) from master? [No/Yes]: ")
            { AllowNone = true };
            pko3.Keywords.Add("No");
            pko3.Keywords.Add("Yes");
            pko3.Keywords.Default = "No";
            var kres3 = ed.GetKeywords(pko3);
            bool insertResidences = (kres3.Status == PromptStatus.OK &&
                                     string.Equals(kres3.StringResult, "Yes", StringComparison.OrdinalIgnoreCase));

            // 6) Insertion point â€” convert PICK (UCS) to WCS **using the INVERSE** of CUCS
            var pIns = ed.GetPoint("\nPick insertion point (centre of middle section): ");
            if (pIns.Status != PromptStatus.OK) return;
            Matrix3d ucsToWcs = ed.CurrentUserCoordinateSystem.Inverse(); // â† correct
            Point3d insertCenter = pIns.Value.TransformBy(ucsToWcs);      // UCS â†’ WCS

            // Units/scales
            double unitsPerKm;
            double secTextHt;
            double surfDevLinetypeScale;
            switch (scaleKey)
            {
                case "20k":
                    unitsPerKm = 250.0;
                    secTextHt = 37.5;
                    surfDevLinetypeScale = 0.50;
                    break;
                case "25k":
                    unitsPerKm = 200.0;
                    secTextHt = 30.0;
                    surfDevLinetypeScale = 0.50;
                    break;
                case "30k":
                    unitsPerKm = 166.66666666666666; // 100 * (50/30)
                    secTextHt = 25.0;
                    surfDevLinetypeScale = 0.50;
                    break;
                case "50k":
                default:
                    unitsPerKm = 100.0;
                    secTextHt = 15.0;
                    surfDevLinetypeScale = 0.25;
                    break;
            }
            double unitsPerMetre = unitsPerKm / 1000.0;

            // Index path
            string idxPath = GetMasterSectionsIndexJsonPath(centerKey.Zone);
            if (!File.Exists(idxPath))
            {
                ed.WriteMessage($"\nSURFDEV: Vertex index not found for Zone {FormatZoneNumber(centerKey.Zone)}. Run RESINDEXV first.");
                return;
            }

            // Load center record
            if (!TryReadSectionFromJsonl(idxPath, centerKey, out VertexIndexRecord centerRec))
            {
                ed.WriteMessage($"\nSURFDEV: Center section not found in vertex index for Zone {FormatZoneNumber(centerKey.Zone)}.");
                return;
            }

            // MASTER centre (AABB centre, WCS)
            Point3d centerMm = new Point3d(
                (centerRec.aabb.MinX + centerRec.aabb.MaxX) * 0.5,
                (centerRec.aabb.MinY + centerRec.aabb.MaxY) * 0.5, 0);

            // One transform for EVERYTHING (outlines, Q-lines, labels, residences)
            Matrix3d xform = BuildMasterToSketchTransform(centerMm, insertCenter, unitsPerMetre);

            // (Optional) quick sanity print
            ed.WriteMessage($"\n[SURFDEV] insertWCS=({insertCenter.X:0.###},{insertCenter.Y:0.###})  centerMm=({centerMm.X:0.###},{centerMm.Y:0.###})  units/m={unitsPerMetre:0.###}");

            // DLS serpentine (row 0=north, col 0=west)
            int[,] serp =
            {
        {31,32,33,34,35,36},
        {30,29,28,27,26,25},
        {19,20,21,22,23,24},
        {18,17,16,15,14,13},
        { 7, 8, 9,10,11,12},
        { 6, 5, 4, 3, 2, 1}
    };

            // Locate center
            if (!int.TryParse(centerKey.Section.TrimStart('0'), out int secNum))
            { ed.WriteMessage("\nSURFDEV: SEC not numeric."); return; }

            int selRow = -1, selCol = -1;
            for (int r = 0; r < 6 && selRow < 0; r++)
                for (int c = 0; c < 6; c++)
                    if (serp[r, c] == secNum) { selRow = r; selCol = c; break; }
            if (selRow < 0) { ed.WriteMessage("\nSURFDEV: SEC not in 1..36."); return; }

            // Neighbor key (Townshipâ†‘ north, Rangeâ†‘ west)
            SectionKey NeighborKey(int dRow, int dCol)
            {
                int targetRow = selRow + dRow;
                int targetCol = selCol + dCol;

                int twp; int rge;
                if (!int.TryParse(centerKey.Township.Trim(), out twp)) twp = 0;
                if (!int.TryParse(centerKey.Range.Trim(), out rge)) rge = 0;

                while (targetRow < 0) { twp += 1; targetRow += 6; }
                while (targetRow > 5) { twp -= 1; targetRow -= 6; }
                while (targetCol < 0) { rge += 1; targetCol += 6; }
                while (targetCol > 5) { rge -= 1; targetCol -= 6; }

                if (twp <= 0 || rge <= 0) return default(SectionKey);

                int s = serp[targetRow, targetCol];
                return new SectionKey(centerKey.Zone,
                                      s.ToString(CultureInfo.InvariantCulture),
                                      twp.ToString(CultureInfo.InvariantCulture),
                                      rge.ToString(CultureInfo.InvariantCulture),
                                      centerKey.Meridian);
            }

            using (doc.LockDocument())
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                // Layers
                EnsureLayer(doc.Database, outlineLayer, tr);
                EnsureLayer(doc.Database, "L-QSEC", tr);
                EnsureLayer(doc.Database, "S-7", tr);
                if (insertResidences) EnsureLayer(doc.Database, RESIDENCE_LAYER, tr);

                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, DbOpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], DbOpenMode.ForWrite);

                // Keep master-space polygons for residence hit-testing
                var sectionPolysMaster = new List<List<Point3d>>();
                int secCount = 0;

                int half = Math.Max(1, (gridN - 1) / 2);

                // ---- NxN window around centre ----
                for (int dRow = -half; dRow <= half; dRow++)
                {
                    for (int dCol = -half; dCol <= half; dCol++)
                    {
                        SectionKey k2 = NeighborKey(dRow, dCol);
                        if (string.IsNullOrEmpty(k2.Section)) continue;

                        if (!TryReadSectionFromJsonl(idxPath, k2, out VertexIndexRecord rec))
                            continue;

                        sectionPolysMaster.Add(rec.verts); // master coords

                        // Outline (build in master, then transform)
                        var pl = new Polyline(rec.verts.Count);
                        for (int i = 0; i < rec.verts.Count; i++)
                        {
                            var p = rec.verts[i]; // master
                            pl.AddVertexAt(i, new Point2d(p.X, p.Y), 0, 0, 0);
                        }
                        pl.Closed = rec.closed;
                        pl.Layer = outlineLayer;
                        pl.ColorIndex = 253;
                        ms.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);
                        pl.TransformBy(xform);
                        pl.LinetypeScale = surfDevLinetypeScale;

                        // Quarter-line anchors (master) â†’ entities â†’ transform
                        if (TryGetQuarterAnchorsByEdgeMedianVertexChain(rec.verts,
                                out Point3d topV, out Point3d botV, out Point3d leftV, out Point3d rightV))
                        {
                            var qv = new Line(topV, botV) { Layer = "L-QSEC", ColorIndex = 253 };
                            var qh = new Line(leftV, rightV) { Layer = "L-QSEC", ColorIndex = 253 };
                            ms.AppendEntity(qv); tr.AddNewlyCreatedDBObject(qv, true);
                            ms.AppendEntity(qh); tr.AddNewlyCreatedDBObject(qh, true);
                            qv.TransformBy(xform);
                            qh.TransformBy(xform);
                            qv.LinetypeScale = surfDevLinetypeScale;
                            qh.LinetypeScale = surfDevLinetypeScale;

                            // Label at cross center (compute in master, then transform point)
                            Point3d centerM = new Point3d(
                                (topV.X + botV.X + leftV.X + rightV.X) * 0.25,
                                (topV.Y + botV.Y + leftV.Y + rightV.Y) * 0.25, 0);
                            AddMaskedLabel(ms, tr, centerM.TransformBy(xform), k2.Section.TrimStart('0'), secTextHt, "S-7");
                        }
                        else
                        {
                            // Fallback: label at master AABB center â†’ transform point
                            double lx = rec.verts.Min(v => v.X), ly = rec.verts.Min(v => v.Y);
                            double ux = rec.verts.Max(v => v.X), uy = rec.verts.Max(v => v.Y);
                            Point3d ctrM = new Point3d((lx + ux) * 0.5, (ly + uy) * 0.5, 0);
                            AddMaskedLabel(ms, tr, ctrM.TransformBy(xform), k2.Section.TrimStart('0'), secTextHt, "S-7");
                        }

                        secCount++;
                    }
                }

                int inserted = 0;

                if (insertResidences)
                {
                    string masterResidencesPath = GetMasterResidencesPath(centerKey.Zone);

                    if (!File.Exists(masterResidencesPath))
                    {
                        ed.WriteMessage($"\nSURFDEV: Master residences DWG not found ({masterResidencesPath}); skipping residence insertion.");
                    }
                    else
                    {
                        using (var masterDb = new Database(false, true))
                        {
                            masterDb.ReadDwgFile(masterResidencesPath, FileOpenMode.OpenForReadAndAllShare, false, null);
                            masterDb.CloseInput(true);

                            ObjectIdCollection srcIds = CollectResidenceSourceIds(masterDb, sectionPolysMaster);

                            if (srcIds.Count > 0)
                            {
                                var idMap = new IdMapping();
                                doc.Database.WblockCloneObjects(
                                    srcIds, ms.ObjectId, idMap, DuplicateRecordCloning.Replace, false);

                                // Dest ids strictly from inputs; skip null/erased
                                var destIds = new List<ObjectId>(srcIds.Count);
                                foreach (ObjectId sid in srcIds)
                                {
                                    if (!idMap.Contains(sid)) continue;
                                    var pair = idMap[sid];
                                    if (!pair.IsCloned) continue;
                                    var did = pair.Value;
                                    if (did.IsNull || did.IsErased) continue;
                                    destIds.Add(did);
                                }

                                // Transform only top-level ModelSpace BR/DBPoint
                                foreach (var did in destIds)
                                {
                                    Entity ent = null;
                                    try { ent = tr.GetObject(did, DbOpenMode.ForWrite, /*openErased*/ true) as Entity; }
                                    catch { continue; }
                                    if (ent == null || ent.IsErased) continue;
                                    if (ent.OwnerId != ms.ObjectId) continue;

                                    if (ent is BlockReference br)
                                    {
                                        br.TransformBy(xform);              // same transform as outlines
                                        br.Layer = RESIDENCE_LAYER;
                                        br.ScaleFactors = new Scale3d(5.0); // force uniform scale = 5
                                        inserted++;
                                    }
                                    else if (ent is DBPoint dp)
                                    {
                                        dp.TransformBy(xform);              // same transform as outlines
                                        dp.Layer = RESIDENCE_LAYER;
                                        inserted++;
                                    }
                                }

                                if (inserted > 0) EnsurePointStyleVisible();
                            }
                        }
                    }
                }

                tr.Commit();

                ed.WriteMessage($"\nSURFDEV: Built {gridN}Ã—{gridN} ({secCount} sections){(insertResidences ? $", inserted {inserted} residence object(s) (blocks/points, OD preserved)" : "")}. Outlines/Q-sec color 253; labels on S-7 with mask.");
            }

            ed.Regen();
        }

        private static double PointToSegDist2(double px, double py,
                                              double ax, double ay,
                                              double bx, double by)
        {
            double vx = bx - ax, vy = by - ay;
            double wx = px - ax, wy = py - ay;

            double c1 = vx * wx + vy * wy;
            if (c1 <= 0.0) return (px - ax) * (px - ax) + (py - ay) * (py - ay);

            double c2 = vx * vx + vy * vy;
            if (c2 <= c1) return (px - bx) * (px - bx) + (py - by) * (py - by);

            double t = c1 / c2;
            double projx = ax + t * vx;
            double projy = ay + t * vy;
            double dx = px - projx, dy = py - projy;
            return dx * dx + dy * dy;
        }

        // What we push for each picked entity
        private sealed class PushItem
        {
            public Point3d Target;           // mapped (master WCS)
            public string BlockName;         // res_other/res_occ/res_abd (fallback = res_other)
            public string Desc;              // from OD (manual)
            public string Notes;             // from OD (manual)
            public string OdTable;           // table to use when writing (prefer record's table; else primary)
        }

        // Transform a source WCS point into master WCS
        private static Point3d MapScaledToMaster(Point3d sourceWcs, Point3d localTL, Point3d masterTL, double scale, double rot)
            => TransformScaledPoint(sourceWcs, localTL, masterTL, scale, rot);

        // Read OD from an entity (any of the supported tables). Returns true if we found one.
        // Read OD from an entity from any of our residence tables. Returns true if found.
        // Read OD from an entity from any of our residence tables. Returns true if found.
        // Read OD from an entity from any of our residence tables. Returns true if found.

        // C# 7.3-friendly helper (NO local functions)
        private static bool ProbeResidenceOd(
            FieldDefinitions defs,
            OdTable table,
            ObjectId entId,
            bool openXRecords,
            out string job,
            out string desc,
            out string notes)
        {
            job = desc = notes = null;

            using (Records recs = table.GetObjectTableRecords(0, entId, OdOpenMode.OpenForRead, openXRecords))
            {
                if (recs == null || recs.Count == 0) return false;

                foreach (Record r in recs)
                {
                    // Be permissive: JOB_NUM might be Character or accidentally an Integer in old data.
                    string j = ReadOd(defs, r, new[] { "JOB_NUM" }, mv =>
                    {
                        if (mv == null) return null;
                        if (mv.Type == OdDataType.Character) return mv.StrValue;
                        if (mv.Type == OdDataType.Integer) return mv.Int32Value.ToString(CultureInfo.InvariantCulture);
                        return mv.StrValue; // fallback
                    });

                    string d = ReadOd(defs, r, new[] { "DESCRIPTION" }, mv => mv?.StrValue);
                    string n = ReadOd(defs, r, new[] { "NOTES" }, mv => mv?.StrValue);

                    // Treat as a hit if any field actually exists on the record (empty string is OK)
                    if (j != null || d != null || n != null)
                    {
                        job = j; desc = d; notes = n;
                        return true;
                    }
                }
            }
            return false;
        }
        // ---------- ATTR READER (ADD THIS) ----------
        private static bool TryReadBlockAttributes(
            BlockReference br,
            out string job,
            out string desc,
            out string notes)
        {
            job = desc = notes = null;
            if (br == null) return false;

            var tm = br.Database?.TransactionManager;
            if (tm == null) return false;

            foreach (ObjectId attId in br.AttributeCollection)
            {
                var ar = tm.GetObject(attId, DbOpenMode.ForRead, false) as AttributeReference;
                if (ar == null) continue;
                var tag = ar.Tag ?? string.Empty;

                if (tag.Equals("JOB_NUM", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("JOBNUMBER", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("JOB", StringComparison.OrdinalIgnoreCase))
                {
                    if (job == null) job = ar.TextString;
                }
                else if (tag.Equals("DESCRIPTION", StringComparison.OrdinalIgnoreCase) ||
                         tag.Equals("DESC", StringComparison.OrdinalIgnoreCase))
                {
                    if (desc == null) desc = ar.TextString;
                }
                else if (tag.Equals("NOTES", StringComparison.OrdinalIgnoreCase) ||
                         tag.Equals("NOTE", StringComparison.OrdinalIgnoreCase) ||
                         tag.Equals("COMMENTS", StringComparison.OrdinalIgnoreCase))
                {
                    if (notes == null) notes = ar.TextString;
                }
            }
            return (job != null || desc != null || notes != null);
        }



        private static bool TrySetStringFieldByAliases(FieldDefinitions defs, Records recs, Record rec, string[] aliases, string value)
        {
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                if (def == null) continue;
                if (!aliases.Any(a => a.Equals(def.Name, StringComparison.OrdinalIgnoreCase))) continue;
                MapValue mv = rec[i];
                mv.Assign(value ?? string.Empty);
                recs.UpdateRecord(rec);
                return true;
            }
            return false;
        }

        private static bool TrySetStringFieldByAliases(FieldDefinitions defs, Record rec, string[] aliases, string value)
        {
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                if (def == null) continue;
                if (!aliases.Any(a => a.Equals(def.Name, StringComparison.OrdinalIgnoreCase))) continue;
                MapValue mv = rec[i];
                mv.Assign(value ?? string.Empty);
                return true;
            }
            return false;
        }

        // Set a block attribute (if present) to a value
        private static void SetBlockAttributes(BlockReference br, string job, string descOrNull, string notesOrNull)
        {
            if (br == null) return;
            SetBlockAttribute(br, "JOB_NUM", job ?? string.Empty);
            if (descOrNull != null) { SetBlockAttribute(br, "DESCRIPTION", descOrNull); SetBlockAttribute(br, "DESC", descOrNull); }
            if (notesOrNull != null) { SetBlockAttribute(br, "NOTES", notesOrNull); SetBlockAttribute(br, "NOTE", notesOrNull); }
        }

        // Build push items (reads OD from source; performs the scale/rotate/map)
        // Build push items (reads OD from source; performs the scale/rotate/map)
        // NOTE: wcsToUcs parameter is now unused; kept for signature compatibility.
        // Build push items. We no longer propagate DESCRIPTION/NOTES â€” only JOB_NUM.
        // Build push items (reads OD from source entities; performs the scale/rotate/map).
        // We propagate JOB_NUM, DESCRIPTION, and NOTES exactly as present in the source OD.
        // If a field is present but empty in source, we propagate empty (this will overwrite defaults in master).
        // ---------- BUILD PUSH LIST FROM SELECTION (ATTRS ONLY) ----------
        private List<PushItem> CollectPushItemsFromSelection(
            Document curDoc,
            SelectionSet sel,
            Point3d localTL_W,
            Point3d masterTL_W,
            double scale,
            double rot,
            string jobFromThisDwg,
            out int missingAttrCount,
            Matrix3d _unused)
        {
            missingAttrCount = 0;
            var items = new List<PushItem>();

            using (var tr = curDoc.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in sel)
                {
                    if (so?.ObjectId.IsNull != false) continue;
                    var ent = tr.GetObject(so.ObjectId, DbOpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    Point3d srcWcs;
                    string blockName = "res_other";

                    if (ent is BlockReference br)
                    {
                        srcWcs = br.Position;
                        blockName = GetEffectiveBlockName(br, tr);
                        if (!RES_BLOCK_NAMES.Contains(blockName)) continue;

                        // Read attributes
                        string j, d, n;
                        if (!TryReadBlockAttributes(br, out j, out d, out n))
                            missingAttrCount++;

                        // Map to master
                        Point3d target = MapScaledToMaster(srcWcs, localTL_W, masterTL_W, scale, rot);

                        items.Add(new PushItem
                        {
                            Target = target,
                            BlockName = blockName,
                            // Always push JOB from DWG name; DESCRIPTION/NOTES from block (empty => overwrite)
                            Desc = d ?? string.Empty,
                            Notes = n ?? string.Empty,
                            OdTable = null // unused now
                        });
                    }
                    else if (ent is DBPoint dp)
                    {
                        // Points have no attrs â€” still allow position copy, empty desc/notes
                        srcWcs = dp.Position;
                        Point3d target = MapScaledToMaster(srcWcs, localTL_W, masterTL_W, scale, rot);

                        items.Add(new PushItem
                        {
                            Target = target,
                            BlockName = blockName,     // will insert res_other if needed
                            Desc = string.Empty,
                            Notes = string.Empty,
                            OdTable = null
                        });
                    }
                }
                tr.Commit();
            }
            // merge duplicates (within tolerance)
            return DeduplicateItems(items, DEDUPE_TOL);
        }
        // Deduplicate by target location within tolerance
        private static List<PushItem> DeduplicateItems(List<PushItem> items, double tol)
        {
            var outList = new List<PushItem>(items.Count);
            foreach (var it in items)
            {
                bool near = outList.Any(x =>
                {
                    double dx = x.Target.X - it.Target.X;
                    double dy = x.Target.Y - it.Target.Y;
                    return (dx * dx + dy * dy) <= tol * tol;
                });
                if (!near) outList.Add(it);
            }
            return outList;
        }

        // If the selection lacks OD, attach a default record on the *current* drawing
        // If the selection lacks OD, attach a minimal record (JOB only) on the *current* drawing

        // Upsert into Master_Residences.dwg: move if within REPLACE_TOL, else insert.
        // Writes OD immediately to the canonical master table ("Blockres_other") and mirrors attributes.
        // Never injects defaults: we write exactly what CollectPushItemsFromSelection provided.
        // Prefer matching BLOCKs over DBPOINTs when both within tolerance.
        // ---------- UPSERT TO MASTER (SIDE DB, ATTRS ONLY) ----------
        // ---------- UPSERT TO MASTER (SIDE DB, ATTRS ONLY) ----------
        private (int moved, int inserted) UpsertResidenceBlocksInMaster(
            List<PushItem> items, string jobFromThisDwg, string masterPointsPath, Editor ed)
        {
            int moved = 0, inserted = 0;

            // Ensure file exists
            if (!File.Exists(masterPointsPath))
            {
                using (var newDb = new Database(true, true))
                    newDb.SaveAs(masterPointsPath, DwgVersion.Current);
            }

            // Open master DWG as side database (no UI)
            using (var masterDb = new Database(false, true))
            {
                masterDb.ReadDwgFile(masterPointsPath, FileOpenMode.OpenForReadAndAllShare, false, null);
                masterDb.CloseInput(true);

                using (var tr = masterDb.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(masterDb.BlockTableId, DbOpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], DbOpenMode.ForWrite);

                    // Ensure target layer exists and is visible
                    ObjectId resLayerId = ForceLayerVisible(masterDb, RESIDENCE_LAYER, tr);

                    // Index existing residence objects (blocks we recognize + legacy points)
                    var existing = new List<(ObjectId id, Point3d pos, bool isBlock)>();
                    foreach (ObjectId id in ms)
                    {
                        if (!(tr.GetObject(id, DbOpenMode.ForRead) is Entity e)) continue;

                        if (e is BlockReference br)
                        {
                            string bn = GetEffectiveBlockName(br, tr);
                            if (!RES_BLOCK_NAMES.Contains(bn)) continue;
                            existing.Add((id, br.Position, true));
                        }
                        else if (e is DBPoint dp)
                        {
                            existing.Add((id, dp.Position, false));
                        }
                    }

                    // Helper: ensure a block definition exists in *master* (clone from a source db if needed)
                    ObjectId EnsureBlockDef(string blockName, Database srcDb)
                    {
                        if (bt.Has(blockName)) return bt[blockName];

                        var src = srcDb ?? AcadApp.DocumentManager.MdiActiveDocument?.Database;
                        if (src == null) return ObjectId.Null;

                        using (var trS = src.TransactionManager.StartTransaction())
                        {
                            var btS = (BlockTable)trS.GetObject(src.BlockTableId, DbOpenMode.ForRead);
                            if (!btS.Has(blockName)) return ObjectId.Null;

                            var ids = new ObjectIdCollection { btS[blockName] };
                            var idMap = new IdMapping();
                            masterDb.WblockCloneObjects(ids, masterDb.BlockTableId, idMap,
                                                        DuplicateRecordCloning.Ignore, false);
                            trS.Commit();

                            // refresh local block table
                            bt = (BlockTable)tr.GetObject(masterDb.BlockTableId, DbOpenMode.ForRead);
                            return bt.Has(blockName) ? bt[blockName] : ObjectId.Null;
                        }
                    }

                    // Active drawing database to use as the source for block definitions
                    var activeDb = AcadApp.DocumentManager.MdiActiveDocument?.Database;

                    foreach (var it in items)
                    {
                        int idx = FindNearestIdxPreferBlocks(existing, it.Target, REPLACE_TOL);
                        if (idx >= 0)
                        {
                            // MOVE/UPDATE existing
                            var ex = existing[idx];
                            var eW = tr.GetObject(ex.id, DbOpenMode.ForWrite) as Entity;

                            if (eW is BlockReference brW)
                            {
                                var d = it.Target - brW.Position;
                                if (!d.IsZeroLength()) brW.TransformBy(Matrix3d.Displacement(d));
                                brW.LayerId = resLayerId;
                                brW.ScaleFactors = new Scale3d(5.0); // enforce uniform scale in master

                                // Write attributes (JOB from DWG name; DESC/NOTES from source item)
                                SetBlockAttributes(brW, jobFromThisDwg, it.Desc, it.Notes);
                            }
                            else if (eW is DBPoint dpW)
                            {
                                dpW.Position = it.Target;
                                dpW.LayerId = resLayerId;
                            }

                            existing[idx] = (ex.id, it.Target, ex.isBlock);
                            moved++;
                        }
                        else
                        {
                            // INSERT new block (preferred) or point if definition missing
                            ObjectId btrId = EnsureBlockDef(it.BlockName, activeDb);
                            if (btrId.IsNull)
                            {
                                var dbp = new DBPoint(it.Target) { LayerId = resLayerId };
                                ms.AppendEntity(dbp); tr.AddNewlyCreatedDBObject(dbp, true);
                                inserted++;
                                continue;
                            }

                            var br = new BlockReference(it.Target, btrId)
                            {
                                LayerId = resLayerId,
                                ScaleFactors = new Scale3d(5.0) // enforce uniform scale in master
                            };
                            ms.AppendEntity(br); tr.AddNewlyCreatedDBObject(br, true);

                            // Create attribute references and set values
                            var btrRec = (BlockTableRecord)tr.GetObject(btrId, DbOpenMode.ForRead);
                            if (btrRec.HasAttributeDefinitions)
                            {
                                foreach (ObjectId id in btrRec)
                                {
                                    var ad = tr.GetObject(id, DbOpenMode.ForRead) as AttributeDefinition;
                                    if (ad == null || ad.Constant) continue;

                                    var ar = new AttributeReference();
                                    ar.SetAttributeFromBlock(ad, br.BlockTransform);
                                    br.AttributeCollection.AppendAttribute(ar);
                                    tr.AddNewlyCreatedDBObject(ar, true);
                                }
                            }

                            SetBlockAttributes(br, jobFromThisDwg, it.Desc, it.Notes);
                            inserted++;
                        }
                    }

                    tr.Commit();
                }

                // Save side database back to disk (no UI)
                masterDb.SaveAs(masterPointsPath, DwgVersion.Current);
            }

            return (moved, inserted);
        }
        private bool IsMasterPointsOpen(string masterPointsPath)
        {
            return GetOpenDocumentByPath(AcadApp.DocumentManager, masterPointsPath) != null;
        }
        // For a NEW record (before AddRecord) â€“ no UpdateRecord() needed
        private static void SetStringField(FieldDefinitions defs, Record rec, string field, string value)
        {
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                if (!def.Name.Equals(field, StringComparison.OrdinalIgnoreCase)) continue;
                MapValue mv = rec[i];     // get existing MapValue
                mv.Assign(value ?? string.Empty);
                break;
            }
        }

        // For an EXISTING record opened via GetObjectTableRecords(OpenForWrite, â€¦)
        private static void SetStringField(FieldDefinitions defs, Records recs, Record rec, string field, string value)
        {
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                if (!def.Name.Equals(field, StringComparison.OrdinalIgnoreCase)) continue;
                MapValue mv = rec[i];     // get existing MapValue
                mv.Assign(value ?? string.Empty);
                recs.UpdateRecord(rec);   // <- IMPORTANT for updates
                break;
            }
        }

        [CommandMethod("ResidenceSync", "DUMPMASTER", CommandFlags.Modal)]
        public void DumpMasterSummary()
        {
            var ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
            if (ed == null) return;
            if (!PromptZone(ed, out CoordinateZone zone)) return;

            string masterResidencesPath = GetMasterResidencesPath(zone);
            if (!File.Exists(masterResidencesPath)) { ed?.WriteMessage($"\nDUMPMASTER: Master file not found ({masterResidencesPath})."); return; }

            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(masterResidencesPath, FileOpenMode.OpenForReadAndAllShare, false, null);
                db.CloseInput(true);

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);

                    int nInserts = 0, nPoints = 0;
                    double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
                    Action<Point3d> acc = p => { if (p.X < minx) minx = p.X; if (p.Y < miny) miny = p.Y; if (p.X > maxx) maxx = p.X; if (p.Y > maxy) maxy = p.Y; };

                    foreach (ObjectId id in ms)
                    {
                        var e = tr.GetObject(id, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead) as Entity;
                        if (e is BlockReference br) { nInserts++; acc(br.Position); }
                        else if (e is DBPoint dp) { nPoints++; acc(dp.Position); }
                    }
                    tr.Commit();

                    ed?.WriteMessage($"\nDUMPMASTER (Zone {FormatZoneNumber(zone)}): INSERTs={nInserts}, POINTs={nPoints}, Extents=[{minx:0.###},{miny:0.###}]â€“[{maxx:0.###},{maxy:0.###}]");
                }
            }
        }
        [CommandMethod("ResidenceSync", "DUMPMASTERPLUS", CommandFlags.Modal)]
        public void DumpMasterPlus()
        {
            var ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
            if (ed == null) return;
            if (!PromptZone(ed, out CoordinateZone zone)) return;

            string masterResidencesPath = GetMasterResidencesPath(zone);
            if (!File.Exists(masterResidencesPath)) { ed?.WriteMessage($"\nDUMPMASTER+: Master file not found ({masterResidencesPath})."); return; }

            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(masterResidencesPath, FileOpenMode.OpenForReadAndAllShare, false, null);
                db.CloseInput(true);

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, DbOpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], DbOpenMode.ForRead);

                    var byName = new Dictionary<string, (int count, List<Point3d> samples)>(StringComparer.OrdinalIgnoreCase);
                    int nPoints = 0;

                    foreach (ObjectId id in ms)
                    {
                        if (!(tr.GetObject(id, DbOpenMode.ForRead) is Entity e)) continue;

                        if (e is BlockReference br)
                        {
                            ObjectId btrId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                            var btr = (BlockTableRecord)tr.GetObject(btrId, DbOpenMode.ForRead);
                            string name = btr?.Name ?? "<null>";

                            if (!byName.TryGetValue(name, out var tup))
                                tup = (0, new List<Point3d>(3));
                            tup.count++;
                            if (tup.samples.Count < 3) tup.samples.Add(br.Position);
                            byName[name] = tup;
                        }
                        else if (e is DBPoint)
                        {
                            nPoints++;
                        }
                    }

                    tr.Commit();

                    if (byName.Count == 0 && nPoints == 0)
                    {
                        ed?.WriteMessage("\nDUMPMASTER+: No INSERTs or DBPOINTs found.");
                        return;
                    }

                    ed?.WriteMessage($"\nDUMPMASTER+ (Zone {FormatZoneNumber(zone)}):");
                    foreach (var kv in byName.OrderBy(k => k.Key))
                    {
                        var smp = kv.Value.samples.Select(p => $"({p.X:0.###},{p.Y:0.###})");
                        ed?.WriteMessage($"\n  {kv.Key}: {kv.Value.count}  samples: {string.Join(", ", smp)}");
                    }
                    if (nPoints > 0)
                        ed?.WriteMessage($"\n  <DBPOINT>: {nPoints}");
                }
            }
        }
        // =========================================================================
        // Helpers (layers, labels, chains, JSONL, geometry)
        // =========================================================================

        private static bool IsPolylineEntity(Entity entity)
            => entity is Polyline || entity is Polyline2d || entity is Polyline3d;

        private List<string> BuildOdTableSearchOrder(Tables tables)
        {
            var names = new List<string>();
            if (tables == null) return names;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(PREFERRED_OD_TABLE) && tables.IsTableDefined(PREFERRED_OD_TABLE))
            {
                names.Add(PREFERRED_OD_TABLE);
                seen.Add(PREFERRED_OD_TABLE);
            }

            foreach (string name in tables.GetTableNames())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (seen.Add(name)) names.Add(name);
            }
            return names;
        }

        private static Document GetOpenDocumentByPath(DocumentCollection docs, string fullPath)
        {
            if (docs == null || string.IsNullOrWhiteSpace(fullPath)) return null;
            string target = NormalizePath(fullPath);

            foreach (Document d in docs)
            {
                if (d == null) continue;

                // Prefer the database filename (full path). Fallback to Name only if it is rooted.
                string candidate = null;
                try
                {
                    candidate = d.Database?.Filename;
                    if (!string.IsNullOrWhiteSpace(candidate))
                        candidate = NormalizePath(candidate);
                }
                catch { /* ignore */ }

                if (string.IsNullOrWhiteSpace(candidate))
                {
                    try
                    {
                        string n = d.Name;
                        if (!string.IsNullOrWhiteSpace(n) && Path.IsPathRooted(n))
                            candidate = NormalizePath(n);
                    }
                    catch { /* ignore */ }
                }

                if (!string.IsNullOrWhiteSpace(candidate) &&
                    string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path); } catch { return path; }
        }

        private bool TryGetEntityAabb(Database db, ObjectId id, Transaction tr, out Aabb2d aabb)
        {
            aabb = default;
            try
            {
                var ent = tr.GetObject(id, DbOpenMode.ForRead) as Entity;
                if (ent == null) return false;

                try
                {
                    var ext = ent.GeometricExtents; // robust to rotation, bulges
                    aabb = new Aabb2d(ext.MinPoint.X, ext.MinPoint.Y, ext.MaxPoint.X, ext.MaxPoint.Y);
                    return true;
                }
                catch
                {
                    // Fallback: vertex sweep
                    double minX = double.MaxValue, minY = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue;

                    if (ent is Polyline pl)
                    {
                        for (int i = 0; i < pl.NumberOfVertices; i++)
                        {
                            var p = pl.GetPoint3dAt(i);
                            if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                            if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
                        }
                    }
                    else if (ent is Polyline2d pl2)
                    {
                        foreach (ObjectId vId in pl2)
                        {
                            var v = (Vertex2d)tr.GetObject(vId, DbOpenMode.ForRead);
                            var p = v.Position;
                            if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                            if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
                        }
                    }
                    else if (ent is Polyline3d pl3)
                    {
                        foreach (ObjectId vId in pl3)
                        {
                            var v = (PolylineVertex3d)tr.GetObject(vId, DbOpenMode.ForRead);
                            var p = new Point3d(v.Position.X, v.Position.Y, 0);
                            if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                            if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
                        }
                    }
                    else
                    {
                        return false;
                    }

                    if (minX == double.MaxValue) return false;
                    aabb = new Aabb2d(minX, minY, maxX, maxY);
                    return true;
                }
            }
            catch { return false; }
        }

        private static void EnsurePointStyleVisible()
        {
            try
            {
                AcadApp.SetSystemVariable("PDMODE", 3);
                AcadApp.SetSystemVariable("PDSIZE", 0.8);
            }
            catch { /* ignore if locked */ }
        }

        private void EnsureLayer(Database db, string layerName, Transaction tr)
        {
            var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, DbOpenMode.ForRead);
            if (!layerTable.Has(layerName))
            {
                layerTable.UpgradeOpen();
                var layerRecord = new LayerTableRecord { Name = layerName };
                layerTable.Add(layerRecord);
                tr.AddNewlyCreatedDBObject(layerRecord, true);
            }
        }

        // Force a layer's ACI color
        private void SetLayerColor(Database db, string layerName, short aci, Transaction tr)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, DbOpenMode.ForRead);
            if (!lt.Has(layerName)) return;
            var ltr = (LayerTableRecord)tr.GetObject(lt[layerName], DbOpenMode.ForWrite);
            ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                Autodesk.AutoCAD.Colors.ColorMethod.ByAci, aci);
        }

        // Masked label (MText with background fill)
        private void AddMaskedLabel(BlockTableRecord ms, Transaction tr,
                                    Point3d pos, string text, double height, string layer)
        {
            var mt = new MText
            {
                Location = pos,
                Attachment = AttachmentPoint.MiddleCenter,
                TextHeight = height,
                Contents = text,
                Layer = layer,
                ColorIndex = 256   // ByLayer for S-7
            };
            mt.BackgroundFill = true;
            mt.UseBackgroundColor = true;
            mt.BackgroundScaleFactor = 1.25;

            ms.AppendEntity(mt);
            tr.AddNewlyCreatedDBObject(mt, true);
        }

        // Create (or normalize) a layer and return its ObjectId.
        // Ensures: ON, THAWED, UNLOCKED, PLOT = true. Name is case-insensitive.
        private static ObjectId ForceLayerVisible(Database db, string layerName, Transaction tr)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, DbOpenMode.ForRead);

            ObjectId lid;
            if (!lt.Has(layerName))
            {
                lt.UpgradeOpen();
                var ltr = new LayerTableRecord { Name = layerName };
                // Make it visible & plottable
                ltr.IsOff = false;
                ltr.IsFrozen = false;
                ltr.IsLocked = false;
                ltr.IsPlottable = true;

                lid = lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }
            else
            {
                lid = lt[layerName];
                var ltr = (LayerTableRecord)tr.GetObject(lid, DbOpenMode.ForWrite);
                ltr.IsOff = false;
                ltr.IsFrozen = false;
                ltr.IsLocked = false;
                ltr.IsPlottable = true;
            }
            return lid;
        }

        // Returns all OD table names in this drawing that look like our residence tables
        // Returns OD tables that either match our canonical names OR
        // define at least one of the fields we care about (JOB_NUM/DESCRIPTION/NOTES).
        private static List<string> GetExistingResidenceOdTables(Tables tables)
        {
            var outNames = new List<string>();
            if (tables == null) return outNames;

            // 1) Exact preferred names first (colon form)
            foreach (var p in new[] { "Block:res_other", "Block:res_occ", "Block:res_abd" })
                if (tables.IsTableDefined(p)) outNames.Add(p);

            // 2) Fallback: colonless variants if present
            foreach (var q in new[] { "Blockres_other", "Blockres_occ", "Blockres_abd" })
                if (tables.IsTableDefined(q) && !outNames.Contains(q, StringComparer.OrdinalIgnoreCase))
                    outNames.Add(q);

            // 3) Any other table that happens to carry our fields (rare)
            foreach (string tn in tables.GetTableNames())
            {
                if (outNames.Contains(tn, StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    using (var t = tables[tn])
                    {
                        var defs = t.FieldDefinitions;
                        for (int i = 0; i < defs.Count; i++)
                        {
                            var name = defs[i]?.Name;
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            if (JOB_ALIASES.Concat(DESC_ALIASES).Concat(NOTES_ALIASES)
                                .Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)))
                            { outNames.Add(tn); break; }
                        }
                    }
                }
                catch { /* ignore */ }
            }
            return outNames;
        }


        // ---------- Side-chain selection (robust) ----------

        private struct EdgeInfo
        {
            public int Index;           // segment index (start vertex index)
            public Point3d A, B, Mid;   // endpoints and midpoint
            public Vector3d U;          // unit direction
            public double Len;          // length
        }

        private struct ChainInfo
        {
            public int Start;           // starting segment index
            public int SegCount;        // number of contiguous segments
            public double Score;        // average projection onto the orthogonal axis (north/east)
            public double TotalLen;     // total length of the chain
        }

        // Build contiguous chains by whichever axis an edge is closer to (no hard angle cutoff).
        private static List<ChainInfo> BuildChainsClosest(List<EdgeInfo> edges, Vector3d primary, Vector3d other)
        {
            var chains = new List<ChainInfo>();
            bool inChain = false;
            int start = -1;
            double sumProj = 0.0;
            int cnt = 0;
            double totLen = 0.0;

            for (int i = 0; i < edges.Count; i++)
            {
                var e = edges[i];
                double de = Math.Abs(e.U.DotProduct(primary));
                double dn = Math.Abs(e.U.DotProduct(other));
                bool isPrimary = de >= dn;

                if (isPrimary)
                {
                    if (!inChain) { inChain = true; start = e.Index; sumProj = 0.0; cnt = 0; totLen = 0.0; }
                    sumProj += (e.Mid - Point3d.Origin).DotProduct(other);
                    cnt++;
                    totLen += e.Len;
                }
                else
                {
                    if (inChain)
                    {
                        chains.Add(new ChainInfo { Start = start, SegCount = cnt, Score = (cnt > 0 ? sumProj / cnt : 0.0), TotalLen = totLen });
                        inChain = false;
                    }
                }
            }
            if (inChain)
                chains.Add(new ChainInfo { Start = start, SegCount = cnt, Score = (cnt > 0 ? sumProj / cnt : 0.0), TotalLen = totLen });

            // Merge wrap-around contiguous chains (last + first)
            if (chains.Count >= 2)
            {
                var first = chains[0];
                var last = chains[chains.Count - 1];
                if (first.Start == 0 && (last.Start + last.SegCount == edges.Count))
                {
                    int totalSeg = last.SegCount + first.SegCount;
                    double totalLen = last.TotalLen + first.TotalLen;
                    double avgScore = (totalSeg > 0) ? (last.Score * last.SegCount + first.Score * first.SegCount) / totalSeg : 0.0;
                    var merged = new ChainInfo { Start = last.Start, SegCount = totalSeg, Score = avgScore, TotalLen = totalLen };
                    chains[0] = merged;
                    chains.RemoveAt(chains.Count - 1);
                }
            }
            return chains;
        }

        // Helper: iterate vertex indices across a chain (m segments â†’ m+1 vertices)
        // ---- helpers used by the anchor picker ----
        private static IEnumerable<int> ChainVertexIndices(ChainInfo ch, int vertexCount)
        {
            for (int k = 0; k <= ch.SegCount; k++)
                yield return (ch.Start + k) % vertexCount;
        }
        private static double AxisProj(Point3d p, Vector3d axis)
        {
            return (p - Point3d.Origin).DotProduct(axis);
        }

        // Pick the median existing vertex in a chain w.r.t. a given axis (E for top/bottom).
        private static Point3d ChainMedianByAxis(List<Point3d> verts, ChainInfo ch, Vector3d axis)
        {
            var idxs = ChainVertexIndices(ch, verts.Count)
                       .OrderBy(i => AxisProj(verts[i], axis))
                       .ToList();
            return verts[idxs[idxs.Count / 2]];
        }

        // Pick the existing vertex in a chain whose projection on `axis` is nearest to `target`.
        private static Point3d ChainVertexNearestAxisValue(List<Point3d> verts, ChainInfo ch, Vector3d axis, double target)
        {
            int bestIdx = (ch.Start) % verts.Count;
            double best = double.MaxValue;

            foreach (int i in ChainVertexIndices(ch, verts.Count))
            {
                double d = Math.Abs(AxisProj(verts[i], axis) - target);
                if (d < best) { best = d; bestIdx = i; }
            }
            return verts[bestIdx];
        }

        // Convenience: set JOB_NUM always, and DESCRIPTION/NOTES when provided.
        // Writes to common tag variants so different block definitions are covered.


        // Returns the middle existing vertex (median-by-coordinate) from each true side chain.
        // Returns the middle existing vertex (median-by-coordinate) from each true side chain.
        // Returns the middle existing vertex (median-by-coordinate) from each true side chain.
        // Hardened against odd shapes and spiky outlines; falls back to oriented-box cross if needed.
        // Robust quarter-line anchors.
        // Top/Bottom choose the vertex closest to horizontal mid-span (E-target), not median index.
        // Left/Right already use nearest to N-target. Falls back to oriented-box cross if off-centre.
        private static bool TryGetQuarterAnchorsByEdgeMedianVertexChain(
            List<Point3d> verts,
            out Point3d topV, out Point3d bottomV, out Point3d leftV, out Point3d rightV)
        {
            topV = bottomV = leftV = rightV = Point3d.Origin;
            if (verts == null || verts.Count < 3) return false;

            // Build edges
            int n = verts.Count;
            var edges = new List<EdgeInfo>(n);
            for (int i = 0; i < n; i++)
            {
                Point3d a = verts[i];
                Point3d b = verts[(i + 1) % n];
                Vector3d v = b - a;
                double len = v.Length;
                if (len <= 1e-9) continue;
                Vector3d u = new Vector3d(v.X / len, v.Y / len, 0);
                Point3d mid = new Point3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, 0);
                edges.Add(new EdgeInfo { Index = i, A = a, B = b, U = u, Mid = mid, Len = len });
            }
            if (edges.Count == 0) return false;

            // Choose a near-horizontal edge with highest average Y; fallback = longest edge.
            const double degTol = 12.0;
            double cosTol = Math.Cos(degTol * Math.PI / 180.0);
            EdgeInfo topEdge = default(EdgeInfo);
            double bestTopY = double.MinValue;
            foreach (var e in edges)
            {
                double horiz = Math.Abs(e.U.DotProduct(Vector3d.XAxis));
                double avgY = (e.A.Y + e.B.Y) * 0.5;
                if (horiz >= cosTol && avgY > bestTopY) { bestTopY = avgY; topEdge = e; }
            }
            if (bestTopY == double.MinValue)
                topEdge = edges.OrderByDescending(e => e.Len).First();

            // Local axes
            Vector3d east = topEdge.U.GetNormal();
            if (east.Length <= 1e-12) return false;
            Vector3d north = east.RotateBy(Math.PI / 2.0, Vector3d.ZAxis).GetNormal();

            // Extents
            double minE = double.MaxValue, maxE = double.MinValue;
            double minN = double.MaxValue, maxN = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                Vector3d dp = verts[i] - Point3d.Origin;
                double pe = dp.DotProduct(east);
                double pn = dp.DotProduct(north);
                if (pe < minE) minE = pe; if (pe > maxE) maxE = pe;
                if (pn < minN) minN = pn; if (pn > maxN) maxN = pn;
            }
            double spanE = Math.Max(1e-6, maxE - minE);
            double spanN = Math.Max(1e-6, maxN - minN);

            double bandTol = Math.Max(5.0, 0.01 * Math.Max(spanE, spanN)); // wider band

            // Chains
            var eChains = BuildChainsClosest(edges, east, north); // top/bottom
            var nChains = BuildChainsClosest(edges, north, east); // left/right
            if (eChains.Count == 0 || nChains.Count == 0) return false;

            bool TouchesTop(ChainInfo ch)
            {
                foreach (int idx in ChainVertexIndices(ch, n))
                    if (maxN - AxisProj(verts[idx], north) <= bandTol) return true;
                return false;
            }
            bool TouchesBottom(ChainInfo ch)
            {
                foreach (int idx in ChainVertexIndices(ch, n))
                    if (AxisProj(verts[idx], north) - minN <= bandTol) return true;
                return false;
            }
            bool TouchesLeft(ChainInfo ch)
            {
                foreach (int idx in ChainVertexIndices(ch, n))
                    if (AxisProj(verts[idx], east) - minE <= bandTol) return true;
                return false;
            }
            bool TouchesRight(ChainInfo ch)
            {
                foreach (int idx in ChainVertexIndices(ch, n))
                    if (maxE - AxisProj(verts[idx], east) <= bandTol) return true;
                return false;
            }

            ChainInfo PickTop(IEnumerable<ChainInfo> list) => list.OrderByDescending(c => c.Score).ThenByDescending(c => c.TotalLen).First();
            ChainInfo PickBottom(IEnumerable<ChainInfo> list) => list.OrderBy(c => c.Score).ThenByDescending(c => c.TotalLen).First();

            var top = eChains.Where(TouchesTop).DefaultIfEmpty(eChains.OrderByDescending(c => c.Score).First()).First();
            var bottom = eChains.Where(TouchesBottom).DefaultIfEmpty(eChains.OrderBy(c => c.Score).First()).First();
            var left = nChains.Where(TouchesLeft).DefaultIfEmpty(nChains.OrderBy(c => c.Score).First()).First();
            var right = nChains.Where(TouchesRight).DefaultIfEmpty(nChains.OrderByDescending(c => c.Score).First()).First();

            // Use nearest-to-mid-span anchors (fixes wrong vertex picks)
            double Etarget = 0.5 * (minE + maxE);
            topV = ChainVertexNearestAxisValue(verts, top, east, Etarget);
            bottomV = ChainVertexNearestAxisValue(verts, bottom, east, Etarget);

            double Ntarget = 0.5 * (minN + maxN);
            leftV = ChainVertexNearestAxisValue(verts, left, north, Ntarget);
            rightV = ChainVertexNearestAxisValue(verts, right, north, Ntarget);

            // Sanity fallback
            double Emid = 0.5 * (AxisProj(leftV, east) + AxisProj(rightV, east));
            double Nmid = 0.5 * (AxisProj(topV, north) + AxisProj(bottomV, north));
            if (Math.Abs(Emid - 0.5 * (minE + maxE)) > 0.25 * spanE ||
                Math.Abs(Nmid - 0.5 * (minN + maxN)) > 0.25 * spanN)
            {
                Point3d FromEN(double E, double N) =>
                    new Point3d(east.X * E + north.X * N, east.Y * E + north.Y * N, 0);
                topV = FromEN(0.5 * (minE + maxE), maxN);
                bottomV = FromEN(0.5 * (minE + maxE), minN);
                leftV = FromEN(minE, 0.5 * (minN + maxN));
                rightV = FromEN(maxE, 0.5 * (minN + maxN));
            }
            return true;
        }

        // Effective (base) name for dynamic or regular blocks.
        private static string GetEffectiveBlockName(BlockReference br, Transaction tr)
        {
            ObjectId btrId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            var btr = (BlockTableRecord)tr.GetObject(btrId, DbOpenMode.ForRead);
            return btr?.Name ?? string.Empty;
        }

        // Pick up master residence blocks whose insertion point lies inside any of the polygons.
        private ObjectIdCollection CollectResidenceSourceIds(Database masterDb, List<List<Point3d>> sectionPolys)
        {
            var outIds = new ObjectIdCollection();
            if (sectionPolys == null || sectionPolys.Count == 0) return outIds;

            using (var tr = masterDb.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(masterDb.BlockTableId, DbOpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], DbOpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    Entity e = tr.GetObject(id, DbOpenMode.ForRead) as Entity;
                    if (e == null) continue;
                    Point3d pos;
                    if (e is BlockReference br)
                    {
                        string name = GetEffectiveBlockName(br, tr);
                        if (!RES_BLOCK_NAMES.Contains(name)) continue;
                        pos = br.Position;
                    }
                    else if (e is DBPoint dp)
                    {
                        pos = dp.Position;
                    }
                    else continue;

                    // Inside any of the requested section polygons?
                    foreach (var poly in sectionPolys)
                    {
                        if (PointInPolygon2D(poly, pos.X, pos.Y))
                        {
                            outIds.Add(id);
                            break;
                        }
                    }
                }
                tr.Commit();
            }
            return outIds;
        }

        // Build the same mapping transform used by the outlines: scale about center, then shift.
        // IMPORTANT: Order matters. Right-most applies first. We want Scale then Displacement,
        // so the matrix product must be postShift * scaleAboutCenter.
        // Build the same mapping transform used by the outlines: scale about center, then shift.
        // IMPORTANT: matrix composition order â€” right-most is applied first.
        // Build the mapping transform for ALL geometry (outlines, Q-lines, labels, residences).
        // Right-most matrix applies first: ScaleAbout(center) then Displacement.
        // Build the mapping transform used by SURFDEV for ALL geometry.
        // Desired mapping: p' = insertCenter + s * (p - centerMm)
        // Matrix composition (right-most applies first):
        // p' = Displacement(insertCenter - centerMm)  *  ScaleAbout(centerMm, s)  *  p
        private static Matrix3d BuildMasterToSketchTransform(Point3d centerMm, Point3d insertCenter, double unitsPerMetre)
        {
            // Scale about the master centre
            var scaleAboutCenter = Matrix3d.Scaling(unitsPerMetre, centerMm);

            // IMPORTANT: displacement is (insertCenter - centerMm), NOT (insertCenter - s*centerMm)
            var postShift = Matrix3d.Displacement(insertCenter - centerMm);

            // Apply scale first, then shift
            return postShift * scaleAboutCenter;
        }

        private struct VertexIndexRecord
        {
            public CoordinateZone zone;
            public string sec, twp, rge, mer;
            public (double MinX, double MinY, double MaxX, double MaxY) aabb;
            public List<Point3d> verts;
            public bool closed;
        }

        private bool TryReadSectionFromJsonl(string jsonlPath, SectionKey key, out VertexIndexRecord rec)
        {
            rec = default;
            var ic = CultureInfo.InvariantCulture;

            using (var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!TryParseJsonLine(line, out var r)) continue;

                    if (r.zone == key.Zone &&
                        EqNorm(r.sec, key.Section) &&
                        EqNorm(r.twp, key.Township) &&
                        EqNorm(r.rge, key.Range) &&
                        EqNorm(r.mer, key.Meridian))
                    {
                        rec = r;
                        return true;
                    }
                }
            }
            return false;

            bool TryParseJsonLine(string s, out VertexIndexRecord r)
            {
                r = default;
                try
                {
                    string GetStr(string tag)
                    {
                        int idxTag = s.IndexOf($"\"{tag}\"", StringComparison.OrdinalIgnoreCase);
                        if (idxTag < 0) return null;
                        int colon = s.IndexOf(':', idxTag); if (colon < 0) return null;
                        int q1 = s.IndexOf('\"', colon + 1); if (q1 < 0) return null;
                        int q2 = s.IndexOf('\"', q1 + 1); if (q2 < 0) return null;
                        return s.Substring(q1 + 1, q2 - q1 - 1);
                    }

                    string sec = GetStr("SEC");
                    string twp = GetStr("TWP");
                    string rge = GetStr("RGE");
                    string mer = GetStr("MER");

                    string normSec = NormStr(sec);
                    string normTwp = NormStr(twp);
                    string normRge = NormStr(rge);
                    string normMer = NormStr(mer);

                    double zoneValue = ExtractNumberAfter(s, "\"ZONE\"");
                    CoordinateZone zone = CoordinateZone.Zone11;
                    if (TryConvertToZone((int)Math.Round(zoneValue), out var parsedZone))
                    {
                        zone = parsedZone;
                    }
                    else if (MeridianMatchesZone(CoordinateZone.Zone12, normMer))
                    {
                        zone = CoordinateZone.Zone12;
                    }

                    double minx = ExtractNumberAfter(s, "\"minx\"");
                    double miny = ExtractNumberAfter(s, "\"miny\"");
                    double maxx = ExtractNumberAfter(s, "\"maxx\"");
                    double maxy = ExtractNumberAfter(s, "\"maxy\"");

                    bool closed = s.IndexOf("\"Closed\":true", StringComparison.OrdinalIgnoreCase) >= 0;

                    int iv = s.IndexOf("\"Verts\":[", StringComparison.OrdinalIgnoreCase);
                    if (iv < 0) return false;
                    int start = s.IndexOf('[', iv + 8);
                    int end = s.LastIndexOf(']');
                    if (start < 0 || end < 0 || end <= start) return false;

                    var verts = new List<Point3d>();
                    int idx = start + 1;
                    while (idx < end)
                    {
                        int a = s.IndexOf('[', idx);
                        if (a < 0 || a > end) break;
                        int b = s.IndexOf(']', a);
                        if (b < 0 || b > end) break;
                        string pair = s.Substring(a + 1, b - a - 1);
                        int comma = pair.IndexOf(',');
                        if (comma > 0)
                        {
                            string sx = pair.Substring(0, comma).Trim();
                            string sy = pair.Substring(comma + 1).Trim();
                            if (double.TryParse(sx, NumberStyles.Float, ic, out double x) &&
                                double.TryParse(sy, NumberStyles.Float, ic, out double y))
                            {
                                verts.Add(new Point3d(x, y, 0));
                            }
                        }
                        idx = b + 1; // advance
                    }

                    r = new VertexIndexRecord
                    {
                        zone = zone,
                        sec = normSec,
                        twp = normTwp,
                        rge = normRge,
                        mer = normMer,
                        aabb = (minx, miny, maxx, maxy),
                        verts = verts,
                        closed = closed
                    };
                    return (verts.Count >= 3);
                }
                catch { return false; }
            }

            double ExtractNumberAfter(string s2, string tag)
            {
                int posTag = s2.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                if (posTag < 0) return 0;
                int colon = s2.IndexOf(':', posTag);
                if (colon < 0) return 0;
                int startNum = colon + 1;
                while (startNum < s2.Length && char.IsWhiteSpace(s2[startNum])) startNum++;
                int endNum = startNum;
                while (endNum < s2.Length &&
                       (char.IsDigit(s2[endNum]) || s2[endNum] == '.' || s2[endNum] == '-' ||
                        s2[endNum] == 'e' || s2[endNum] == 'E' || s2[endNum] == '+'))
                {
                    endNum++;
                }
                double v;
                if (double.TryParse(s2.Substring(startNum, endNum - startNum), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out v))
                    return v;
                return 0;
            }
        }



        // -------- ObjectData read helpers & string normalizers --------

        private static string ReadOd(FieldDefinitions defs, Record rec, string[] aliases, Func<MapValue, string> toString)
        {
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                if (def == null) continue;
                if (!aliases.Any(a => a.Equals(def.Name, StringComparison.OrdinalIgnoreCase))) continue;

                MapValue mv;
                try { mv = rec[i]; } catch { continue; }
                string s = toString(mv);
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            return null;
        }

        private string MapValueToString(MapValue mv)
        {
            if (mv == null) return null;
            switch (mv.Type)
            {
                case OdDataType.Character: return mv.StrValue;
                case OdDataType.Integer: return mv.Int32Value.ToString(CultureInfo.InvariantCulture);
                case OdDataType.Real: return mv.DoubleValue.ToString("0.####", CultureInfo.InvariantCulture);
                default: return null;
            }
        }

        private static string NormStr(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            string trimmed = s.Trim();

            // Prefer numeric content even when prefixed (e.g., "SEC-23", "TWP-031", "MER-5")
            string digitsOnly = new string(trimmed.Where(char.IsDigit).ToArray());
            if (!string.IsNullOrEmpty(digitsOnly) && int.TryParse(digitsOnly, out int digitValue))
            {
                return digitValue.ToString(CultureInfo.InvariantCulture);
            }

            if (int.TryParse(trimmed, out int n)) return n.ToString(CultureInfo.InvariantCulture);
            string noZeros = trimmed.TrimStart('0');
            return noZeros.Length > 0 ? noZeros : "0";
        }

        private static bool EqNorm(string a, string b)
        {
            if (int.TryParse(a?.Trim(), out int ai) && int.TryParse(b?.Trim(), out int bi)) return ai == bi;
            string aa = NormStr(a);
            string bb = NormStr(b);
            if (int.TryParse(aa, out ai) && int.TryParse(bb, out bi)) return ai == bi;
            return string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool MeridianMatchesZone(CoordinateZone zone, string merValue)
        {
            if (string.IsNullOrWhiteSpace(merValue)) return false;
            string normalized = NormStr(merValue);
            if (!int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mer)) return false;

            switch (zone)
            {
                case CoordinateZone.Zone11:
                    return mer == 5 || mer == 6;
                case CoordinateZone.Zone12:
                    return mer == 4;
                default:
                    return false;
            }
        }

        // --------- User prompts & master points reader ---------

        private bool PromptSectionKey(Editor ed, out SectionKey key)
        {
            key = default;

            if (!PromptZone(ed, out CoordinateZone zone)) return false;

            string sec = PromptString(ed, "Enter SEC: ");
            if (sec == null) return false;

            string twp = PromptString(ed, "Enter TWP: ");
            if (twp == null) return false;

            string rge = PromptString(ed, "Enter RGE: ");
            if (rge == null) return false;

            string mer = PromptString(ed, "Enter MER: ");
            if (mer == null) return false;

            key = new SectionKey(zone, sec, twp, rge, mer);
            return true;
        }

        private bool PromptZone(Editor ed, out CoordinateZone zone)
        {
            zone = default;

            var opts = new PromptKeywordOptions("\nEnter Zone [11/12]: ") { AllowNone = false };
            opts.Keywords.Add("11");
            opts.Keywords.Add("12");
            opts.Keywords.Default = "11";

            var res = ed.GetKeywords(opts);
            if (res.Status != PromptStatus.OK) return false;

            zone = (string.Equals(res.StringResult, "12", StringComparison.OrdinalIgnoreCase))
                ? CoordinateZone.Zone12
                : CoordinateZone.Zone11;
            return true;
        }

        private string PromptString(Editor ed, string message)
        {
            var opts = new PromptStringOptions("\n" + message) { AllowSpaces = false };
            var res = ed.GetString(opts);
            return (res.Status == PromptStatus.OK) ? res.StringResult : null;
        }
        // Prefer matching BLOCKs over DBPOINTs when both are within tol.
        private static int FindNearestIdxPreferBlocks(List<(ObjectId id, Point3d pos, bool isBlock)> list, Point3d target, double tol)
        {
            double tol2 = tol * tol;

            int bestIdx = -1;
            double bestD2 = double.MaxValue;

            // Pass 1: prefer blocks
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].isBlock) continue;
                double dx = target.X - list[i].pos.X;
                double dy = target.Y - list[i].pos.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 <= tol2 && d2 < bestD2)
                {
                    bestD2 = d2; bestIdx = i;
                }
            }
            if (bestIdx >= 0) return bestIdx;

            // Pass 2: allow DBPOINTs if no blocks within tol
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].isBlock) continue;
                double dx = target.X - list[i].pos.X;
                double dy = target.Y - list[i].pos.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 <= tol2 && d2 < bestD2)
                {
                    bestD2 = d2; bestIdx = i;
                }
            }
            return bestIdx;
        }

        private List<Point3d> ReadPointsFromMaster(string masterPointsPath, out bool exists)
        {
            exists = File.Exists(masterPointsPath);
            if (!exists) return new List<Point3d>();

            var points = new List<Point3d>();
            using (var masterDb = new Database(false, true))
            {
                masterDb.ReadDwgFile(masterPointsPath, FileOpenMode.OpenForReadAndAllShare, false, null);
                masterDb.CloseInput(true);

                using (Transaction tr = masterDb.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(masterDb.BlockTableId, DbOpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], DbOpenMode.ForRead);

                    foreach (ObjectId id in ms)
                    {
                        var ent = tr.GetObject(id, DbOpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        switch (ent)
                        {
                            case DBPoint dbPoint:
                                points.Add(dbPoint.Position);
                                break;
                            case BlockReference blockRef:
                                points.Add(blockRef.Position);
                                break;
                        }
                    }

                    tr.Commit();
                }
            }
            return points;
        }
        private static void HardFlushAndSaveMaster(Document masterDoc, string path, Editor ed)
        {
            try
            {
                // Light "touch" to ensure DB is considered dirty if needed
                masterDoc.Database.TileMode = masterDoc.Database.TileMode; // noop touch

                // Removed: masterDoc.Database.Caption (doesn't exist on Database)

                var dbPath = NormalizePath(masterDoc.Database.Filename);
                var targetPath = NormalizePath(path);

                if (!string.IsNullOrWhiteSpace(targetPath) &&
                    string.Equals(dbPath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Open doc points at the same file; SaveAs to the same path to force a flush
                    masterDoc.Database.SaveAs(targetPath, DwgVersion.Current);
                    ed?.WriteMessage($"\n[Save] Database.SaveAs â†’ {targetPath}");
                }
                else
                {
                    // Fallback: close & save to the requested path
                    masterDoc.CloseAndSave(string.IsNullOrWhiteSpace(path) ? masterDoc.Database.Filename : path);
                    ed?.WriteMessage($"\n[Save] CloseAndSave â†’ {path}");
                }
            }
            catch (System.Exception ex)
            {
                ed?.WriteMessage($"\n[Save] Failed: {ex.Message}");
                throw;
            }
        }

        // --------- Geometry & utilities ---------

        // Robustly find TOP-LEFT (NW) and TOP-RIGHT (NE) corners of the master section.
        // Works even when the top edge has a slight slope and vertex spacing is irregular.
        // Robustly find TOP-LEFT (NW) and TOP-RIGHT (NE) corners of the master section.
        // Works even when the top edge has a slight slope and vertex spacing is irregular.
        private bool TryGetSectionTopCorners(List<Point3d> verts, out Point3d topLeft, out Point3d topRight)
        {
            topLeft = topRight = Point3d.Origin;
            if (verts == null || verts.Count < 3) return false;

            // Build edges
            int n = verts.Count;
            var edges = new List<EdgeInfo>(n);
            for (int i = 0; i < n; i++)
            {
                Point3d a = verts[i];
                Point3d b = verts[(i + 1) % n];
                Vector3d v = b - a;
                double len = v.Length;
                if (len <= 1e-9) continue;
                Vector3d u = new Vector3d(v.X / len, v.Y / len, 0);
                Point3d mid = new Point3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, 0);
                edges.Add(new EdgeInfo { Index = i, A = a, B = b, U = u, Mid = mid, Len = len });
            }
            if (edges.Count == 0) return false;

            // Choose a near-horizontal edge with highest average Y; fallback = longest edge.
            const double degTol = 15.0;
            double cosTol = Math.Cos(degTol * Math.PI / 180.0);
            EdgeInfo topEdge = default(EdgeInfo);
            double bestTopY = double.MinValue;
            foreach (var e in edges)
            {
                double horiz = Math.Abs(e.U.DotProduct(Vector3d.XAxis));
                double avgY = (e.A.Y + e.B.Y) * 0.5;
                if (horiz >= cosTol && avgY > bestTopY) { bestTopY = avgY; topEdge = e; }
            }
            if (bestTopY == double.MinValue)
                topEdge = edges.OrderByDescending(e => e.Len).First();

            // Local axes: EAST along the top edge, NORTH perpendicular
            Vector3d east = topEdge.U.GetNormal();
            if (east.Length <= 1e-12) return false;
            Vector3d north = east.RotateBy(Math.PI / 2.0, Vector3d.ZAxis).GetNormal();

            // Extents & band tolerance (2 m or 0.5% of the larger span)
            double minE = double.MaxValue, maxE = double.MinValue;
            double minN = double.MaxValue, maxN = double.MinValue;
            foreach (var v in verts)
            {
                double pe = AxisProj(v, east);
                double pn = AxisProj(v, north);
                if (pe < minE) minE = pe; if (pe > maxE) maxE = pe;
                if (pn < minN) minN = pn; if (pn > maxN) maxN = pn;
            }
            double spanE = Math.Max(1e-6, maxE - minE);
            double spanN = Math.Max(1e-6, maxN - minN);
            double bandTol = Math.Max(2.0, 0.005 * Math.Max(spanE, spanN));

            // Build east-west chains and pick the one touching the top band
            var eChains = BuildChainsClosest(edges, east, north);
            if (eChains.Count == 0) return false;

            bool TouchesTop(ChainInfo ch)
            {
                foreach (int idx in ChainVertexIndices(ch, n))
                    if (maxN - AxisProj(verts[idx], north) <= bandTol)
                        return true;
                return false;
            }

            ChainInfo topChain = eChains
                .Where(TouchesTop)
                .OrderByDescending(c => c.Score)       // most "northern"
                .ThenByDescending(c => c.TotalLen)     // tie-break by length
                .DefaultIfEmpty(eChains.OrderByDescending(c => c.Score).ThenByDescending(c => c.TotalLen).First())
                .First();

            // The corners are the min/max EAST vertices along that top chain
            int leftIdx = topChain.Start % n, rightIdx = leftIdx;
            double bestLeftE = double.MaxValue, bestRightE = double.MinValue;
            foreach (int idx in ChainVertexIndices(topChain, n))
            {
                double pe = AxisProj(verts[idx], east);
                if (pe < bestLeftE) { bestLeftE = pe; leftIdx = idx; }
                if (pe > bestRightE) { bestRightE = pe; rightIdx = idx; }
            }

            topLeft = verts[leftIdx];
            topRight = verts[rightIdx];
            return true;
        }

        private static Point3d TransformScaledPoint(Point3d source, Point3d localOrigin, Point3d masterOrigin, double scale, double rotation)
        {
            double dx = source.X - localOrigin.X;
            double dy = source.Y - localOrigin.Y;

            double cos = Math.Cos(rotation);
            double sin = Math.Sin(rotation);

            double mx = masterOrigin.X + (dx * cos - dy * sin) * scale;
            double my = masterOrigin.Y + (dx * sin + dy * cos) * scale;

            return new Point3d(mx, my, 0);
        }

        // Simple ray-cast point-in-polygon (2D) â€” correct even/odd test
        private static bool PointInPolygon2D(List<Point3d> poly, double x, double y)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = poly[i].X, yi = poly[i].Y;
                double xj = poly[j].X, yj = poly[j].Y;

                // Only consider edges that straddle the horizontal ray
                if ((yi > y) != (yj > y))
                {
                    // Compute the x coordinate of the intersection
                    double xint = xi + (y - yi) * (xj - xi) / (yj - yi);
                    if (x < xint) inside = !inside;
                }
            }
            return inside;
        }

        private static int FindNearIndex(List<Point3d> pts, Point3d p, double tol)
        {
            double tol2 = tol * tol;
            for (int i = 0; i < pts.Count; i++)
            {
                var q = pts[i];
                double dx = p.X - q.X, dy = p.Y - q.Y;
                if (dx * dx + dy * dy <= tol2) return i;
            }
            return -1;
        }

        private static bool HasNear(List<Point3d> pts, Point3d p, double tol)
            => FindNearIndex(pts, p, tol) >= 0;

        private static List<Point3d> DeduplicateList(List<Point3d> pts, double tol)
        {
            var outList = new List<Point3d>(pts.Count);
            foreach (var p in pts)
                if (!HasNear(outList, p, tol)) outList.Add(p);
            return outList;
        }
        // Set a single block attribute (if present) to a value.
        private static void SetBlockAttribute(BlockReference br, string tag, string value)
        {
            if (br == null || string.IsNullOrWhiteSpace(tag)) return;
            var tm = br.Database?.TransactionManager;
            if (tm == null) return;

            foreach (ObjectId attId in br.AttributeCollection)
            {
                var ar = tm.GetObject(attId, DbOpenMode.ForWrite, false) as AttributeReference;
                if (ar == null) continue;
                if (ar.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))
                {
                    ar.TextString = value ?? string.Empty;
                    // no break on purpose: some blocks might have duplicate tags we want consistent
                }
            }
        }

        // ---------------- value types ----------------

        private readonly struct SectionKey
        {
            public SectionKey(CoordinateZone zone, string sec, string twp, string rge, string mer)
            {
                Zone = zone;
                Section = (sec ?? string.Empty).Trim();
                Township = (twp ?? string.Empty).Trim();
                Range = (rge ?? string.Empty).Trim();
                Meridian = (mer ?? string.Empty).Trim();
            }

            public CoordinateZone Zone { get; }
            public string Section { get; }
            public string Township { get; }
            public string Range { get; }
            public string Meridian { get; }

            public override string ToString()
                => $"Zone {FormatZoneNumber(Zone)}, SEC {Section}, TWP {Township}, RGE {Range}, MER {Meridian}";
        }

        private readonly struct Aabb2d
        {
            public Aabb2d(double minX, double minY, double maxX, double maxY)
            {
                MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
            }
            public double MinX { get; }
            public double MinY { get; }
            public double MaxX { get; }
            public double MaxY { get; }
        }
    }
}

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Work Test 2\Desktop\SURF DEV\ResidenceSync\UI\MacroBuilder.cs
/////////////////////////////////////////////////////////////////////

namespace ResidenceSync.UI
{
    internal static class MacroBuilder
    {
        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var cleaned = value.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            return cleaned.Trim();
        }

        private static string AppendIfPresent(string macro, string value)
        {
            var sanitized = Sanitize(value);
            if (!string.IsNullOrEmpty(sanitized))
            {
                macro += sanitized + "\n";
            }

            return macro;
        }

        public static string BuildBuildSec(string zone, string sec, string twp, string rge, string mer)
        {
            var macro = "BUILDSEC\n";
            // BUILDSEC first prompts for UTM confirmation; default to "Yes" so UI macros align with prompt order.
            macro = AppendIfPresent(macro, "Yes");
            macro = AppendIfPresent(macro, zone);
            macro = AppendIfPresent(macro, sec);
            macro = AppendIfPresent(macro, twp);
            macro = AppendIfPresent(macro, rge);
            macro = AppendIfPresent(macro, mer);
            return macro.EndsWith("\n", StringComparison.Ordinal) ? macro : macro + "\n";
        }

        public static string BuildPushResS(string zone)
        {
            var macro = "PUSHRESS\n";
            macro = AppendIfPresent(macro, zone);
            return macro.EndsWith("\n", StringComparison.Ordinal) ? macro : macro + "\n";
        }

        public static string BuildSurfDev(
            string zone,
            string sec,
            string twp,
            string rge,
            string mer,
            string size,
            string scale,
            bool? isSurveyed,
            bool? insertResidences)
        {
            var macro = "SURFDEV\n";
            macro = AppendIfPresent(macro, zone);
            macro = AppendIfPresent(macro, sec);
            macro = AppendIfPresent(macro, twp);
            macro = AppendIfPresent(macro, rge);
            macro = AppendIfPresent(macro, mer);
            // SURFDEV now prompts for grid size before scale.
            macro = AppendIfPresent(macro, size);
            macro = AppendIfPresent(macro, scale);

            if (isSurveyed.HasValue)
            {
                macro += (isSurveyed.Value ? "Surveyed" : "Unsurveyed") + "\n";
            }

            if (insertResidences.HasValue)
            {
                macro += (insertResidences.Value ? "Yes" : "No") + "\n";
            }

            return macro.EndsWith("\n", StringComparison.Ordinal) ? macro : macro + "\n";
        }
    }
}

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Work Test 2\Desktop\SURF DEV\ResidenceSync\UI\RSPanel.cs
/////////////////////////////////////////////////////////////////////

namespace ResidenceSync.UI
{
    public partial class RSPanel : UserControl
    {
        public RSPanel()
        {
            InitializeComponent();
            NormalizeTableChildren(); // ensure no child forces row growth
            InitializeZoneOptions();
            InitializeGridSizeOptions();
            InitializeScaleOptions();
            InitializeSurveyedOptions();
            InitializeInsertResidencesOptions();
            LoadUserSettings();
            InitializeQuickInsertButtons();
        }

        private void RSPanel_Load(object sender, EventArgs e)
        {
            // keep status row collapsed unless you set text/Visible=true later
            if (this.tableLayoutMain.RowStyles.Count >= 3)
            {
                this.tableLayoutMain.RowStyles[2].SizeType = System.Windows.Forms.SizeType.Absolute;
                this.tableLayoutMain.RowStyles[2].Height = 0F;
            }
        }

        private void NormalizeTableChildren()
        {
            foreach (Control c in tableSurfDev.Controls)
            {
                c.AutoSize = false;
                c.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; // not Bottom
                c.Margin = new Padding(3);
                if (c is TextBox tb) tb.Multiline = false;
            }
        }


        // RSPanel.cs â€“ prompt user before sending macro
        private void btnBuildSection_Click(object sender, EventArgs e)
        {
            SaveUserSettings();

            // Confirm whether the user is in UTM
            var result = MessageBox.Show(
                "Are you in UTM?",
                "Confirm UTM",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            // If the user clicks â€œNoâ€, cancel the operation
            if (result != DialogResult.Yes)
            {
                SetStatus("Section build cancelled â€“ not in UTM.");
                return;
            }

            // User confirmed theyâ€™re in UTM â€“ proceed with macro
            var macro = MacroBuilder.BuildBuildSec(
                GetZoneSelectionValue(),
                GetTextValue(textSection),
                GetTextValue(textTownship),
                GetTextValue(textRange),
                GetTextValue(textMeridian));

            SendMacro(macro);
        }

        private void btnPushResidences_Click(object sender, EventArgs e)
        {
            SaveUserSettings();
            var macro = MacroBuilder.BuildPushResS(GetZoneSelectionValue());
            SendMacro(macro);
        }

        private void btnBuildSurface_Click(object sender, EventArgs e)
        {
            SaveUserSettings();
            var macro = MacroBuilder.BuildSurfDev(
                GetZoneSelectionValue(),
                GetTextValue(textSection),
                GetTextValue(textTownship),
                GetTextValue(textRange),
                GetTextValue(textMeridian),
                GetSurfaceSizeSelection(),
                GetScaleSelection(),
                GetSurveyedSelection(),
                GetInsertResidencesSelection());
            SendMacro(macro);
        }

        private void SendMacro(string macro)
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    SetStatus("No active document.");
                    return;
                }

                doc.SendStringToExecute(macro, true, false, false);
                SetStatus($"Sent: {macro.Replace("\n", "\\n")}");
            }
            catch (System.Exception ex)
            {
                SetStatus($"Error: {ex.Message}");
            }
        }

        private void SetStatus(string message)
        {
            lblStatus.Text = message;
        }

        private string GetSurfaceSizeSelection()
        {
            if (comboGridSize.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
            {
                return selected;
            }

            return "5x5";
        }

        private string GetScaleSelection()
        {
            if (comboScale.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
            {
                return selected;
            }

            return null;
        }

        private bool? GetSurveyedSelection()
        {
            if (comboSurveyed.SelectedItem is string selected)
            {
                return string.Equals(selected, "Surveyed", StringComparison.OrdinalIgnoreCase);
            }

            return null;
        }

        private bool? GetInsertResidencesSelection()
        {
            if (comboInsertResidences.SelectedItem is string selected)
            {
                return string.Equals(selected, "Yes", StringComparison.OrdinalIgnoreCase);
            }

            return null;
        }

        private string GetTextValue(TextBox textBox)
        {
            var value = textBox.Text?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private void LoadUserSettings()
        {
            var settings = Settings.Default;
            SelectZoneValue(settings.SectionKeyZone ?? "11");
            textSection.Text = settings.SectionKeySec ?? string.Empty;
            textTownship.Text = settings.SectionKeyTwp ?? string.Empty;
            textRange.Text = settings.SectionKeyRge ?? string.Empty;
            textMeridian.Text = settings.SectionKeyMer ?? string.Empty;

            var savedSize = settings.SurfDevSize;
            var gridIndex = -1;
            if (!string.IsNullOrWhiteSpace(savedSize))
            {
                gridIndex = comboGridSize.FindStringExact(savedSize);
            }

            if (gridIndex >= 0)
            {
                comboGridSize.SelectedIndex = gridIndex;
            }
            else
            {
                comboGridSize.SelectedIndex = comboGridSize.FindStringExact("5x5");
            }

            var savedScale = settings.SurfDevScale;
            if (!string.IsNullOrWhiteSpace(savedScale))
            {
                SelectComboValue(comboScale, savedScale);
            }
            else
            {
                SelectComboValue(comboScale, null);
            }

            var savedSurveyed = settings.SurfDevSurveyed;
            if (savedSurveyed.HasValue)
            {
                SelectComboValue(comboSurveyed, savedSurveyed.Value ? "Surveyed" : "Unsurveyed");
            }
            else
            {
                SelectComboValue(comboSurveyed, null);
            }

            var savedInsert = settings.SurfDevInsertResidences;
            if (savedInsert.HasValue)
            {
                SelectComboValue(comboInsertResidences, savedInsert.Value ? "Yes" : "No");
            }
            else
            {
                SelectComboValue(comboInsertResidences, "No");
            }
        }

        private void SaveUserSettings()
        {
            var settings = Settings.Default;
            settings.SectionKeyZone = GetZoneSelectionValue() ?? "11";
            settings.SectionKeySec = textSection.Text?.Trim();
            settings.SectionKeyTwp = textTownship.Text?.Trim();
            settings.SectionKeyRge = textRange.Text?.Trim();
            settings.SectionKeyMer = textMeridian.Text?.Trim();
            settings.SurfDevSize = GetSurfaceSizeSelection();
            settings.SurfDevScale = GetScaleSelection() ?? string.Empty;
            settings.SurfDevSurveyed = GetSurveyedSelection();
            settings.SurfDevInsertResidences = GetInsertResidencesSelection();
            settings.Save();
        }

        private void InitializeGridSizeOptions()
        {
            comboGridSize.Items.Clear();
            comboGridSize.Items.AddRange(new object[]
            {
                "3x3",
                "5x5",
                "7x7",
                "9x9"
            });

            if (comboGridSize.SelectedIndex < 0)
            {
                comboGridSize.SelectedIndex = comboGridSize.FindStringExact("5x5");
            }
        }

        private void InitializeScaleOptions()
        {
            comboScale.Items.Clear();
            comboScale.Items.AddRange(new object[]
            {
                "50k",
                "30k",
                "25k",
                "20k"
            });

            SelectComboValue(comboScale, "50k");
        }

        private void InitializeSurveyedOptions()
        {
            comboSurveyed.Items.Clear();
            comboSurveyed.Items.AddRange(new object[]
            {
                "Surveyed",
                "Unsurveyed"
            });

            SelectComboValue(comboSurveyed, "Surveyed");
        }

        private void InitializeInsertResidencesOptions()
        {
            comboInsertResidences.Items.Clear();
            comboInsertResidences.Items.AddRange(new object[]
            {
                "Yes",
                "No"
            });

            SelectComboValue(comboInsertResidences, "No");
        }

        private void InitializeZoneOptions()
        {
            comboZone.Items.Clear();
            comboZone.Items.Add(new ZoneOption("Zone 11", "11"));
            comboZone.Items.Add(new ZoneOption("Zone 12", "12"));

            if (comboZone.Items.Count > 0)
            {
                comboZone.SelectedIndex = 0;
            }
        }

        private void SelectComboValue(ComboBox comboBox, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                if (comboBox.Items.Count > 0)
                {
                    comboBox.SelectedIndex = 0;
                }
                else
                {
                    comboBox.SelectedIndex = -1;
                }
                return;
            }

            var index = comboBox.FindStringExact(value);
            if (index >= 0)
            {
                comboBox.SelectedIndex = index;
            }
            else if (comboBox.Items.Count > 0 && comboBox.SelectedIndex < 0)
            {
                comboBox.SelectedIndex = 0;
            }
        }

        private void labelSurveyed_Click(object sender, EventArgs e)
        {

        }

        private void tableSurfDev_Paint(object sender, PaintEventArgs e)
        {

        }

        private string GetZoneSelectionValue()
        {
            if (comboZone.SelectedItem is ZoneOption option)
            {
                return option.Value;
            }

            if (comboZone.SelectedItem is string text)
            {
                return ExtractDigits(text);
            }

            return "11";
        }

        private void SelectZoneValue(string zoneValue)
        {
            if (string.IsNullOrWhiteSpace(zoneValue))
            {
                if (comboZone.Items.Count > 0)
                {
                    comboZone.SelectedIndex = 0;
                }
                return;
            }

            for (int i = 0; i < comboZone.Items.Count; i++)
            {
                if (comboZone.Items[i] is ZoneOption option &&
                    string.Equals(option.Value, zoneValue, StringComparison.OrdinalIgnoreCase))
                {
                    comboZone.SelectedIndex = i;
                    return;
                }
            }

            if (comboZone.Items.Count > 0)
            {
                comboZone.SelectedIndex = 0;
            }
        }

        private string ExtractDigits(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            var digits = new string(input.Where(char.IsDigit).ToArray());
            return string.IsNullOrEmpty(digits) ? null : digits;
        }

        private sealed class ZoneOption
        {
            public ZoneOption(string display, string value)
            {
                Display = display;
                Value = value;
            }

            public string Display { get; }
            public string Value { get; }

            public override string ToString() => Display;
        }

        private ContextMenuStrip BuildMacroMenu((string label, string macro)[] macros)
        {
            var menu = new ContextMenuStrip();
            foreach (var (label, macro) in macros)
            {
                var item = new ToolStripMenuItem(label);
                var macroToRun = macro;
                item.Click += (sender, e) => RunInsertMacro(macroToRun);
                menu.Items.Add(item);
            }

            return menu;
        }

        private void AttachMenu(Button button, ContextMenuStrip menu)
        {
            button.Click += (sender, e) => menu.Show(button, new Point(0, button.Height));
        }

        private void RunInsertMacro(string macro)
        {
            SendMacro(macro + "\n");
        }

        private void InitializeQuickInsertButtons()
        {
            const string arrow = " â–¾";

            string BuildInsertMacro(string blockName) =>
                $"^C^C(progn (command \"-LAYER\" \"S\" \"0\" \"\") (InsertBlock1 \"{blockName}\"))";

            var btnFreeholdRadius = new Button
            {
                Name = "btnFreeholdRadius",
                Text = "Freehold Radius" + arrow,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill
            };

            var freeholdMenu = BuildMacroMenu(new[]
            {
                ("Freehold Radius", BuildInsertMacro("blk_surf_dev_freehold"))
            });
            AttachMenu(btnFreeholdRadius, freeholdMenu);

            var btnExtentFabric = new Button
            {
                Name = "btnExtentFabric",
                Text = "Extent Fabric" + arrow,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill
            };

            var extentMenu = BuildMacroMenu(new[]
            {
                ("50k Surveyed Ext.", BuildInsertMacro("50000_surv_fabric")),
                ("50k Unsurveyed Ext.", BuildInsertMacro("50000_ut_fabric"))
            });
            AttachMenu(btnExtentFabric, extentMenu);

            var btnTownshipFabric = new Button
            {
                Name = "btnTownshipFabric",
                Text = "Township Fabric" + arrow,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill
            };

            var townshipMenu = BuildMacroMenu(new[]
            {
                ("50k Surveyed", BuildInsertMacro("fabric_twp50000")),
                ("20k Surveyed", BuildInsertMacro("fabric_twp20000")),
                ("25k Surveyed", BuildInsertMacro("fabric_twp25000")),
                ("30k Surveyed", BuildInsertMacro("fabric_twp30000"))
            });
            AttachMenu(btnTownshipFabric, townshipMenu);

            var btnRadiusCircles = new Button
            {
                Name = "btnRadiusCircles",
                Text = "Radius Circles" + arrow,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill
            };

            var radiusMenu = BuildMacroMenu(new[]
            {
                ("50k Radius Circle", BuildInsertMacro("blk_50000_rad_circles")),
                ("20k Radius Circle", BuildInsertMacro("blk_20000_rad_circles")),
                ("25k Radius Circle", BuildInsertMacro("blk_25000_rad_circles")),
                ("30k Radius Circle", BuildInsertMacro("blk_30000_rad_circles"))
            });
            AttachMenu(btnRadiusCircles, radiusMenu);

            tableQuickButtons.Controls.Add(btnFreeholdRadius, 0, 0);
            tableQuickButtons.Controls.Add(btnExtentFabric, 1, 0);
            tableQuickButtons.Controls.Add(btnTownshipFabric, 2, 0);
            tableQuickButtons.Controls.Add(btnRadiusCircles, 3, 0);
        }
    }
}

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Work Test 2\Desktop\SURF DEV\ResidenceSync\UI\RSUiCommands.cs
/////////////////////////////////////////////////////////////////////

// ResidenceSync UI Palette
// How to compile: Build the ResidenceSync solution in Visual Studio targeting .NET Framework 4.8.
// How to load: NETLOAD â†’ pick ResidenceSync.dll.
// How to open the UI: run command RSUI.
// Usage notes: The palette only sends command macros; any required picks or prompts continue in AutoCAD.

namespace ResidenceSync.UI
{
    public class RSUiCommands : IExtensionApplication
    {
        private static PaletteSet _paletteSet;
        private static RSPanel _panel;
        private static readonly object _syncRoot = new object();
        private static readonly System.Guid PaletteGuid = new System.Guid("2F0D4F71-7A8C-4C44-9F2C-A0A5D5C9E51E");

        [CommandMethod("ResidenceSync", "RSUI", CommandFlags.Modal)]
        public void ShowResidenceSyncPalette()
        {
            EnsurePalette();

            if (_paletteSet != null)
            {
                if (!_paletteSet.Visible)
                {
                    _paletteSet.Visible = true;
                }

                _paletteSet.KeepFocus = false;
                _paletteSet.Activate(0);
            }
        }

        private static void EnsurePalette()
        {
            if (_paletteSet != null)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_paletteSet != null)
                {
                    return;
                }

                _panel = new RSPanel
                {
                    Dock = DockStyle.Fill
                };

                _paletteSet = new PaletteSet("ResidenceSync UI", PaletteGuid)
                {
                    Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowTabForSingle | PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.Snappable,
                    Size = new Size(360, 520),
                    KeepFocus = false
                };

                _paletteSet.MinimumSize = new Size(320, 360);
                _paletteSet.DockEnabled = DockSides.Left | DockSides.Right | DockSides.Bottom;
                _paletteSet.EnableTransparency(false);
                _paletteSet.Add("ResidenceSync", _panel);
                _paletteSet.Visible = true;
            }
        }

        public void Initialize()
        {
        }

        public void Terminate()
        {
            if (_paletteSet != null)
            {
                _paletteSet.Visible = false;
                _paletteSet.Dispose();
                _paletteSet = null;
                _panel = null;
            }
        }
    }
}

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\SectionIndexReader.cs
/////////////////////////////////////////////////////////////////////

/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public readonly struct SectionKey
    {
        public SectionKey(int zone, string section, string township, string range, string meridian)
        {
            Zone = zone;
            Section = section;
            Township = township;
            Range = range;
            Meridian = meridian;
        }

        public int Zone { get; }
        public string Section { get; }
        public string Township { get; }
        public string Range { get; }
        public string Meridian { get; }
    }

    public sealed class SectionOutline
    {
        public SectionOutline(List<Point2d> vertices, bool closed, string sourcePath)
        {
            Vertices = vertices;
            Closed = closed;
            SourcePath = sourcePath;
        }

        public List<Point2d> Vertices { get; }
        public bool Closed { get; }
        public string SourcePath { get; }
    }

    public static class SectionIndexReader
    {
        public static bool TryLoadSectionOutline(string baseFolder, SectionKey key, Logger logger, out SectionOutline outline)
        {
            outline = null;

            var jsonlPath = GetIndexPath(baseFolder, key.Zone, ".jsonl");
            if (!string.IsNullOrWhiteSpace(jsonlPath) && File.Exists(jsonlPath))
            {
                if (TryReadFromJsonl(jsonlPath, key, out outline))
                {
                    return true;
                }

                logger.WriteLine("Section not found in JSONL index: " + jsonlPath);
            }

            var csvPath = GetIndexPath(baseFolder, key.Zone, ".csv");
            if (!string.IsNullOrWhiteSpace(csvPath) && File.Exists(csvPath))
            {
                if (TryReadFromCsv(csvPath, key, out outline))
                {
                    return true;
                }

                logger.WriteLine("Section not found in CSV index: " + csvPath);
            }

            logger.WriteLine("Section index not found or missing section entry for zone " + key.Zone + ".");
            return false;
        }

        private static string? GetIndexPath(string baseFolder, int zone, string extension)
        {
            var preferred = Path.Combine(baseFolder, $"Master_Sections.index_Z{zone}{extension}");
            if (File.Exists(preferred))
            {
                return preferred;
            }

            var fallback = Path.Combine(baseFolder, $"Master_Sections.index{extension}");
            if (File.Exists(fallback))
            {
                return fallback;
            }

            return preferred;
        }

        private static bool TryReadFromJsonl(string path, SectionKey key, out SectionOutline outline)
        {
            outline = null;
            var keySection = NormalizeKey(key.Section);
            var keyTownship = NormalizeKey(key.Township);
            var keyRange = NormalizeKey(key.Range);
            var keyMeridian = NormalizeKey(key.Meridian);

            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (!TryParseJsonLine(line, out var record))
                {
                    continue;
                }

                if (record.Zone != key.Zone)
                {
                    continue;
                }

                if (!KeyEquals(record.Section, keySection) ||
                    !KeyEquals(record.Township, keyTownship) ||
                    !KeyEquals(record.Range, keyRange) ||
                    !KeyEquals(record.Meridian, keyMeridian))
                {
                    continue;
                }

                outline = new SectionOutline(record.Vertices, record.Closed, path);
                return true;
            }

            return false;
        }

        private static bool TryReadFromCsv(string path, SectionKey key, out SectionOutline outline)
        {
            outline = null;
            var keySection = NormalizeKey(key.Section);
            var keyTownship = NormalizeKey(key.Township);
            var keyRange = NormalizeKey(key.Range);
            var keyMeridian = NormalizeKey(key.Meridian);

            using (var reader = new StreamReader(path))
            {
                var headerLine = reader.ReadLine();
                if (headerLine == null)
                {
                    return false;
                }

                var headers = ParseCsvLine(headerLine);
                var indices = BuildHeaderIndex(headers);
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var columns = ParseCsvLine(line);
                    if (!TryGetColumn(columns, indices, "ZONE", out var zoneValue) ||
                        !int.TryParse(NormalizeKey(zoneValue), NumberStyles.Integer, CultureInfo.InvariantCulture, out var zone) ||
                        zone != key.Zone)
                    {
                        continue;
                    }

                    if (!TryGetColumn(columns, indices, "SEC", out var secValue) ||
                        !TryGetColumn(columns, indices, "TWP", out var twpValue) ||
                        !TryGetColumn(columns, indices, "RGE", out var rgeValue) ||
                        !TryGetColumn(columns, indices, "MER", out var merValue))
                    {
                        continue;
                    }

                    if (!KeyEquals(NormalizeKey(secValue), keySection) ||
                        !KeyEquals(NormalizeKey(twpValue), keyTownship) ||
                        !KeyEquals(NormalizeKey(rgeValue), keyRange) ||
                        !KeyEquals(NormalizeKey(merValue), keyMeridian))
                    {
                        continue;
                    }

                    if (!TryGetDouble(columns, indices, "MINX", out var minX) ||
                        !TryGetDouble(columns, indices, "MINY", out var minY) ||
                        !TryGetDouble(columns, indices, "MAXX", out var maxX) ||
                        !TryGetDouble(columns, indices, "MAXY", out var maxY))
                    {
                        continue;
                    }

                    var vertices = new List<Point2d>
                    {
                        new Point2d(minX, minY),
                        new Point2d(maxX, minY),
                        new Point2d(maxX, maxY),
                        new Point2d(minX, maxY)
                    };

                    outline = new SectionOutline(vertices, true, path);
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseJsonLine(string line, out SectionRecord record)
        {
            record = default;
            try
            {
                using (var doc = JsonDocument.Parse(line))
                {
                    var root = doc.RootElement;
                    if (!TryGetProperty(root, "ZONE", out var zoneElement) &&
                        !TryGetProperty(root, "zone", out zoneElement))
                    {
                        return false;
                    }

                    if (!TryReadInt(zoneElement, out var zone))
                    {
                        return false;
                    }

                    if (!TryGetProperty(root, "SEC", out var secElement) ||
                        !TryGetProperty(root, "TWP", out var twpElement) ||
                        !TryGetProperty(root, "RGE", out var rgeElement) ||
                        !TryGetProperty(root, "MER", out var merElement))
                    {
                        return false;
                    }

                    var vertices = new List<Point2d>();
                    if (TryGetProperty(root, "Verts", out var vertsElement) && vertsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var pair in vertsElement.EnumerateArray())
                        {
                            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2)
                            {
                                continue;
                            }

                            if (TryReadDouble(pair[0], out var x) && TryReadDouble(pair[1], out var y))
                            {
                                vertices.Add(new Point2d(x, y));
                            }
                        }
                    }

                    if (vertices.Count == 0 &&
                        TryGetProperty(root, "minx", out var minXElement) &&
                        TryGetProperty(root, "miny", out var minYElement) &&
                        TryGetProperty(root, "maxx", out var maxXElement) &&
                        TryGetProperty(root, "maxy", out var maxYElement) &&
                        TryReadDouble(minXElement, out var minX) &&
                        TryReadDouble(minYElement, out var minY) &&
                        TryReadDouble(maxXElement, out var maxX) &&
                        TryReadDouble(maxYElement, out var maxY))
                    {
                        vertices.Add(new Point2d(minX, minY));
                        vertices.Add(new Point2d(maxX, minY));
                        vertices.Add(new Point2d(maxX, maxY));
                        vertices.Add(new Point2d(minX, maxY));
                    }

                    if (vertices.Count < 3)
                    {
                        return false;
                    }

                    var closed = true;
                    if (TryGetProperty(root, "Closed", out var closedElement) && closedElement.ValueKind == JsonValueKind.True)
                    {
                        closed = true;
                    }
                    else if (TryGetProperty(root, "Closed", out closedElement) && closedElement.ValueKind == JsonValueKind.False)
                    {
                        closed = false;
                    }

                    record = new SectionRecord
                    {
                        Zone = zone,
                        Section = NormalizeKey(secElement.GetString()),
                        Township = NormalizeKey(twpElement.GetString()),
                        Range = NormalizeKey(rgeElement.GetString()),
                        Meridian = NormalizeKey(merElement.GetString()),
                        Vertices = vertices,
                        Closed = closed
                    };
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static bool TryReadInt(JsonElement element, out int value)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.String &&
                int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            value = 0;
            return false;
        }

        private static bool TryReadDouble(JsonElement element, out double value)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.String &&
                double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            value = 0;
            return false;
        }

        private static Dictionary<string, int> BuildHeaderIndex(IReadOnlyList<string> headers)
        {
            var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count; i++)
            {
                var normalized = headers[i].Trim();
                if (!lookup.ContainsKey(normalized))
                {
                    lookup.Add(normalized, i);
                }
            }

            return lookup;
        }

        private static bool TryGetColumn(IReadOnlyList<string> columns, Dictionary<string, int> indices, string name, out string value)
        {
            value = string.Empty;
            if (!indices.TryGetValue(name, out var index))
            {
                return false;
            }

            if (index < 0 || index >= columns.Count)
            {
                return false;
            }

            value = columns[index];
            return true;
        }

        private static bool TryGetDouble(IReadOnlyList<string> columns, Dictionary<string, int> indices, string name, out double value)
        {
            value = 0;
            if (!TryGetColumn(columns, indices, name, out var raw))
            {
                return false;
            }

            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(line))
            {
                return result;
            }

            var current = new StringBuilder();
            var inQuotes = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            result.Add(current.ToString());
            return result;
        }

        private static string NormalizeKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            var digits = new StringBuilder();
            foreach (var ch in trimmed)
            {
                if (char.IsDigit(ch))
                {
                    digits.Append(ch);
                }
            }

            if (digits.Length > 0 && int.TryParse(digits.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                return numeric.ToString(CultureInfo.InvariantCulture);
            }

            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                return numeric.ToString(CultureInfo.InvariantCulture);
            }

            return trimmed.TrimStart('0');
        }

        private static bool KeyEquals(string a, string b)
        {
            if (int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ai) &&
                int.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bi))
            {
                return ai == bi;
            }

            return string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private struct SectionRecord
        {
            public int Zone;
            public string Section;
            public string Township;
            public string Range;
            public string Meridian;
            public List<Point2d> Vertices;
            public bool Closed;
        }
    }
}

/////////////////////////////////////////////////////////////////////

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\ShapefileImporter.cs
/////////////////////////////////////////////////////////////////////

/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public sealed class ShapefileImportSummary
    {
        public int ImportedDispositions { get; set; }
        public int FilteredDispositions { get; set; }
        public int DedupedDispositions { get; set; }
        public int ImportFailures { get; set; }
    }

    public static class ShapefileImporter
    {
        private static readonly string[] MapImportTypeNames =
        {
            "Autodesk.Gis.Map.ImportExport.MapImport",
            "Autodesk.Gis.Map.Platform.ImportExport.MapImport"
        };

        private static readonly string[] MapImportAssemblyNames =
        {
            "ManagedMapApi",
            "AcMapImportExport",
            "AcMapMgd"
        };

        private static readonly Lazy<Type?> MapImportType = new Lazy<Type?>(ResolveMapImportType);

        public static ShapefileImportSummary ImportShapefiles(
            Database database,
            Editor editor,
            Logger logger,
            Config config,
            IReadOnlyList<ObjectId> sectionPolylineIds,
            List<ObjectId> dispositionPolylines)
        {
            var summary = new ShapefileImportSummary();

            if (!config.ImportDispositionShapefiles)
            {
                logger.WriteLine("Disposition shapefile import disabled. Skipping shapefile import.");
                return summary;
            }

            var dispositionFiles = config.DispositionShapefileNames ?? Array.Empty<string>();
            if (dispositionFiles.Length == 0)
            {
                logger.WriteLine("No disposition shapefiles configured.");
                return summary;
            }

            var sectionExtents = BuildSectionBufferExtents(database, sectionPolylineIds, config.SectionBufferDistance);
            if (sectionExtents.Count == 0)
            {
                logger.WriteLine("No section extents available for shapefile filtering.");
                return summary;
            }

            var existingKeys = BuildExistingFeatureKeys(database, logger);
            var existingIds = CapturePolylineIds(database);
            var searchFolders = BuildShapefileSearchFolders(config);
            logger.WriteLine($"Shapefile search folders: {string.Join("; ", searchFolders)}");
            logger.WriteLine($"Section extents loaded: {sectionExtents.Count} (buffer {config.SectionBufferDistance}).");

            if (!TryGetMapImportType(logger, out var mapImportType))
            {
                summary.ImportFailures += dispositionFiles.Length;
                return summary;
            }

            foreach (var shapefile in dispositionFiles)
            {
                logger.WriteLine($"Resolving shapefile: {shapefile}");
                var shapefilePath = ResolveShapefilePath(searchFolders, shapefile);
                if (string.IsNullOrWhiteSpace(shapefilePath))
                {
                    logger.WriteLine($"Shapefile missing: {shapefile}. Searched: {string.Join("; ", searchFolders)}");
                    summary.ImportFailures++;
                    continue;
                }

                logger.WriteLine($"Using shapefile: {shapefilePath}");
                LogShapefileSidecars(shapefilePath, logger);

                logger.WriteLine("Starting shapefile import.");
                if (!TryImportShapefile(mapImportType, shapefilePath, logger))
                {
                    logger.WriteLine("Shapefile import failed.");
                    summary.ImportFailures++;
                    continue;
                }

                var newIds = CaptureNewPolylineIds(database, existingIds);
                existingIds.UnionWith(newIds);
                logger.WriteLine($"Shapefile import produced {newIds.Count} new polylines.");

                if (newIds.Count == 0)
                {
                    logger.WriteLine("No new LWPOLYLINE entities detected after import.");
                    continue;
                }

                FilterAndCollect(database, logger, newIds, sectionExtents, existingKeys, dispositionPolylines, summary, Path.GetFileName(shapefilePath));
            }

            summary.ImportedDispositions = dispositionPolylines.Count;
            editor.WriteMessage($"\nImported {summary.ImportedDispositions} dispositions from shapefiles.");
            return summary;
        }

        private static bool TryGetMapImportType(Logger logger, out Type mapImportType)
        {
            mapImportType = MapImportType.Value;
            if (mapImportType == null)
            {
                logger.WriteLine("MapImport type not found. Ensure Map 3D is available before importing shapefiles.");
                logger.WriteLine($"Loaded assemblies: {string.Join(", ", AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name))}");
                logger.WriteLine("Skipping shapefile import for this run.");
                return false;
            }

            return true;
        }

        private static Type? ResolveMapImportType()
        {
            foreach (var typeName in MapImportTypeNames)
            {
                var resolved = Type.GetType(typeName);
                if (resolved != null)
                {
                    return resolved;
                }

                foreach (var assemblyName in MapImportAssemblyNames)
                {
                    resolved = Type.GetType($"{typeName}, {assemblyName}");
                    if (resolved != null)
                    {
                        return resolved;
                    }
                }
            }

            foreach (var assemblyName in MapImportAssemblyNames)
            {
                TryLoadAssembly(assemblyName);
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var typeName in MapImportTypeNames)
                {
                    var resolved = assembly.GetType(typeName, false);
                    if (resolved != null)
                    {
                        return resolved;
                    }
                }
            }

            return null;
        }

        private static void TryLoadAssembly(string assemblyName)
        {
            try
            {
                Assembly.Load(assemblyName);
            }
            catch
            {
                // ignored
            }
        }

        private static IReadOnlyList<string> BuildShapefileSearchFolders(Config config)
        {
            var folders = new List<string>();
            AddFolder(folders, config.ShapefileFolder);
            AddFolder(folders, new Config().ShapefileFolder);
            AddFolder(folders, Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory);
            return folders;
        }

        private static void AddFolder(List<string> folders, string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(folder);
            }
        }

        private static string? ResolveShapefilePath(IReadOnlyList<string> folders, string shapefile)
        {
            foreach (var folder in folders)
            {
                var candidate = Path.Combine(folder, shapefile);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static HashSet<ObjectId> CapturePolylineIds(Database database)
        {
            var ids = new HashSet<ObjectId>();
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    if (id.ObjectClass.DxfName != "LWPOLYLINE")
                    {
                        continue;
                    }

                    ids.Add(id);
                }

                transaction.Commit();
            }

            return ids;
        }

        private static List<ObjectId> CaptureNewPolylineIds(Database database, HashSet<ObjectId> existingIds)
        {
            var newIds = new List<ObjectId>();
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    if (id.ObjectClass.DxfName != "LWPOLYLINE")
                    {
                        continue;
                    }

                    if (!existingIds.Contains(id))
                    {
                        newIds.Add(id);
                    }
                }

                transaction.Commit();
            }

            return newIds;
        }

        private static List<Extents2d> BuildSectionBufferExtents(Database database, IReadOnlyList<ObjectId> sectionPolylineIds, double buffer)
        {
            var extents = new List<Extents2d>();
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                foreach (var id in sectionPolylineIds)
                {
                    var polyline = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (polyline == null)
                    {
                        continue;
                    }

                    var polylineExtents = polyline.GeometricExtents;
                    var bufferExtents = new Extents2d(
                        new Point2d(polylineExtents.MinPoint.X - buffer, polylineExtents.MinPoint.Y - buffer),
                        new Point2d(polylineExtents.MaxPoint.X + buffer, polylineExtents.MaxPoint.Y + buffer));
                    extents.Add(bufferExtents);
                }

                transaction.Commit();
            }

            return extents;
        }

        private static HashSet<string> BuildExistingFeatureKeys(Database database, Logger logger)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    var polyline = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (polyline == null)
                    {
                        continue;
                    }

                    var key = BuildFeatureKey(polyline, id, logger);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        keys.Add(key);
                    }
                }

                transaction.Commit();
            }

            return keys;
        }

        private static void FilterAndCollect(
            Database database,
            Logger logger,
            List<ObjectId> newIds,
            List<Extents2d> sectionExtents,
            HashSet<string> existingKeys,
            List<ObjectId> dispositionPolylines,
            ShapefileImportSummary summary,
            string shapefileName)
        {
            var filteredStart = summary.FilteredDispositions;
            var dedupedStart = summary.DedupedDispositions;
            var acceptedStart = dispositionPolylines.Count;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                foreach (var id in newIds)
                {
                    var polyline = transaction.GetObject(id, OpenMode.ForWrite) as Polyline;
                    if (polyline == null || !polyline.Closed)
                    {
                        summary.FilteredDispositions++;
                        polyline?.Erase();
                        continue;
                    }

                    var extents = polyline.GeometricExtents;
                    var extents2d = new Extents2d(
                        new Point2d(extents.MinPoint.X, extents.MinPoint.Y),
                        new Point2d(extents.MaxPoint.X, extents.MaxPoint.Y));
                    if (!IsWithinSections(extents2d, sectionExtents))
                    {
                        summary.FilteredDispositions++;
                        polyline.Erase();
                        continue;
                    }

                    var key = BuildFeatureKey(polyline, id, logger);
                    if (!string.IsNullOrWhiteSpace(key) && existingKeys.Contains(key))
                    {
                        summary.DedupedDispositions++;
                        polyline.Erase();
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        existingKeys.Add(key);
                    }

                    dispositionPolylines.Add(id);
                }

                transaction.Commit();
            }

            var accepted = dispositionPolylines.Count - acceptedStart;
            var filtered = summary.FilteredDispositions - filteredStart;
            var deduped = summary.DedupedDispositions - dedupedStart;
            logger.WriteLine($"Shapefile '{shapefileName}' results: accepted {accepted}, filtered {filtered}, deduped {deduped}.");
        }

        private static bool IsWithinSections(Extents2d polyExtents, List<Extents2d> sectionExtents)
        {
            foreach (var sectionExtent in sectionExtents)
            {
                if (GeometryUtils.ExtentsIntersect(polyExtents, sectionExtent))
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildFeatureKey(Polyline polyline, ObjectId id, Logger logger)
        {
            var extents = polyline.GeometricExtents;
            var centerX = (extents.MinPoint.X + extents.MaxPoint.X) / 2.0;
            var centerY = (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0;
            var roundedCenter = $"{Math.Round(centerX, 2):F2},{Math.Round(centerY, 2):F2}";

            var od = OdHelpers.ReadObjectData(id, logger);
            if (od != null)
            {
                od.TryGetValue("DISP_NUM", out var dispNum);
                od.TryGetValue("COMPANY", out var company);
                od.TryGetValue("PURPCD", out var purpose);
                return $"{dispNum}|{company}|{purpose}|{roundedCenter}";
            }

            return roundedCenter;
        }

        private static bool TryImportShapefile(Type mapImportType, string shapefilePath, Logger logger)
        {
            try
            {
                var mapImport = Activator.CreateInstance(mapImportType);
                SetProperty(mapImportType, mapImport, "SourceFile", shapefilePath);
                SetProperty(mapImportType, mapImport, "FileName", shapefilePath);
                SetProperty(mapImportType, mapImport, "CreateObjectData", true);
                SetProperty(mapImportType, mapImport, "UseObjectData", true);
                SetProperty(mapImportType, mapImport, "ImportPolylines", true);
                SetProperty(mapImportType, mapImport, "ImportClosedPolylines", true);

                var initMethod = mapImportType.GetMethod("Init");
                initMethod?.Invoke(mapImport, new object[] { "MAPIMPORT" });

                if (InvokeIfExists(mapImportType, mapImport, "Import", logger))
                {
                    logger.WriteLine("MapImport.Import invoked.");
                    return true;
                }

                if (InvokeIfExists(mapImportType, mapImport, "Run", logger))
                {
                    logger.WriteLine("MapImport.Run invoked.");
                    return true;
                }

                if (InvokeIfExists(mapImportType, mapImport, "Execute", logger))
                {
                    logger.WriteLine("MapImport.Execute invoked.");
                    return true;
                }

                logger.WriteLine("MapImport executed without a valid import method.");
                return false;
            }
            catch (Exception ex)
            {
                logger.WriteLine("Shapefile import failed: " + ex.Message);
                return false;
            }
        }

        private static void SetProperty(Type type, object instance, string propertyName, object value)
        {
            var property = type.GetProperty(propertyName);
            if (property != null && property.CanWrite)
            {
                property.SetValue(instance, value);
            }
        }

        private static bool InvokeIfExists(Type type, object instance, string methodName, Logger logger)
        {
            var method = type.GetMethod(methodName, Type.EmptyTypes);
            if (method != null)
            {
                logger.WriteLine($"Invoking MapImport.{methodName}.");
                method.Invoke(instance, null);
                return true;
            }

            return false;
        }

        private static void LogShapefileSidecars(string shapefilePath, Logger logger)
        {
            var basePath = Path.Combine(
                Path.GetDirectoryName(shapefilePath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(shapefilePath));
            var required = new[] { ".shp", ".shx", ".dbf" };
            foreach (var extension in required)
            {
                var candidate = basePath + extension;
                if (!File.Exists(candidate))
                {
                    logger.WriteLine($"Missing shapefile sidecar: {candidate}");
                }
            }
        }
    }
}

