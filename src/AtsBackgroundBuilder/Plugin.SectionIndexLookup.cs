using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
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
            [NotNullWhen(true)] out SectionOutline? outline)
        {
            outline = null;
            var cacheKey = BuildSectionOutlineCacheKey(searchFolders, key);
            lock (SectionOutlineCacheLock)
            {
                if (SectionOutlineCache.TryGetValue(cacheKey, out var cached))
                {
                    outline = cached == null
                        ? null
                        : new SectionOutline(new List<Point2d>(cached.Vertices), cached.Closed, cached.SourcePath);
                    return outline != null;
                }
            }

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
                    lock (SectionOutlineCacheLock)
                    {
                        SectionOutlineCache[cacheKey] = new SectionOutline(new List<Point2d>(outline.Vertices), outline.Closed, outline.SourcePath);
                    }
                    return true;
                }
            }

            if (!checkedAny)
            {
                logger.WriteLine($"No section index file found for zone {key.Zone}. Searched: {string.Join("; ", searchFolders)}");
            }

            lock (SectionOutlineCacheLock)
            {
                SectionOutlineCache[cacheKey] = null;
            }
            return false;
        }

        private static bool FolderHasSectionIndex(string folder, int zone)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }

            var cacheKey = string.Format(CultureInfo.InvariantCulture, "{0}|{1}", folder, zone);
            lock (SectionOutlineCacheLock)
            {
                if (FolderIndexCache.TryGetValue(cacheKey, out var existsCached))
                {
                    return existsCached;
                }
            }

            var jsonl = Path.Combine(folder, $"Master_Sections.index_Z{zone}.jsonl");
            var csv = Path.Combine(folder, $"Master_Sections.index_Z{zone}.csv");
            var jsonlFallback = Path.Combine(folder, "Master_Sections.index.jsonl");
            var csvFallback = Path.Combine(folder, "Master_Sections.index.csv");
            var exists = File.Exists(jsonl) || File.Exists(csv) || File.Exists(jsonlFallback) || File.Exists(csvFallback);
            lock (SectionOutlineCacheLock)
            {
                FolderIndexCache[cacheKey] = exists;
            }

            return exists;
        }

        private static string BuildSectionOutlineCacheKey(IReadOnlyList<string> searchFolders, SectionKey key)
        {
            var foldersKey = searchFolders == null
                ? string.Empty
                : string.Join(";", searchFolders.Select(f => (f ?? string.Empty).Trim()).Where(f => f.Length > 0));

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}|{4}|{5}",
                key.Zone,
                NormalizeNumberToken(key.Meridian),
                NormalizeNumberToken(key.Range),
                NormalizeNumberToken(key.Township),
                NormalizeNumberToken(key.Section),
                foldersKey);
        }

        private struct P3ImportSummary
        {
            public int ImportedEntities;
            public int FilteredEntities;
            public int ImportFailures;
        }
    }
}
