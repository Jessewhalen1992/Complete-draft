using System;
using System.Collections.Generic;

namespace AtsBackgroundBuilder.Core
{
    internal readonly struct BoundaryImportMergeResult
    {
        internal BoundaryImportMergeResult(int added, int duplicates)
        {
            Added = added;
            Duplicates = duplicates;
        }

        internal int Added { get; }
        internal int Duplicates { get; }
    }

    internal static class BoundaryImportRowMergeService
    {
        internal static HashSet<string> BuildExistingKeySet(
            IEnumerable<(string Meridian, string Range, string Township, string Section, string Quarter)> rows)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rows == null)
            {
                return keys;
            }

            foreach (var row in rows)
            {
                keys.Add(BuildRowKey(row.Meridian, row.Range, row.Township, row.Section, row.Quarter));
            }

            return keys;
        }

        internal static BoundaryImportMergeResult MergeImportedRows(
            IEnumerable<BoundarySectionImportService.SectionGridEntry> importedRows,
            HashSet<string> existingKeys,
            Action<BoundarySectionImportService.SectionGridEntry> addRow)
        {
            if (existingKeys == null)
            {
                existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            if (addRow == null)
            {
                throw new ArgumentNullException(nameof(addRow));
            }

            var added = 0;
            var duplicates = 0;
            if (importedRows == null)
            {
                return new BoundaryImportMergeResult(added, duplicates);
            }

            foreach (var entry in importedRows)
            {
                if (entry == null)
                {
                    continue;
                }

                var key = BuildRowKey(entry.Meridian, entry.Range, entry.Township, entry.Section, entry.Quarter);
                if (!existingKeys.Add(key))
                {
                    duplicates++;
                    continue;
                }

                addRow(entry);
                added++;
            }

            return new BoundaryImportMergeResult(added, duplicates);
        }

        internal static string BuildBoundaryImportResultMessage(string serviceMessage, int added, int duplicates)
        {
            var prefix = string.IsNullOrWhiteSpace(serviceMessage)
                ? string.Empty
                : serviceMessage.Trim() + Environment.NewLine + Environment.NewLine;
            if (added <= 0)
            {
                return prefix + "No new section rows were added." +
                       (duplicates > 0 ? $" Skipped {duplicates} duplicate row(s)." : string.Empty);
            }

            return prefix + $"Added {added} row(s) to the section input list." +
                   (duplicates > 0 ? $" Skipped {duplicates} duplicate row(s)." : string.Empty);
        }

        private static string BuildRowKey(string m, string rge, string twp, string sec, string hq)
        {
            return string.Join(
                "|",
                NormalizeRowToken(m),
                NormalizeRowToken(rge),
                NormalizeRowToken(twp),
                NormalizeRowToken(sec),
                NormalizeRowToken(hq));
        }

        private static string NormalizeRowToken(string value)
        {
            return value?.Trim().ToUpperInvariant() ?? string.Empty;
        }
    }
}
