using System;
using System.Collections.Generic;
using System.Linq;
using AtsBackgroundBuilder.SurfaceImpact;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        private static void RunSurfaceImpact(
            Database database,
            Editor editor,
            Logger logger,
            AtsBackgroundBuilder.Core.AtsBuildInput input)
        {
            if (database == null || editor == null || logger == null || input == null)
            {
                return;
            }

            if (input.PlsrXmlPaths == null || input.PlsrXmlPaths.Count == 0)
            {
                logger.WriteLine("Surface Impact skipped: no XML files selected.");
                editor.WriteMessage("\nSurface Impact skipped: no XML files selected.");
                return;
            }

            var validXmlPaths = input.PlsrXmlPaths
                .Where(path => !string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (validXmlPaths.Count == 0)
            {
                logger.WriteLine("Surface Impact skipped: selected XML files are missing.");
                editor.WriteMessage("\nSurface Impact skipped: selected XML files are missing.");
                return;
            }

            var parser = new SurfaceImpactXmlParser();
            var allRecords = new List<SurfaceImpactActivityRecord>();
            var errors = new List<string>();

            foreach (var xmlPath in validXmlPaths)
            {
                var parseResult = parser.ParseFile(xmlPath);
                allRecords.AddRange(parseResult.Records);
                errors.AddRange(parseResult.Errors);
            }

            foreach (var error in errors)
            {
                logger.WriteLine("Surface Impact parse: " + error);
            }

            if (allRecords.Count == 0)
            {
                logger.WriteLine("Surface Impact skipped: no records extracted from XML.");
                editor.WriteMessage("\nSurface Impact skipped: no records extracted from XML (check ReportRunDate and XML content).");
                return;
            }

            allRecords = KeepNewestReportForDuplicateLandLocations(
                allRecords,
                out var removedForNewestWins,
                out var duplicateLandLocationCount);

            if (duplicateLandLocationCount > 0)
            {
                logger.WriteLine(
                    "Surface Impact newest-wins: duplicate land locations=" +
                    duplicateLandLocationCount +
                    ", removed older/duplicate rows=" +
                    removedForNewestWins +
                    ".");
            }

            var scopedRecords = FilterSurfaceImpactRecordsByInputScope(
                allRecords,
                input,
                out var outOfScopeCount,
                out var unparsedLandCount);

            logger.WriteLine(
                "Surface Impact scope filter: input=" +
                allRecords.Count +
                ", kept=" +
                scopedRecords.Count +
                ", outOfScope=" +
                outOfScopeCount +
                ", landParseMisses=" +
                unparsedLandCount +
                ".");

            if (scopedRecords.Count == 0)
            {
                editor.WriteMessage("\nSurface Impact skipped: no XML activities matched the requested section input.");
                return;
            }

            var processor = new SurfaceImpactProcessor();
            var processed = processor.FilterAndCategorize(scopedRecords);

            var includedCount = processed.FmaRecords.Count + processed.TpaRecords.Count + processed.SurfaceRecords.Count;
            if (includedCount == 0)
            {
                editor.WriteMessage("\nSurface Impact skipped: no activities remained after filtering rules.");
                logger.WriteLine("Surface Impact: no records remained after filtering rules.");
                return;
            }

            var prompt = new PromptPointOptions("\nSpecify insertion point for Surface Impact table:");
            var promptResult = editor.GetPoint(prompt);
            if (promptResult.Status != PromptStatus.OK)
            {
                logger.WriteLine("Surface Impact cancelled: insertion point was not provided.");
                editor.WriteMessage("\nSurface Impact cancelled.");
                return;
            }

            try
            {
                using (var tr = database.TransactionManager.StartTransaction())
                {
                    var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                    var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    var builder = new SurfaceImpactTableBuilder();
                    var table = builder.BuildTable(
                        database,
                        tr,
                        processed.FmaRecords,
                        processed.TpaRecords,
                        processed.SurfaceRecords);
                    table.Position = promptResult.Value;

                    modelSpace.AppendEntity(table);
                    tr.AddNewlyCreatedDBObject(table, true);
                    tr.Commit();
                }

                var summary =
                    "Surface Impact table created (FMA=" +
                    processed.FmaRecords.Count +
                    ", TPA=" +
                    processed.TpaRecords.Count +
                    ", Surface=" +
                    processed.SurfaceRecords.Count +
                    ").";
                logger.WriteLine(summary);
                editor.WriteMessage("\n" + summary);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                var message = "Surface Impact failed to create table: " + ex.ErrorStatus + " (" + ex.Message + ")";
                logger.WriteLine(message);
                editor.WriteMessage("\n" + message);
            }
            catch (Exception ex)
            {
                var message = "Surface Impact failed to create table: " + ex.Message;
                logger.WriteLine(message);
                editor.WriteMessage("\n" + message);
            }
        }

        private static List<SurfaceImpactActivityRecord> KeepNewestReportForDuplicateLandLocations(
            List<SurfaceImpactActivityRecord> records,
            out int removedRecords,
            out int duplicateLandLocationCount)
        {
            removedRecords = 0;
            duplicateLandLocationCount = 0;

            if (records == null || records.Count == 0)
            {
                return records ?? new List<SurfaceImpactActivityRecord>();
            }

            string NormalizeLand(string value) => (value ?? string.Empty).Trim();

            static DateTime ReportDate(SurfaceImpactActivityRecord record)
            {
                if (record != null && record.ReportRunDate.HasValue)
                {
                    return record.ReportRunDate.Value.Date;
                }

                return DateTime.MinValue;
            }

            bool HasRealLand(SurfaceImpactActivityRecord record)
            {
                if (record == null)
                {
                    return false;
                }

                var land = NormalizeLand(record.LandLocation);
                if (string.IsNullOrWhiteSpace(land))
                {
                    return false;
                }

                return !string.Equals(land, "N/A", StringComparison.OrdinalIgnoreCase);
            }

            var landGroups = records
                .Where(HasRealLand)
                .GroupBy(r => NormalizeLand(r.LandLocation), StringComparer.OrdinalIgnoreCase)
                .ToList();

            duplicateLandLocationCount = landGroups.Count(g => g.Count() > 1);

            var newestDateByLand = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in landGroups)
            {
                var maxDate = DateTime.MinValue;
                foreach (var record in group)
                {
                    var date = ReportDate(record);
                    if (date > maxDate)
                    {
                        maxDate = date;
                    }
                }

                newestDateByLand[group.Key] = maxDate;
            }

            var output = new List<SurfaceImpactActivityRecord>(records.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var record in records)
            {
                if (!HasRealLand(record))
                {
                    output.Add(record);
                    continue;
                }

                var land = NormalizeLand(record.LandLocation);
                var newestDate = newestDateByLand.TryGetValue(land, out var nd) ? nd : DateTime.MinValue;

                var recordDate = ReportDate(record);
                if (recordDate != newestDate)
                {
                    removedRecords++;
                    continue;
                }

                var signature =
                    land + "\u001F" +
                    recordDate.ToString("yyyy-MM-dd") + "\u001F" +
                    (record.DispositionNumber ?? string.Empty) + "\u001F" +
                    (record.OwnerName ?? string.Empty) + "\u001F" +
                    (record.ExpiryDateString ?? string.Empty) + "\u001F" +
                    (record.VersionDateString ?? string.Empty) + "\u001F" +
                    (record.Status ?? string.Empty);

                if (!seen.Add(signature))
                {
                    removedRecords++;
                    continue;
                }

                output.Add(record);
            }

            return output;
        }

        private static List<SurfaceImpactActivityRecord> FilterSurfaceImpactRecordsByInputScope(
            IReadOnlyList<SurfaceImpactActivityRecord> records,
            AtsBackgroundBuilder.Core.AtsBuildInput input,
            out int outOfScopeCount,
            out int unparsedLandCount)
        {
            outOfScopeCount = 0;
            unparsedLandCount = 0;

            if (records == null || records.Count == 0 || input == null || input.SectionRequests == null || input.SectionRequests.Count == 0)
            {
                return new List<SurfaceImpactActivityRecord>();
            }

            var requestedQuarterKeys = new HashSet<string>(
                BuildRequestedQuarterKeys(input.SectionRequests),
                StringComparer.OrdinalIgnoreCase);

            var requestedSectionKeys = BuildRequestedSectionScopeKeys(input.SectionRequests);
            if (requestedSectionKeys.Count == 0)
            {
                return new List<SurfaceImpactActivityRecord>();
            }

            var scoped = new List<SurfaceImpactActivityRecord>(records.Count);
            foreach (var record in records)
            {
                if (record == null)
                {
                    continue;
                }

                if (!TryParseLandScope(record.LandLocation, out var meridian, out var range, out var township, out var section, out var quarterToken))
                {
                    unparsedLandCount++;
                    outOfScopeCount++;
                    continue;
                }

                var sectionKey = BuildSurfaceSectionScopeKey(meridian, range, township, section);
                if (!requestedSectionKeys.Contains(sectionKey))
                {
                    outOfScopeCount++;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(quarterToken))
                {
                    var quarterMatchFound = false;
                    foreach (var expandedQuarter in ExpandLandQuarterToken(quarterToken))
                    {
                        var quarterKey = BuildQuarterKey(meridian, range, township, section, expandedQuarter);
                        if (requestedQuarterKeys.Contains(quarterKey))
                        {
                            quarterMatchFound = true;
                            break;
                        }
                    }

                    if (!quarterMatchFound)
                    {
                        outOfScopeCount++;
                        continue;
                    }
                }

                scoped.Add(record);
            }

            return scoped;
        }

        private static HashSet<string> BuildRequestedSectionScopeKeys(IEnumerable<SectionRequest> requests)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (requests == null)
            {
                return keys;
            }

            foreach (var request in requests)
            {
                var meridian = NormalizeMeridianToken(request.Key.Meridian);
                var range = NormalizeNumberToken(request.Key.Range);
                var township = NormalizeNumberToken(request.Key.Township);
                var section = NormalizeNumberToken(request.Key.Section);

                if (string.IsNullOrWhiteSpace(meridian) ||
                    string.IsNullOrWhiteSpace(range) ||
                    string.IsNullOrWhiteSpace(township) ||
                    string.IsNullOrWhiteSpace(section))
                {
                    continue;
                }

                keys.Add(BuildSurfaceSectionScopeKey(meridian, range, township, section));
            }

            return keys;
        }

        private static string BuildSurfaceSectionScopeKey(string meridian, string range, string township, string section)
        {
            return NormalizeMeridianToken(meridian) +
                   "|" +
                   NormalizeNumberToken(range) +
                   "|" +
                   NormalizeNumberToken(township) +
                   "|" +
                   NormalizeNumberToken(section);
        }

        private static IEnumerable<string> ExpandLandQuarterToken(string quarterToken)
        {
            var normalized = (quarterToken ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                yield break;
            }

            switch (normalized)
            {
                case "NW":
                case "NE":
                case "SW":
                case "SE":
                    yield return normalized;
                    yield break;
                case "N":
                    yield return "NW";
                    yield return "NE";
                    yield break;
                case "S":
                    yield return "SW";
                    yield return "SE";
                    yield break;
                case "E":
                    yield return "NE";
                    yield return "SE";
                    yield break;
                case "W":
                    yield return "NW";
                    yield return "SW";
                    yield break;
            }
        }

        private static bool TryParseLandScope(
            string landLocation,
            out string meridian,
            out string range,
            out string township,
            out string section,
            out string quarterToken)
        {
            meridian = string.Empty;
            range = string.Empty;
            township = string.Empty;
            section = string.Empty;
            quarterToken = string.Empty;

            if (string.IsNullOrWhiteSpace(landLocation))
            {
                return false;
            }

            var tokens = landLocation
                .Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (tokens.Count < 4)
            {
                return false;
            }

            // Quarter token can appear first or last depending on feed format.
            if (TryNormalizeQuarterToken(tokens[tokens.Count - 1], out var quarterFromEnd))
            {
                quarterToken = quarterFromEnd;
                tokens.RemoveAt(tokens.Count - 1);
            }
            else if (TryNormalizeQuarterToken(tokens[0], out var quarterFromStart))
            {
                quarterToken = quarterFromStart;
                tokens.RemoveAt(0);
            }

            if (tokens.Count >= 5 &&
                TryNormalizeMeridianToken(tokens[4], out var lsdPatternMeridian) &&
                TryParsePositiveInt(tokens[0], out var lsdNumber) &&
                lsdNumber >= 1 && lsdNumber <= 16 &&
                TryParsePositiveInt(tokens[1], out var lsdPatternSection) &&
                lsdPatternSection >= 1 && lsdPatternSection <= 36 &&
                TryParsePositiveInt(tokens[2], out _) &&
                TryParsePositiveInt(tokens[3], out _))
            {
                meridian = lsdPatternMeridian;
                range = NormalizeNumberToken(tokens[3]);
                township = NormalizeNumberToken(tokens[2]);
                section = NormalizeNumberToken(tokens[1]);
                return true;
            }

            if (tokens.Count >= 4 &&
                TryNormalizeMeridianToken(tokens[0], out var meridianFirstPatternMeridian) &&
                TryParsePositiveInt(tokens[1], out _) &&
                TryParsePositiveInt(tokens[2], out _) &&
                TryParsePositiveInt(tokens[3], out _))
            {
                meridian = meridianFirstPatternMeridian;
                range = NormalizeNumberToken(tokens[1]);
                township = NormalizeNumberToken(tokens[2]);
                section = NormalizeNumberToken(tokens[3]);
                return true;
            }

            if (tokens.Count >= 4 &&
                TryNormalizeMeridianToken(tokens[3], out var sectionPatternMeridian) &&
                TryParsePositiveInt(tokens[0], out var sectionPatternSection) &&
                sectionPatternSection >= 1 && sectionPatternSection <= 36 &&
                TryParsePositiveInt(tokens[1], out _) &&
                TryParsePositiveInt(tokens[2], out _))
            {
                meridian = sectionPatternMeridian;
                range = NormalizeNumberToken(tokens[2]);
                township = NormalizeNumberToken(tokens[1]);
                section = NormalizeNumberToken(tokens[0]);
                return true;
            }

            return false;
        }

        private static bool TryNormalizeMeridianToken(string token, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var digits = new string(token.Where(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out var meridianNumber))
            {
                return false;
            }

            if (meridianNumber <= 0)
            {
                return false;
            }

            if (meridianNumber > 9)
            {
                return false;
            }

            normalized = meridianNumber.ToString();
            return true;
        }

        private static bool TryNormalizeQuarterToken(string token, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var upper = token.Trim().ToUpperInvariant();
            if (upper == "NW" || upper == "NE" || upper == "SW" || upper == "SE" ||
                upper == "N" || upper == "S" || upper == "E" || upper == "W")
            {
                normalized = upper;
                return true;
            }

            return false;
        }

        private static bool TryParsePositiveInt(string token, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var trimmed = token.Trim();
            if (!int.TryParse(trimmed, out value))
            {
                return false;
            }

            return value > 0;
        }
    }
}
