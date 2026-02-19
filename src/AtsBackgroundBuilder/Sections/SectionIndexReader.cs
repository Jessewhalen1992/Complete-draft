/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder.Sections
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
        private sealed class FileIndexCacheEntry
        {
            public FileIndexCacheEntry(DateTime lastWriteUtc, Dictionary<string, SectionOutline> index)
            {
                LastWriteUtc = lastWriteUtc;
                Index = index;
            }

            public DateTime LastWriteUtc { get; }
            public Dictionary<string, SectionOutline> Index { get; }
        }

        private static readonly object FileIndexCacheLock = new object();
        private static readonly Dictionary<string, FileIndexCacheEntry> JsonlFileIndexCache =
            new Dictionary<string, FileIndexCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, FileIndexCacheEntry> CsvFileIndexCache =
            new Dictionary<string, FileIndexCacheEntry>(StringComparer.OrdinalIgnoreCase);

        public static bool TryLoadSectionOutline(
            string baseFolder,
            SectionKey key,
            Logger logger,
            [NotNullWhen(true)] out SectionOutline? outline)
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

        private static bool TryReadFromJsonl(string path, SectionKey key, [NotNullWhen(true)] out SectionOutline? outline)
        {
            outline = null;
            var index = GetOrBuildJsonlFileIndex(path);
            if (index == null)
            {
                return false;
            }

            var lookupKey = BuildLookupKey(key.Zone, key.Section, key.Township, key.Range, key.Meridian);
            if (!index.TryGetValue(lookupKey, out var cached))
            {
                return false;
            }

            outline = CloneOutline(cached, path);
            return true;
        }

        private static bool TryReadFromCsv(string path, SectionKey key, [NotNullWhen(true)] out SectionOutline? outline)
        {
            outline = null;
            var index = GetOrBuildCsvFileIndex(path);
            if (index == null)
            {
                return false;
            }

            var lookupKey = BuildLookupKey(key.Zone, key.Section, key.Township, key.Range, key.Meridian);
            if (!index.TryGetValue(lookupKey, out var cached))
            {
                return false;
            }

            outline = CloneOutline(cached, path);
            return true;
        }

        private static Dictionary<string, SectionOutline>? GetOrBuildJsonlFileIndex(string path)
        {
            if (!TryGetFileLastWriteUtc(path, out var lastWriteUtc))
            {
                return null;
            }

            lock (FileIndexCacheLock)
            {
                if (JsonlFileIndexCache.TryGetValue(path, out var cached) &&
                    cached.LastWriteUtc == lastWriteUtc)
                {
                    return cached.Index;
                }
            }

            var built = BuildJsonlFileIndex(path);
            lock (FileIndexCacheLock)
            {
                JsonlFileIndexCache[path] = new FileIndexCacheEntry(lastWriteUtc, built);
            }

            return built;
        }

        private static Dictionary<string, SectionOutline>? GetOrBuildCsvFileIndex(string path)
        {
            if (!TryGetFileLastWriteUtc(path, out var lastWriteUtc))
            {
                return null;
            }

            lock (FileIndexCacheLock)
            {
                if (CsvFileIndexCache.TryGetValue(path, out var cached) &&
                    cached.LastWriteUtc == lastWriteUtc)
                {
                    return cached.Index;
                }
            }

            var built = BuildCsvFileIndex(path);
            lock (FileIndexCacheLock)
            {
                CsvFileIndexCache[path] = new FileIndexCacheEntry(lastWriteUtc, built);
            }

            return built;
        }

        private static bool TryGetFileLastWriteUtc(string path, out DateTime lastWriteUtc)
        {
            lastWriteUtc = default;
            try
            {
                lastWriteUtc = File.GetLastWriteTimeUtc(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, SectionOutline> BuildJsonlFileIndex(string path)
        {
            var index = new Dictionary<string, SectionOutline>(StringComparer.OrdinalIgnoreCase);
            try
            {
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

                    if (record.Vertices == null || record.Vertices.Count < 3)
                    {
                        continue;
                    }

                    var lookupKey = BuildLookupKey(record.Zone, record.Section, record.Township, record.Range, record.Meridian);
                    if (string.IsNullOrWhiteSpace(lookupKey) || index.ContainsKey(lookupKey))
                    {
                        continue;
                    }

                    index[lookupKey] = new SectionOutline(new List<Point2d>(record.Vertices), record.Closed, path);
                }
            }
            catch
            {
                return new Dictionary<string, SectionOutline>(StringComparer.OrdinalIgnoreCase);
            }

            return index;
        }

        private static Dictionary<string, SectionOutline> BuildCsvFileIndex(string path)
        {
            var index = new Dictionary<string, SectionOutline>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var reader = new StreamReader(path))
                {
                    var headerLine = reader.ReadLine();
                    if (headerLine == null)
                    {
                        return index;
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
                            !int.TryParse(NormalizeKey(zoneValue), NumberStyles.Integer, CultureInfo.InvariantCulture, out var zone))
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

                        if (!TryGetDouble(columns, indices, "MINX", out var minX) ||
                            !TryGetDouble(columns, indices, "MINY", out var minY) ||
                            !TryGetDouble(columns, indices, "MAXX", out var maxX) ||
                            !TryGetDouble(columns, indices, "MAXY", out var maxY))
                        {
                            continue;
                        }

                        var lookupKey = BuildLookupKey(zone, secValue, twpValue, rgeValue, merValue);
                        if (string.IsNullOrWhiteSpace(lookupKey) || index.ContainsKey(lookupKey))
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

                        index[lookupKey] = new SectionOutline(vertices, true, path);
                    }
                }
            }
            catch
            {
                return new Dictionary<string, SectionOutline>(StringComparer.OrdinalIgnoreCase);
            }

            return index;
        }

        private static string BuildLookupKey(int zone, string? section, string? township, string? range, string? meridian)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}|{4}",
                zone,
                NormalizeKey(section),
                NormalizeKey(township),
                NormalizeKey(range),
                NormalizeKey(meridian));
        }

        private static SectionOutline CloneOutline(SectionOutline source, string sourcePath)
        {
            return new SectionOutline(
                new List<Point2d>(source.Vertices),
                source.Closed,
                sourcePath);
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
