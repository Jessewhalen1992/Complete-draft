using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using AtsBackgroundBuilder.Geometry;
using AtsBackgroundBuilder.Sections;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder.Core
{
    internal static class BoundarySectionImportService
    {
        private const double ExtentsTolerance = 1e-3;

        internal sealed class SectionGridEntry
        {
            public SectionGridEntry(string meridian, string range, string township, string section, string quarter)
            {
                Meridian = meridian ?? string.Empty;
                Range = range ?? string.Empty;
                Township = township ?? string.Empty;
                Section = section ?? string.Empty;
                Quarter = quarter ?? string.Empty;
            }

            public string Meridian { get; }
            public string Range { get; }
            public string Township { get; }
            public string Section { get; }
            public string Quarter { get; }
        }

        internal static bool TryCollectEntriesFromBoundary(
            Config config,
            int zone,
            out List<SectionGridEntry> entries,
            out string message,
            out bool cancelled,
            IntPtr hostWindowHandle)
        {
            entries = new List<SectionGridEntry>();
            message = string.Empty;
            cancelled = false;

            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                message = "No active AutoCAD document is available.";
                return false;
            }

            var editor = document.Editor;
            var database = document.Database;

            PromptEntityResult selection;
            using (var userInteraction = StartUserInteraction(editor, hostWindowHandle))
            {
                try
                {
                    var prompt = new PromptEntityOptions("\nSelect closed boundary polyline: ");
                    prompt.SetRejectMessage("\nSelected object must be a closed polyline.");
                    prompt.AddAllowedClass(typeof(Polyline), exactMatch: false);
                    selection = editor.GetEntity(prompt);
                    if (selection.Status != PromptStatus.OK)
                    {
                        cancelled = selection.Status == PromptStatus.Cancel;
                        if (!cancelled)
                        {
                            message = "Boundary selection did not complete.";
                        }

                        return false;
                    }
                }
                finally
                {
                    RefreshEditorPrompt(editor);
                }
            }

            Polyline? boundaryClone = null;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                if (!(transaction.GetObject(selection.ObjectId, OpenMode.ForRead, false) is Polyline boundary) ||
                    !IsClosedPolyline(boundary))
                {
                    message = "Selected entity must be a closed polyline with at least 3 vertices.";
                    return false;
                }

                boundaryClone = (Polyline)boundary.Clone();
                transaction.Commit();
            }

            try
            {
                return TryCollectEntriesFromSectionIndex(config, zone, boundaryClone, out entries, out message);
            }
            finally
            {
                if (boundaryClone != null)
                {
                    try
                    {
                        boundaryClone.Dispose();
                    }
                    catch
                    {
                        // best-effort dispose
                    }
                }
            }
        }

        private static IDisposable StartUserInteraction(Editor editor, IntPtr hostWindowHandle)
        {
            if (editor == null || hostWindowHandle == IntPtr.Zero)
            {
                return NullDisposable.Instance;
            }

            try
            {
                return editor.StartUserInteraction(hostWindowHandle);
            }
            catch
            {
                return NullDisposable.Instance;
            }
        }

        private sealed class NullDisposable : IDisposable
        {
            internal static readonly NullDisposable Instance = new NullDisposable();
            public void Dispose()
            {
            }
        }

        private static void RefreshEditorPrompt(Editor editor)
        {
            if (editor == null)
            {
                return;
            }

            try
            {
                editor.WriteMessage("\n");
                editor.PostCommandPrompt();
            }
            catch
            {
                // best-effort prompt refresh only
            }
        }

        private static bool TryCollectEntriesFromSectionIndex(
            Config config,
            int zone,
            Polyline boundary,
            out List<SectionGridEntry> entries,
            out string message)
        {
            entries = new List<SectionGridEntry>();
            message = string.Empty;

            if (boundary == null || !IsClosedPolyline(boundary))
            {
                message = "Boundary selection is invalid.";
                return false;
            }

            if (config == null)
            {
                config = new Config();
            }

            if (zone != 11 && zone != 12)
            {
                message = $"Unsupported zone: {zone}.";
                return false;
            }

            var searchFolders = BuildSectionIndexSearchFolders(config);
            if (searchFolders.Count == 0)
            {
                message = "No section-index search folders are configured.";
                return true;
            }

            var boundaryHasExtents = TryGetExtents(boundary, out var boundaryExtents);
            var loadedAnySectionIndex = false;
            var sectionCount = 0;
            var quarterInsideCount = 0;
            var uniqueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var logger = new Logger();

            foreach (var folder in searchFolders)
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    continue;
                }

                if (!SectionIndexReader.TryLoadSectionOutlinesForZone(folder, zone, logger, out var sectionEntries) ||
                    sectionEntries.Count == 0)
                {
                    continue;
                }

                loadedAnySectionIndex = true;
                foreach (var sectionEntry in sectionEntries)
                {
                    if (sectionEntry == null ||
                        sectionEntry.Outline == null ||
                        sectionEntry.Outline.Vertices == null ||
                        sectionEntry.Outline.Vertices.Count < 3)
                    {
                        continue;
                    }

                    using var sectionPolyline = BuildPolyline(sectionEntry.Outline);
                    if (sectionPolyline == null || !IsClosedPolyline(sectionPolyline))
                    {
                        continue;
                    }

                    if (boundaryHasExtents &&
                        TryGetExtents(sectionPolyline, out var sectionExtents) &&
                        !GeometryUtils.ExtentsIntersect(boundaryExtents, sectionExtents))
                    {
                        continue;
                    }

                    sectionCount++;
                    if (!Plugin.TryBuildQuarterMapForBoundaryImport(sectionPolyline, out var quarterMap) ||
                        quarterMap.Count == 0)
                    {
                        DisposeQuarterMap(quarterMap);
                        continue;
                    }

                    try
                    {
                        foreach (var quarterPair in quarterMap)
                        {
                            var quarterPolyline = quarterPair.Value;
                            if (quarterPolyline == null || !IsClosedPolyline(quarterPolyline))
                            {
                                continue;
                            }

                            if (boundaryHasExtents &&
                                TryGetExtents(quarterPolyline, out var quarterExtents) &&
                                !ContainsExtents(boundaryExtents, quarterExtents, ExtentsTolerance))
                            {
                                continue;
                            }

                            if (!IsPolylineFullyInsideBoundary(boundary, quarterPolyline))
                            {
                                continue;
                            }

                            var quarterToken = Plugin.QuarterTokenForBoundaryImport(quarterPair.Key);
                            if (string.IsNullOrWhiteSpace(quarterToken))
                            {
                                continue;
                            }

                            quarterInsideCount++;
                            var key = sectionEntry.Key;
                            var entry = new SectionGridEntry(
                                NormalizeLegalNumberToken(key.Meridian),
                                NormalizeLegalNumberToken(key.Range),
                                NormalizeLegalNumberToken(key.Township),
                                NormalizeLegalNumberToken(key.Section),
                                quarterToken);
                            if (uniqueKeys.Add(BuildEntryKey(entry)))
                            {
                                entries.Add(entry);
                            }
                        }
                    }
                    finally
                    {
                        DisposeQuarterMap(quarterMap);
                    }
                }
            }

            entries = entries
                .OrderBy(e => ParseSortToken(e.Meridian).Number)
                .ThenBy(e => ParseSortToken(e.Meridian).Raw, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => ParseSortToken(e.Range).Number)
                .ThenBy(e => ParseSortToken(e.Range).Raw, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => ParseSortToken(e.Township).Number)
                .ThenBy(e => ParseSortToken(e.Township).Raw, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => ParseSortToken(e.Section).Number)
                .ThenBy(e => ParseSortToken(e.Section).Raw, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => QuarterOrder(e.Quarter))
                .ToList();

            if (!loadedAnySectionIndex)
            {
                message = $"No section index file was found for Zone {zone}. Checked: {string.Join("; ", searchFolders)}";
                return true;
            }

            if (entries.Count == 0)
            {
                message = $"No quarter definitions from section index fall completely inside the selected boundary (Zone {zone}, sections checked: {sectionCount}).";
                return true;
            }

            message = $"Found {entries.Count} quarter definition(s) inside the boundary (Zone {zone}, sections checked: {sectionCount}, quarter hits: {quarterInsideCount}).";
            return true;
        }

        private static IReadOnlyList<string> BuildSectionIndexSearchFolders(Config config)
        {
            var folders = new List<string>();
            AddFolder(folders, config?.SectionIndexFolder);
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

        private static Polyline? BuildPolyline(SectionOutline outline)
        {
            if (outline == null || outline.Vertices == null || outline.Vertices.Count < 3)
            {
                return null;
            }

            var polyline = new Polyline(outline.Vertices.Count);
            for (var i = 0; i < outline.Vertices.Count; i++)
            {
                polyline.AddVertexAt(i, outline.Vertices[i], 0, 0, 0);
            }

            polyline.Closed = outline.Closed || outline.Vertices[0].GetDistanceTo(outline.Vertices[outline.Vertices.Count - 1]) <= 1e-3;
            return polyline;
        }

        private static void DisposeQuarterMap(Dictionary<QuarterSelection, Polyline>? quarterMap)
        {
            if (quarterMap == null)
            {
                return;
            }

            foreach (var polyline in quarterMap.Values)
            {
                if (polyline == null)
                {
                    continue;
                }

                try
                {
                    polyline.Dispose();
                }
                catch
                {
                    // best-effort dispose
                }
            }
        }

        private static bool IsClosedPolyline(Polyline polyline)
        {
            if (polyline == null || polyline.NumberOfVertices < 3)
            {
                return false;
            }

            if (polyline.Closed)
            {
                return true;
            }

            try
            {
                var first = polyline.GetPoint2dAt(0);
                var last = polyline.GetPoint2dAt(polyline.NumberOfVertices - 1);
                return first.GetDistanceTo(last) <= 1e-3;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPolylineFullyInsideBoundary(Polyline boundary, Polyline candidate)
        {
            if (boundary == null || candidate == null)
            {
                return false;
            }

            var vertexCount = candidate.NumberOfVertices;
            if (vertexCount < 3)
            {
                return false;
            }

            for (var i = 0; i < vertexCount; i++)
            {
                var a = candidate.GetPoint2dAt(i);
                var b = candidate.GetPoint2dAt((i + 1) % vertexCount);
                if (!GeometryUtils.IsPointInsidePolyline(boundary, a))
                {
                    return false;
                }

                var midpoint = new Point2d(0.5 * (a.X + b.X), 0.5 * (a.Y + b.Y));
                if (!GeometryUtils.IsPointInsidePolyline(boundary, midpoint))
                {
                    return false;
                }
            }

            double length;
            try
            {
                length = candidate.Length;
            }
            catch
            {
                return false;
            }

            if (length <= 1e-6)
            {
                return false;
            }

            var sampleCount = Math.Max(16, Math.Min(120, (int)Math.Ceiling(length / 120.0)));
            for (var i = 0; i < sampleCount; i++)
            {
                var distance = ((i + 0.5) / sampleCount) * length;
                double parameter;
                Point3d point;
                try
                {
                    parameter = candidate.GetParameterAtDistance(distance);
                    point = candidate.GetPointAtParameter(parameter);
                }
                catch
                {
                    return false;
                }

                if (!GeometryUtils.IsPointInsidePolyline(boundary, new Point2d(point.X, point.Y)))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryGetExtents(Polyline polyline, out Extents3d extents)
        {
            extents = default;
            try
            {
                extents = polyline.GeometricExtents;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ContainsExtents(Extents3d outer, Extents3d inner, double tolerance)
        {
            return inner.MinPoint.X >= (outer.MinPoint.X - tolerance) &&
                   inner.MinPoint.Y >= (outer.MinPoint.Y - tolerance) &&
                   inner.MaxPoint.X <= (outer.MaxPoint.X + tolerance) &&
                   inner.MaxPoint.Y <= (outer.MaxPoint.Y + tolerance);
        }

        private static string NormalizeLegalNumberToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0)
            {
                return number.ToString(CultureInfo.InvariantCulture);
            }

            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric) && numeric > 0.0)
            {
                var rounded = (int)Math.Round(numeric);
                if (Math.Abs(numeric - rounded) <= 1e-9)
                {
                    return rounded.ToString(CultureInfo.InvariantCulture);
                }
            }

            return trimmed;
        }

        private static string BuildEntryKey(SectionGridEntry entry)
        {
            return string.Join(
                "|",
                NormalizeRowToken(entry.Meridian),
                NormalizeRowToken(entry.Range),
                NormalizeRowToken(entry.Township),
                NormalizeRowToken(entry.Section),
                NormalizeRowToken(entry.Quarter));
        }

        private static string NormalizeRowToken(string? value)
        {
            return (value ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static (int Number, string Raw) ParseSortToken(string token)
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return (number, string.Empty);
            }

            return (int.MaxValue, token ?? string.Empty);
        }

        private static int QuarterOrder(string quarter)
        {
            var normalized = NormalizeRowToken(quarter);
            if (string.Equals(normalized, "NW", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (string.Equals(normalized, "NE", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (string.Equals(normalized, "SW", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            if (string.Equals(normalized, "SE", StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            return 4;
        }
    }
}
