using System;
using System.Collections.Generic;
using System.Linq;

namespace AtsBackgroundBuilder.SurfaceImpact
{
    internal sealed class SurfaceImpactProcessor
    {
        private static readonly string[] ExcludedPrefixes =
        {
            "LOC", "PLA", "MSL", "MLL", "DML", "EZE", "PIL", "RME", "RML", "DLO", "ROE", "RRD", "DPI", "DPL", "VCE", "DRS", "SML", "SME"
        };

        public SurfaceImpactProcessingResult FilterAndCategorize(IEnumerable<SurfaceImpactActivityRecord> records)
        {
            if (records == null)
            {
                throw new ArgumentNullException(nameof(records));
            }

            var filtered = records.Where(IsIncluded).ToList();

            var fma = DeduplicateByDisposition(filtered.Where(r => StartsWith(r.DispositionNumber, "FMA"))).ToList();
            var tpa = DeduplicateByDisposition(filtered.Where(r => StartsWith(r.DispositionNumber, "TPA"))).ToList();

            var surfaceCandidates = filtered
                .Where(r => !StartsWith(r.DispositionNumber, "FMA") && !StartsWith(r.DispositionNumber, "TPA"))
                .ToList();

            var surfaceList = new List<SurfaceImpactActivityRecord>(surfaceCandidates.Count);
            foreach (var record in surfaceCandidates)
            {
                if (ContainsRtfTfa(record.DispositionNumber))
                {
                    record.IsRtfTfa = true;
                }

                surfaceList.Add(record);
            }

            // Deduplicate RTF/TFA by (Land Location, Owner Name).
            var dedupedSurface = new List<SurfaceImpactActivityRecord>();

            var rtfDeduped = surfaceList
                .Where(r => r.IsRtfTfa)
                .GroupBy(r => MakeLandOwnerKey(r.LandLocation, r.OwnerName), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First());

            // Keep RTF/TFA rows first, then others.
            dedupedSurface.AddRange(rtfDeduped);
            dedupedSurface.AddRange(surfaceList.Where(r => !r.IsRtfTfa));

            foreach (var record in dedupedSurface)
            {
                record.BaseLandLocation = ComputeBaseLandLocation(record.LandLocation);
            }

            var orderedSurface = dedupedSurface
                .OrderBy(r => r.BaseLandLocation ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.LandLocation ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new SurfaceImpactProcessingResult
            {
                FmaRecords = fma,
                TpaRecords = tpa,
                SurfaceRecords = orderedSurface
            };
        }

        private static bool IsIncluded(SurfaceImpactActivityRecord record)
        {
            if (record == null)
            {
                return false;
            }

            var disp = record.DispositionNumber ?? string.Empty;
            if (string.IsNullOrWhiteSpace(disp))
            {
                return false;
            }

            var status = record.Status ?? string.Empty;
            if (status.IndexOf("Cancelled", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            if (ExcludedPrefixes.Any(prefix => StartsWith(disp, prefix)))
            {
                return false;
            }

            if (record.IsExpired && !StartsWith(disp, "TPA") && !StartsWith(disp, "FMA"))
            {
                return false;
            }

            return true;
        }

        private static IEnumerable<SurfaceImpactActivityRecord> DeduplicateByDisposition(IEnumerable<SurfaceImpactActivityRecord> records)
        {
            return records
                .GroupBy(r => (r.DispositionNumber ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First());
        }

        private static string MakeLandOwnerKey(string land, string owner)
        {
            return (land ?? string.Empty).Trim() + "\u001F" + (owner ?? string.Empty).Trim();
        }

        private static bool StartsWith(string value, string prefix)
        {
            if (value == null)
            {
                return false;
            }

            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsRtfTfa(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            return value.IndexOf("RTF", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("TFA", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ComputeBaseLandLocation(string landLocation)
        {
            if (string.IsNullOrWhiteSpace(landLocation))
            {
                return string.Empty;
            }

            var parts = landLocation.Split('-');
            if (parts.Length > 4)
            {
                return string.Join("-", parts.Take(4));
            }

            return landLocation;
        }
    }

    internal sealed class SurfaceImpactProcessingResult
    {
        public List<SurfaceImpactActivityRecord> FmaRecords { get; set; } = new List<SurfaceImpactActivityRecord>();
        public List<SurfaceImpactActivityRecord> TpaRecords { get; set; } = new List<SurfaceImpactActivityRecord>();
        public List<SurfaceImpactActivityRecord> SurfaceRecords { get; set; } = new List<SurfaceImpactActivityRecord>();
    }
}
