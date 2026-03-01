using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace AtsBackgroundBuilder.SurfaceImpact
{
    internal sealed class SurfaceImpactXmlParser
    {
        private static readonly XNamespace Ns = "urn:srd.gov.ab.ca:glimps:data:reports";

        public SurfaceImpactParseResult ParseFile(string filePath)
        {
            var result = new SurfaceImpactParseResult
            {
                SourceFile = filePath
            };

            try
            {
                var doc = XDocument.Load(filePath);

                // ReportRunDate is required. If missing/invalid, no records are extracted from this file.
                var reportRunDateText = doc
                    .Descendants(Ns + "ReportRunDate")
                    .Select(x => x.Value)
                    .FirstOrDefault();

                if (string.IsNullOrWhiteSpace(reportRunDateText))
                {
                    result.Errors.Add($"ReportRunDate not found or empty in {filePath}.");
                    return result;
                }

                var reportRunDate = ParseDate(reportRunDateText);
                if (!reportRunDate.HasValue)
                {
                    result.Errors.Add($"Invalid ReportRunDate in {filePath}: '{reportRunDateText}'. Expected yyyy-MM-dd.");
                    return result;
                }

                result.ReportRunDate = reportRunDate.Value;
                result.ReportRunDateRaw = NormalizeToDate10(reportRunDateText);

                var activities = doc.Descendants(Ns + "Activity");
                foreach (var activity in activities)
                {
                    var disposition = (activity.Descendants(Ns + "ActivityNumber").Select(x => x.Value).FirstOrDefault() ?? "N/A").Trim();

                    var owner = activity
                        .Descendants(Ns + "ActivityClient")
                        .Descendants(Ns + "ClientName")
                        .Select(x => x.Value)
                        .FirstOrDefault();
                    owner = (owner ?? "N/A").Trim();

                    var expiryRaw = (activity.Descendants(Ns + "ExpiryDate").Select(x => x.Value).FirstOrDefault() ?? string.Empty).Trim();
                    var status = (activity.Descendants(Ns + "Status").Select(x => x.Value).FirstOrDefault() ?? string.Empty).Trim();
                    var versionRaw = (activity.Descendants(Ns + "ActivityDate").Select(x => x.Value).FirstOrDefault() ?? string.Empty).Trim();

                    var expiryText = string.IsNullOrWhiteSpace(expiryRaw) ? "N/A" : NormalizeToDate10(expiryRaw);
                    var versionDate = string.IsNullOrWhiteSpace(versionRaw) ? "N/A" : NormalizeToDate10(versionRaw);
                    var expiryDate = ParseDate(expiryRaw);

                    // Include all LandId values under all Lands blocks.
                    var landIdEls = activity
                        .Descendants(Ns + "Lands")
                        .Descendants(Ns + "LandId");

                    foreach (var landIdEl in landIdEls)
                    {
                        var landLocation = (landIdEl.Value ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(landLocation))
                        {
                            landLocation = "N/A";
                        }

                        result.Records.Add(new SurfaceImpactActivityRecord
                        {
                            DispositionNumber = string.IsNullOrWhiteSpace(disposition) ? "N/A" : disposition,
                            OwnerName = string.IsNullOrWhiteSpace(owner) ? "N/A" : owner,
                            ExpiryDateString = expiryText,
                            LandLocation = landLocation,
                            VersionDateString = versionDate,
                            Status = string.IsNullOrWhiteSpace(status) ? "N/A" : status,
                            ReportRunDate = reportRunDate.Value,
                            ExpiryDate = expiryDate,
                            IsExpired = expiryDate.HasValue && expiryDate.Value.Date < reportRunDate.Value.Date
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to parse {filePath}: {ex.Message}");
            }

            return result;
        }

        private static DateTime? ParseDate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var trimmed = text.Trim();
            if (trimmed.Length >= 10)
            {
                trimmed = trimmed.Substring(0, 10);
            }

            if (DateTime.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            {
                return exact.Date;
            }

            if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return parsed.Date;
            }

            return null;
        }

        private static string NormalizeToDate10(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "N/A";
            }

            var trimmed = text.Trim();
            return trimmed.Length >= 10 ? trimmed.Substring(0, 10) : trimmed;
        }
    }

    internal sealed class SurfaceImpactParseResult
    {
        public string SourceFile { get; set; } = string.Empty;
        public List<SurfaceImpactActivityRecord> Records { get; } = new List<SurfaceImpactActivityRecord>();
        public List<string> Errors { get; } = new List<string>();
        public DateTime? ReportRunDate { get; set; }
        public string ReportRunDateRaw { get; set; } = string.Empty;
    }
}
