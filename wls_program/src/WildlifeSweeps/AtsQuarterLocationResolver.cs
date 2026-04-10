using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using AtsBackgroundBuilder;
using AtsBackgroundBuilder.Sections;
using Autodesk.AutoCAD.Geometry;

namespace WildlifeSweeps
{
    internal sealed class AtsQuarterLocationResolver
    {
        private const string DefaultSectionIndexFolder = @"C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\RES MANAGER";
        private const double BoundaryToleranceMeters = 0.25;

        private readonly List<SectionSpatialFrame> _sections;

        private AtsQuarterLocationResolver(List<SectionSpatialFrame> sections)
        {
            _sections = sections;
        }

        public int SectionCount => _sections.Count;

        public readonly struct QuarterMatch
        {
            public QuarterMatch(
                string location,
                IReadOnlyList<Point2d> quarterVertices,
                string quarterToken,
                string section,
                string township,
                string range,
                string meridian)
            {
                Location = location;
                QuarterVertices = quarterVertices ?? Array.Empty<Point2d>();
                QuarterToken = quarterToken ?? string.Empty;
                Section = section ?? string.Empty;
                Township = township ?? string.Empty;
                Range = range ?? string.Empty;
                Meridian = meridian ?? string.Empty;
            }

            public string Location { get; }
            public IReadOnlyList<Point2d> QuarterVertices { get; }
            public string QuarterToken { get; }
            public string Section { get; }
            public string Township { get; }
            public string Range { get; }
            public string Meridian { get; }
        }

        public readonly struct LsdMatch
        {
            public LsdMatch(
                string location,
                int lsd,
                string quarterToken,
                string section,
                string township,
                string range,
                string meridian,
                string metes,
                string bounds,
                IReadOnlyList<Point2d> quarterVertices)
            {
                Location = location;
                Lsd = lsd;
                QuarterToken = quarterToken;
                Section = section;
                Township = township;
                Range = range;
                Meridian = meridian;
                Metes = metes;
                Bounds = bounds;
                QuarterVertices = quarterVertices ?? Array.Empty<Point2d>();
            }

            public string Location { get; }
            public int Lsd { get; }
            public string QuarterToken { get; }
            public string Section { get; }
            public string Township { get; }
            public string Range { get; }
            public string Meridian { get; }
            public string Metes { get; }
            public string Bounds { get; }
            public IReadOnlyList<Point2d> QuarterVertices { get; }
        }

        public static bool TryCreate(
            int zone,
            string? drawingPath,
            [NotNullWhen(true)] out AtsQuarterLocationResolver? resolver)
        {
            resolver = null;
            if (zone <= 0)
            {
                return false;
            }

            var logger = new Logger();
            foreach (var folder in BuildSearchFolders(drawingPath))
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                if (!SectionIndexReader.TryLoadSectionOutlinesForZone(folder, zone, logger, out var outlines) ||
                    outlines.Count == 0)
                {
                    continue;
                }

                var frames = BuildFrames(outlines);
                if (frames.Count == 0)
                {
                    continue;
                }

                resolver = new AtsQuarterLocationResolver(frames);
                return true;
            }

            return false;
        }

        public string ResolveLocation(Point2d point)
        {
            return TryResolveLsdMatch(point, out var match)
                ? match.Location
                : string.Empty;
        }

        public string ResolveQuarterLocation(Point2d point)
        {
            return TryResolveQuarterMatch(point, out var match)
                ? match.Location
                : string.Empty;
        }

        public bool TryResolveQuarterMatch(Point2d point, out QuarterMatch match)
        {
            match = default;
            if (!TryResolveSection(point, out var best))
            {
                return false;
            }

            var quarter = ResolveQuarterBounds(best, point);
            var location = $"{FormatQuarterToken(quarter.Token)} {best.Section}-{best.Township}-{best.Range}-W{best.Meridian}";
            match = new QuarterMatch(
                location,
                BuildQuarterVertices(best, quarter),
                quarter.Token,
                best.Section,
                best.Township,
                best.Range,
                best.Meridian);
            return true;
        }

        public bool TryResolveLsdMatch(Point2d point, out LsdMatch match)
        {
            match = default;
            if (!TryResolveSection(point, out var best))
            {
                return false;
            }

            var quarter = ResolveQuarterBounds(best, point);
            var lsd = ResolveLsdNumber(best, point);
            var locals = ResolveLocals(best, point);
            var location = $"{lsd}-{best.Section}-{best.Township}-{best.Range}-W{best.Meridian}";
            match = new LsdMatch(
                location,
                lsd,
                quarter.Token,
                best.Section,
                best.Township,
                best.Range,
                best.Meridian,
                locals.Metes,
                locals.Bounds,
                BuildQuarterVertices(best, quarter));
            return true;
        }

        private bool TryResolveSection(Point2d point, [NotNullWhen(true)] out SectionSpatialFrame? best)
        {
            best = null;
            var bestScore = double.NegativeInfinity;
            foreach (var section in _sections)
            {
                if (!section.Extents.Contains(point, BoundaryToleranceMeters))
                {
                    continue;
                }

                if (!IsPointInsidePolygon(section.Vertices, point, BoundaryToleranceMeters))
                {
                    continue;
                }

                var score = DistanceSqToBoundary(section.Vertices, point);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = section;
                }
            }

            if (best == null)
            {
                return false;
            }

            return true;
        }

        private static IReadOnlyList<string> BuildSearchFolders(string? drawingPath)
        {
            var folders = new List<string>();
            AddFolder(folders, Environment.GetEnvironmentVariable("WLS_SECTION_INDEX_FOLDER"));
            AddFolder(folders, Environment.GetEnvironmentVariable("ATSBUILD_SECTION_INDEX_FOLDER"));
            AddFolder(folders, Environment.GetEnvironmentVariable("ATS_SECTION_INDEX_FOLDER"));
            AddFolder(folders, TryGetDrawingFolder(drawingPath));
            AddFolder(folders, Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            AddFolder(folders, Environment.CurrentDirectory);
            AddFolder(folders, DefaultSectionIndexFolder);
            return folders;
        }

        private static string? TryGetDrawingFolder(string? drawingPath)
        {
            if (string.IsNullOrWhiteSpace(drawingPath))
            {
                return null;
            }

            try
            {
                return Path.GetDirectoryName(drawingPath);
            }
            catch
            {
                return null;
            }
        }

        private static void AddFolder(List<string> folders, string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            var trimmed = folder.Trim();
            if (!folders.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(trimmed);
            }
        }

        private static List<SectionSpatialFrame> BuildFrames(IReadOnlyList<SectionIndexReader.SectionOutlineEntry> outlines)
        {
            var frames = new List<SectionSpatialFrame>(outlines.Count);
            foreach (var outline in outlines)
            {
                if (outline?.Outline?.Vertices == null || outline.Outline.Vertices.Count < 3)
                {
                    continue;
                }

                if (!TryCreateFrame(outline, out var frame))
                {
                    continue;
                }

                frames.Add(frame);
            }

            return frames;
        }

        private static bool TryCreateFrame(
            SectionIndexReader.SectionOutlineEntry entry,
            [NotNullWhen(true)] out SectionSpatialFrame? frame)
        {
            frame = null;
            var vertices = entry.Outline.Vertices;
            if (!TryGetExtents(vertices, out var extents))
            {
                return false;
            }

            if (!TryBuildOrientation(vertices, out var southWest, out var eastUnit, out var northUnit, out var width, out var height))
            {
                southWest = new Point2d(extents.MinX, extents.MinY);
                eastUnit = new Vector2d(1, 0);
                northUnit = new Vector2d(0, 1);
                width = Math.Max(1e-6, extents.MaxX - extents.MinX);
                height = Math.Max(1e-6, extents.MaxY - extents.MinY);
            }

            frame = new SectionSpatialFrame(
                NormalizeToken(entry.Key.Section),
                NormalizeToken(entry.Key.Township),
                NormalizeToken(entry.Key.Range),
                NormalizeToken(entry.Key.Meridian),
                vertices,
                extents,
                southWest,
                eastUnit,
                northUnit,
                width,
                height);
            return true;
        }

        private static bool TryBuildOrientation(
            IReadOnlyList<Point2d> vertices,
            out Point2d southWest,
            out Vector2d eastUnit,
            out Vector2d northUnit,
            out double width,
            out double height)
        {
            return AtsPolygonFrameBuilder.TryBuildFrame(
                vertices,
                out southWest,
                out eastUnit,
                out northUnit,
                out width,
                out height);
        }

        private static bool TryGetExtents(IReadOnlyList<Point2d> vertices, out SectionExtents extents)
        {
            extents = default;
            if (vertices == null || vertices.Count == 0)
            {
                return false;
            }

            var minX = double.PositiveInfinity;
            var minY = double.PositiveInfinity;
            var maxX = double.NegativeInfinity;
            var maxY = double.NegativeInfinity;
            foreach (var vertex in vertices)
            {
                minX = Math.Min(minX, vertex.X);
                minY = Math.Min(minY, vertex.Y);
                maxX = Math.Max(maxX, vertex.X);
                maxY = Math.Max(maxY, vertex.Y);
            }

            extents = new SectionExtents(minX, minY, maxX, maxY);
            return true;
        }

        private static bool IsPointInsidePolygon(IReadOnlyList<Point2d> vertices, Point2d point, double tolerance)
        {
            if (DistanceSqToBoundary(vertices, point) <= tolerance * tolerance)
            {
                return true;
            }

            var inside = false;
            var previous = vertices[vertices.Count - 1];
            for (var i = 0; i < vertices.Count; i++)
            {
                var current = vertices[i];
                if ((previous.Y > point.Y) != (current.Y > point.Y))
                {
                    var intersectX = ((current.X - previous.X) * (point.Y - previous.Y) / (current.Y - previous.Y)) + previous.X;
                    if (point.X < intersectX)
                    {
                        inside = !inside;
                    }
                }

                previous = current;
            }

            return inside;
        }

        private static double DistanceSqToBoundary(IReadOnlyList<Point2d> vertices, Point2d point)
        {
            var minDistanceSq = double.MaxValue;
            for (var i = 0; i < vertices.Count; i++)
            {
                var start = vertices[i];
                var end = vertices[(i + 1) % vertices.Count];
                minDistanceSq = Math.Min(minDistanceSq, DistanceSqToSegment(point, start, end));
            }

            return minDistanceSq;
        }

        private static double DistanceSqToSegment(Point2d point, Point2d start, Point2d end)
        {
            var edgeX = end.X - start.X;
            var edgeY = end.Y - start.Y;
            var lengthSq = (edgeX * edgeX) + (edgeY * edgeY);
            if (lengthSq <= 0.0)
            {
                var dx = point.X - start.X;
                var dy = point.Y - start.Y;
                return (dx * dx) + (dy * dy);
            }

            var dxp = point.X - start.X;
            var dyp = point.Y - start.Y;
            var projection = (dxp * edgeX) + (dyp * edgeY);
            var t = Math.Max(0.0, Math.Min(1.0, projection / lengthSq));
            var closestX = start.X + (t * edgeX);
            var closestY = start.Y + (t * edgeY);
            var dxc = point.X - closestX;
            var dyc = point.Y - closestY;
            return (dxc * dxc) + (dyc * dyc);
        }

        private static QuarterBounds ResolveQuarterBounds(SectionSpatialFrame section, Point2d point)
        {
            var vector = point - section.SouthWest;
            var u = vector.DotProduct(section.EastUnit) / section.Width;
            var t = vector.DotProduct(section.NorthUnit) / section.Height;
            u = Math.Max(0.0, Math.Min(0.999999, u));
            t = Math.Max(0.0, Math.Min(0.999999, t));

            var east = u >= 0.5;
            var north = t >= 0.5;
            if (north && !east) return new QuarterBounds("NW", 0.0, 0.5, 0.5, 1.0);
            if (north && east) return new QuarterBounds("NE", 0.5, 1.0, 0.5, 1.0);
            if (!north && !east) return new QuarterBounds("SW", 0.0, 0.5, 0.0, 0.5);
            return new QuarterBounds("SE", 0.5, 1.0, 0.0, 0.5);
        }

        private static int ResolveLsdNumber(SectionSpatialFrame section, Point2d point)
        {
            var vector = point - section.SouthWest;
            var u = vector.DotProduct(section.EastUnit) / section.Width;
            var t = vector.DotProduct(section.NorthUnit) / section.Height;
            u = Math.Max(0.0, Math.Min(0.999999, u));
            t = Math.Max(0.0, Math.Min(0.999999, t));

            var col = (int)Math.Floor(u * 4.0);
            var row = (int)Math.Floor(t * 4.0);
            col = Math.Max(0, Math.Min(3, col));
            row = Math.Max(0, Math.Min(3, row));

            return LsdNumberingHelper.GetLsdNumber(row, col);
        }

        private static LocalOffsets ResolveLocals(SectionSpatialFrame section, Point2d point)
        {
            var vector = point - section.SouthWest;
            var easting = Math.Max(0.0, Math.Min(section.Width, vector.DotProduct(section.EastUnit) / section.EastUnit.Length));
            var northing = Math.Max(0.0, Math.Min(section.Height, vector.DotProduct(section.NorthUnit) / section.NorthUnit.Length));

            var distanceFromSouth = northing;
            var distanceFromNorth = section.Height - northing;
            var distanceFromWest = easting;
            var distanceFromEast = section.Width - easting;

            var metes = distanceFromSouth <= distanceFromNorth
                ? FormatLocalOffset(distanceFromSouth, "N")
                : FormatLocalOffset(distanceFromNorth, "S");

            var bounds = distanceFromWest <= distanceFromEast
                ? FormatLocalOffset(distanceFromWest, "E")
                : FormatLocalOffset(distanceFromEast, "W");

            return new LocalOffsets(metes, bounds);
        }

        private static string FormatLocalOffset(double distance, string direction)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{distance:0.0} {direction}");
        }

        private static IReadOnlyList<Point2d> BuildQuarterVertices(SectionSpatialFrame section, QuarterBounds quarter)
        {
            return new[]
            {
                QuarterLocalToWorld(section, quarter.UMin, quarter.TMin),
                QuarterLocalToWorld(section, quarter.UMax, quarter.TMin),
                QuarterLocalToWorld(section, quarter.UMax, quarter.TMax),
                QuarterLocalToWorld(section, quarter.UMin, quarter.TMax)
            };
        }

        private static Point2d QuarterLocalToWorld(SectionSpatialFrame section, double u, double t)
        {
            var localOffset = (section.EastUnit * (u * section.Width)) + (section.NorthUnit * (t * section.Height));
            return section.SouthWest + localOffset;
        }

        private static string NormalizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var digits = new string(value.Where(char.IsDigit).ToArray());
            if (digits.Length > 0 &&
                int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return number.ToString(CultureInfo.InvariantCulture);
            }

            return value.Trim().TrimStart('0');
        }

        private static string FormatQuarterToken(string token)
        {
            return (token ?? string.Empty).Trim().ToUpperInvariant() switch
            {
                "NW" => "N.W.",
                "NE" => "N.E.",
                "SW" => "S.W.",
                "SE" => "S.E.",
                _ => token
            };
        }

        private readonly struct QuarterBounds
        {
            public QuarterBounds(string token, double uMin, double uMax, double tMin, double tMax)
            {
                Token = token;
                UMin = uMin;
                UMax = uMax;
                TMin = tMin;
                TMax = tMax;
            }

            public string Token { get; }
            public double UMin { get; }
            public double UMax { get; }
            public double TMin { get; }
            public double TMax { get; }
        }

        private readonly struct LocalOffsets
        {
            public LocalOffsets(string metes, string bounds)
            {
                Metes = metes;
                Bounds = bounds;
            }

            public string Metes { get; }
            public string Bounds { get; }
        }

        private readonly struct SectionExtents
        {
            public SectionExtents(double minX, double minY, double maxX, double maxY)
            {
                MinX = minX;
                MinY = minY;
                MaxX = maxX;
                MaxY = maxY;
            }

            public double MinX { get; }
            public double MinY { get; }
            public double MaxX { get; }
            public double MaxY { get; }

            public bool Contains(Point2d point, double tolerance)
            {
                return point.X >= MinX - tolerance &&
                       point.X <= MaxX + tolerance &&
                       point.Y >= MinY - tolerance &&
                       point.Y <= MaxY + tolerance;
            }
        }

        private sealed class SectionSpatialFrame
        {
            public SectionSpatialFrame(
                string section,
                string township,
                string range,
                string meridian,
                IReadOnlyList<Point2d> vertices,
                SectionExtents extents,
                Point2d southWest,
                Vector2d eastUnit,
                Vector2d northUnit,
                double width,
                double height)
            {
                Section = section;
                Township = township;
                Range = range;
                Meridian = meridian;
                Vertices = vertices;
                Extents = extents;
                SouthWest = southWest;
                EastUnit = eastUnit;
                NorthUnit = northUnit;
                Width = width;
                Height = height;
            }

            public string Section { get; }
            public string Township { get; }
            public string Range { get; }
            public string Meridian { get; }
            public IReadOnlyList<Point2d> Vertices { get; }
            public SectionExtents Extents { get; }
            public Point2d SouthWest { get; }
            public Vector2d EastUnit { get; }
            public Vector2d NorthUnit { get; }
            public double Width { get; }
            public double Height { get; }
        }
    }
}
