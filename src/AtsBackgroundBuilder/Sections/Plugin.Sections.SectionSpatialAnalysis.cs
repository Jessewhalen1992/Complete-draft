/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        private static bool TryGetAtsSectionGridPosition(int section, out int row, out int col)
        {
            row = -1;
            col = -1;
            if (section < 1 || section > 36)
            {
                return false;
            }

            // ATS/DLS section grid (6x6 snake pattern, north at top).
            // Alberta numbering starts at the southeast corner:
            // 6  5  4  3  2  1
            // 7  8  9 10 11 12
            // 18 17 16 15 14 13
            // 19 20 21 22 23 24
            // 30 29 28 27 26 25
            // 31 32 33 34 35 36
            var rows = new[]
            {
                new[] { 31, 32, 33, 34, 35, 36 },
                new[] { 30, 29, 28, 27, 26, 25 },
                new[] { 19, 20, 21, 22, 23, 24 },
                new[] { 18, 17, 16, 15, 14, 13 },
                new[] { 7, 8, 9, 10, 11, 12 },
                new[] { 6, 5, 4, 3, 2, 1 }
            };

            for (var r = 0; r < rows.Length; r++)
            {
                for (var c = 0; c < rows[r].Length; c++)
                {
                    if (rows[r][c] == section)
                    {
                        row = r;
                        col = c;
                        return true;
                    }
                }
            }

            return false;
        }

        private static List<LsdCellInfo> BuildSectionLsdCells(Transaction transaction, Dictionary<ObjectId, int> sectionNumberById)
        {
            var cells = new List<LsdCellInfo>();
            if (sectionNumberById == null || sectionNumberById.Count == 0)
            {
                return cells;
            }

            foreach (var pair in sectionNumberById)
            {
                if (pair.Key.IsNull || pair.Key.IsErased)
                {
                    continue;
                }

                var section = transaction.GetObject(pair.Key, OpenMode.ForRead) as Polyline;
                if (section == null || !section.Closed || section.NumberOfVertices < 3)
                {
                    continue;
                }

                BuildLsdCellsForSection(section, pair.Value, cells);
            }

            return cells;
        }

        private static void BuildLsdCellsForSection(Polyline section, int sectionNumber, List<LsdCellInfo> destination)
        {
            var outline = GetPolylinePoints(section);
            if (outline.Count < 3)
            {
                return;
            }

            QuarterAnchors anchors;
            if (!TryGetQuarterAnchors(section, out anchors))
            {
                anchors = GetFallbackAnchors(section);
            }

            var eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
            var northUnit = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));

            if (!TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.NorthWest, out var nw) ||
                !TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.NorthEast, out var ne) ||
                !TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthWest, out var sw) ||
                !TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthEast, out var se))
            {
                var ext = section.GeometricExtents;
                sw = new Point2d(ext.MinPoint.X, ext.MinPoint.Y);
                se = new Point2d(ext.MaxPoint.X, ext.MinPoint.Y);
                nw = new Point2d(ext.MinPoint.X, ext.MaxPoint.Y);
                ne = new Point2d(ext.MaxPoint.X, ext.MaxPoint.Y);
            }

            for (var row = 0; row < 4; row++)
            {
                for (var col = 0; col < 4; col++)
                {
                    var u0 = col / 4.0;
                    var u1 = (col + 1) / 4.0;
                    var t0 = row / 4.0;
                    var t1 = (row + 1) / 4.0;

                    var sample = BilinearPoint(sw, se, nw, ne, 0.5 * (u0 + u1), 0.5 * (t0 + t1));
                    var points = new List<Point2d>(outline);

                    var leftSouth = BilinearPoint(sw, se, nw, ne, u0, 0.0);
                    var leftNorth = BilinearPoint(sw, se, nw, ne, u0, 1.0);
                    var rightSouth = BilinearPoint(sw, se, nw, ne, u1, 0.0);
                    var rightNorth = BilinearPoint(sw, se, nw, ne, u1, 1.0);
                    var bottomWest = BilinearPoint(sw, se, nw, ne, 0.0, t0);
                    var bottomEast = BilinearPoint(sw, se, nw, ne, 1.0, t0);
                    var topWest = BilinearPoint(sw, se, nw, ne, 0.0, t1);
                    var topEast = BilinearPoint(sw, se, nw, ne, 1.0, t1);

                    var keepLeft = GetSideSign(leftSouth, leftNorth, sample);
                    var keepRight = GetSideSign(rightSouth, rightNorth, sample);
                    var keepBottom = GetSideSign(bottomWest, bottomEast, sample);
                    var keepTop = GetSideSign(topWest, topEast, sample);

                    if (keepLeft == 0 || keepRight == 0 || keepBottom == 0 || keepTop == 0)
                    {
                        continue;
                    }

                    points = ClipPolygon(points, leftSouth, leftNorth, keepLeft);
                    points = ClipPolygon(points, rightSouth, rightNorth, keepRight);
                    points = ClipPolygon(points, bottomWest, bottomEast, keepBottom);
                    points = ClipPolygon(points, topWest, topEast, keepTop);

                    var cell = BuildPolylineFromPoints(points);
                    if (cell == null)
                    {
                        continue;
                    }

                    var lsd = GetLsdNumber(row, col);
                    destination.Add(new LsdCellInfo(cell, lsd, sectionNumber));
                }
            }
        }

        private static int GetLsdNumber(int rowFromSouth, int colFromWest)
        {
            if ((rowFromSouth % 2) == 0)
            {
                return (rowFromSouth * 4) + (4 - colFromWest);
            }

            return (rowFromSouth * 4) + (colFromWest + 1);
        }

        private static Point2d BilinearPoint(Point2d sw, Point2d se, Point2d nw, Point2d ne, double u, double t)
        {
            var south = sw + ((se - sw) * u);
            var north = nw + ((ne - nw) * u);
            return south + ((north - south) * t);
        }

        private static string GetDominantLsdSectionToken(Polyline disposition, List<LsdCellInfo> lsdCells)
        {
            if (disposition == null || lsdCells == null || lsdCells.Count == 0)
            {
                return string.Empty;
            }

            double bestArea = 0.0;
            int bestLsd = 0;
            int bestSection = 0;

            Extents3d dispExtents;
            try
            {
                dispExtents = disposition.GeometricExtents;
            }
            catch
            {
                return string.Empty;
            }

            foreach (var cell in lsdCells)
            {
                if (cell.Cell == null)
                {
                    continue;
                }

                try
                {
                    if (!GeometryUtils.ExtentsIntersect(dispExtents, cell.Cell.GeometricExtents))
                    {
                        continue;
                    }
                }
                catch
                {
                    // ignore extents failures and try geometric intersection directly
                }

                var overlapArea = GetIntersectionArea(disposition, cell.Cell);
                if (overlapArea > bestArea)
                {
                    bestArea = overlapArea;
                    bestLsd = cell.Lsd;
                    bestSection = cell.Section;
                }
            }

            return bestArea > 1e-6 && bestLsd > 0 && bestSection > 0
                ? $"{bestLsd}-{bestSection}"
                : string.Empty;
        }

        private static List<SectionSpatialInfo> BuildSectionSpatialInfos(Transaction transaction, Dictionary<ObjectId, int> sectionNumberById)
        {
            var infos = new List<SectionSpatialInfo>();
            if (sectionNumberById == null || sectionNumberById.Count == 0)
            {
                return infos;
            }

            foreach (var pair in sectionNumberById)
            {
                if (pair.Key.IsNull || pair.Key.IsErased)
                {
                    continue;
                }

                var section = transaction.GetObject(pair.Key, OpenMode.ForRead) as Polyline;
                if (section == null || !section.Closed || section.NumberOfVertices < 3)
                {
                    continue;
                }

                if (TryCreateSectionSpatialInfo(section, pair.Value, out var info))
                {
                    infos.Add(info);
                }
            }

            return infos;
        }

        private static List<SectionSpatialInfo> LoadSupplementalSectionSpatialInfos(
            IReadOnlyList<SectionRequest> requests,
            Config config,
            Logger logger)
        {
            var infos = new List<SectionSpatialInfo>();
            if (requests == null || requests.Count == 0)
            {
                return infos;
            }

            var searchFolders = BuildSectionIndexSearchFolders(config);
            var townshipKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sectionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var request in requests)
            {
                var key = request.Key;
                var townshipKey = $"{key.Zone}|{NormalizeNumberToken(key.Meridian)}|{NormalizeNumberToken(key.Range)}|{NormalizeNumberToken(key.Township)}";
                if (!townshipKeys.Add(townshipKey))
                {
                    continue;
                }

                for (var section = 1; section <= 36; section++)
                {
                    var sectionKey = new SectionKey(
                        key.Zone,
                        section.ToString(CultureInfo.InvariantCulture),
                        key.Township,
                        key.Range,
                        key.Meridian);
                    var keyId = BuildSectionKeyId(sectionKey);
                    if (!sectionKeys.Add(keyId))
                    {
                        continue;
                    }

                    if (!TryLoadSectionOutline(searchFolders, sectionKey, logger, out var outline))
                    {
                        continue;
                    }

                    if (TryCreateSectionSpatialInfo(outline, section, out var info))
                    {
                        infos.Add(info);
                    }
                }
            }

            if (infos.Count > 0)
            {
                logger.WriteLine($"Loaded {infos.Count} supplemental section outlines for wellsite section matching.");
            }

            return infos;
        }

        private static bool TryCreateSectionSpatialInfo(
            SectionOutline outline,
            int sectionNumber,
            [NotNullWhen(true)] out SectionSpatialInfo? info)
        {
            info = null;
            if (outline == null || outline.Vertices == null || outline.Vertices.Count < 3)
            {
                return false;
            }

            var section = new Polyline(outline.Vertices.Count)
            {
                Closed = outline.Closed
            };
            for (var i = 0; i < outline.Vertices.Count; i++)
            {
                section.AddVertexAt(i, outline.Vertices[i], 0, 0, 0);
            }

            try
            {
                return TryCreateSectionSpatialInfo(section, sectionNumber, out info);
            }
            finally
            {
                section.Dispose();
            }
        }

        private static bool TryCreateSectionSpatialInfo(
            Polyline section,
            int sectionNumber,
            [NotNullWhen(true)] out SectionSpatialInfo? info)
        {
            info = null;
            var clone = section.Clone() as Polyline;
            if (clone == null)
            {
                return false;
            }

            try
            {
                QuarterAnchors anchors;
                if (!TryGetQuarterAnchors(clone, out anchors))
                {
                    anchors = GetFallbackAnchors(clone);
                }

                var eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
                var northUnit = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));

                if (!TryGetQuarterCorner(clone, eastUnit, northUnit, QuarterCorner.SouthWest, out var sw) ||
                    !TryGetQuarterCorner(clone, eastUnit, northUnit, QuarterCorner.SouthEast, out var se) ||
                    !TryGetQuarterCorner(clone, eastUnit, northUnit, QuarterCorner.NorthWest, out var nw) ||
                    !TryGetQuarterCorner(clone, eastUnit, northUnit, QuarterCorner.NorthEast, out var ne))
                {
                    var ext = clone.GeometricExtents;
                    sw = new Point2d(ext.MinPoint.X, ext.MinPoint.Y);
                    se = new Point2d(ext.MaxPoint.X, ext.MinPoint.Y);
                    nw = new Point2d(ext.MinPoint.X, ext.MaxPoint.Y);
                    ne = new Point2d(ext.MaxPoint.X, ext.MaxPoint.Y);
                }

                var width = Math.Abs((se - sw).DotProduct(eastUnit));
                var height = Math.Abs((nw - sw).DotProduct(northUnit));
                if (width <= 1e-6 || height <= 1e-6)
                {
                    clone.Dispose();
                    return false;
                }

                info = new SectionSpatialInfo(clone, sectionNumber, sw, se, nw, ne, eastUnit, northUnit, width, height);
                return true;
            }
            catch
            {
                clone.Dispose();
                return false;
            }
        }

        private static string GetDominantLsdSectionTokenBySection(Polyline disposition, List<SectionSpatialInfo> sections)
        {
            if (disposition == null || sections == null || sections.Count == 0)
            {
                return string.Empty;
            }

            SectionSpatialInfo? best = null;
            double bestArea = 0.0;

            foreach (var section in sections)
            {
                var area = GetIntersectionArea(disposition, section.SectionPolyline);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = section;
                }
            }

            if (best == null || bestArea <= 1e-6)
            {
                return string.Empty;
            }

            if (!TryGetRepresentativePointInSection(disposition, best.SectionPolyline, out var point))
            {
                return string.Empty;
            }

            var rowCol = GetLsdRowCol(point, best);
            if (rowCol == null)
            {
                return string.Empty;
            }

            var lsd = GetLsdNumber(rowCol.Value.row, rowCol.Value.col);
            return (lsd > 0 && best.Section > 0) ? $"{lsd}-{best.Section}" : string.Empty;
        }

        private static bool TryGetRepresentativePointInSection(Polyline disposition, Polyline section, out Point2d point)
        {
            point = default;
            if (disposition == null || section == null)
            {
                return false;
            }

            if (GeometryUtils.TryIntersectPolylines(disposition, section, out var pieces) && pieces.Count > 0)
            {
                Polyline? best = null;
                double bestArea = 0.0;
                foreach (var p in pieces)
                {
                    double area = 0.0;
                    try { area = Math.Abs(p.Area); } catch { }
                    if (area > bestArea)
                    {
                        best?.Dispose();
                        best = p;
                        bestArea = area;
                    }
                    else
                    {
                        p.Dispose();
                    }
                }

                if (best != null)
                {
                    try
                    {
                        point = GeometryUtils.GetSafeInteriorPoint(best);
                        return true;
                    }
                    finally
                    {
                        best.Dispose();
                    }
                }
            }

            return false;
        }

        private static (int row, int col)? GetLsdRowCol(Point2d point, SectionSpatialInfo section)
        {
            var v = point - section.SouthWest;
            var u = v.DotProduct(section.EastUnit) / section.Width;
            var t = v.DotProduct(section.NorthUnit) / section.Height;

            if (double.IsNaN(u) || double.IsInfinity(u) || double.IsNaN(t) || double.IsInfinity(t))
            {
                return null;
            }

            u = Math.Max(0.0, Math.Min(0.999999, u));
            t = Math.Max(0.0, Math.Min(0.999999, t));

            var col = (int)Math.Floor(u * 4.0);
            var row = (int)Math.Floor(t * 4.0);

            if (col < 0 || col > 3 || row < 0 || row > 3)
            {
                return null;
            }

            return (row, col);
        }

        private static string GetSectionOverlapDebugString(Polyline disposition, List<SectionSpatialInfo> sections)
        {
            if (disposition == null || sections == null || sections.Count == 0)
            {
                return "(no section infos)";
            }

            var rows = new List<(int section, double area)>();
            foreach (var section in sections)
            {
                var area = GetIntersectionArea(disposition, section.SectionPolyline);
                if (area > 1e-6)
                {
                    rows.Add((section.Section, area));
                }
            }

            if (rows.Count == 0)
            {
                return "(no overlaps > 0)";
            }

            rows.Sort((a, b) => b.area.CompareTo(a.area));
            var top = rows.Take(3).Select(r => $"SEC{r.section}={r.area:0.###}");
            return string.Join(", ", top);
        }

        private static string GetPointBasedLsdSectionToken(Polyline disposition, List<SectionSpatialInfo> sections, out string debug)
        {
            debug = "no-sections";
            if (disposition == null || sections == null || sections.Count == 0)
            {
                return string.Empty;
            }

            var samplePoints = new List<Point2d>();
            try
            {
                samplePoints.Add(GeometryUtils.GetSafeInteriorPoint(disposition));
            }
            catch
            {
                // ignore
            }

            for (var i = 0; i < disposition.NumberOfVertices; i++)
            {
                samplePoints.Add(disposition.GetPoint2dAt(i));
            }

            if (samplePoints.Count == 0)
            {
                debug = "no-samples";
                return string.Empty;
            }

            SectionSpatialInfo? bestSection = null;
            int bestHits = -1;
            Point2d bestPoint = default;

            foreach (var section in sections)
            {
                int hits = 0;
                Point2d firstHit = default;
                bool hasFirst = false;

                foreach (var p in samplePoints)
                {
                    if (GeometryUtils.IsPointInsidePolyline(section.SectionPolyline, p))
                    {
                        hits++;
                        if (!hasFirst)
                        {
                            firstHit = p;
                            hasFirst = true;
                        }
                    }
                }

                if (hits > bestHits)
                {
                    bestHits = hits;
                    bestSection = section;
                    bestPoint = hasFirst ? firstHit : samplePoints[0];
                }
            }

            if (bestSection == null || bestHits <= 0)
            {
                debug = "no-section-hit";
                return string.Empty;
            }

            var rowCol = GetLsdRowCol(bestPoint, bestSection);
            if (rowCol == null)
            {
                debug = $"section={bestSection.Section} hits={bestHits} rowcol=none";
                return string.Empty;
            }

            var lsd = GetLsdNumber(rowCol.Value.row, rowCol.Value.col);
            debug = $"section={bestSection.Section} hits={bestHits} point=({bestPoint.X:0.###},{bestPoint.Y:0.###}) row={rowCol.Value.row} col={rowCol.Value.col} lsd={lsd}";
            return (lsd > 0 && bestSection.Section > 0) ? $"{lsd}-{bestSection.Section}" : string.Empty;
        }

        private static double GetIntersectionArea(Polyline a, Polyline b)
        {
            if (a == null || b == null)
            {
                return 0.0;
            }

            if (!GeometryUtils.TryIntersectPolylines(a, b, out var pieces) || pieces == null || pieces.Count == 0)
            {
                return 0.0;
            }

            double area = 0.0;
            foreach (var piece in pieces)
            {
                try
                {
                    area += Math.Abs(piece.Area);
                }
                catch
                {
                    // ignore individual piece area failures
                }
                finally
                {
                    piece.Dispose();
                }
            }

            return area;
        }

        private static void DisposeLsdCells(List<LsdCellInfo> cells)
        {
            if (cells == null)
            {
                return;
            }

            foreach (var cell in cells)
            {
                try
                {
                    cell.Cell?.Dispose();
                }
                catch
                {
                    // ignore cleanup failures
                }
            }
        }

        private static void DisposeSectionInfos(List<SectionSpatialInfo> infos)
        {
            if (infos == null)
            {
                return;
            }

            foreach (var info in infos)
            {
                try
                {
                    info.SectionPolyline?.Dispose();
                }
                catch
                {
                    // ignore cleanup failures
                }
            }
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
    }
}

