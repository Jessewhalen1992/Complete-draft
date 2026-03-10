using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.IO;

namespace AtsBackgroundBuilder.Core
{
    internal sealed class AtsBuildExcelInputLoadResult
    {
        internal AtsBuildExcelInputLoadResult(
            AtsBuildInput? input,
            string workbookPath,
            string worksheetName,
            int sourceRowCount,
            string errorMessage)
        {
            Input = input;
            WorkbookPath = workbookPath ?? string.Empty;
            WorksheetName = worksheetName ?? string.Empty;
            SourceRowCount = sourceRowCount;
            ErrorMessage = errorMessage ?? string.Empty;
        }

        internal AtsBuildInput? Input { get; }
        internal string WorkbookPath { get; }
        internal string WorksheetName { get; }
        internal int SourceRowCount { get; }
        internal string ErrorMessage { get; }
        internal bool Success => Input != null && string.IsNullOrWhiteSpace(ErrorMessage);
    }

    internal static class AtsBuildExcelInputLoader
    {
        private static readonly string[] PreferredWorksheetNames =
        {
            "ATSBUILD_Input",
            "Blank_Template",
        };

        internal static AtsBuildExcelInputLoadResult Load(string workbookPath, Logger logger)
        {
            if (string.IsNullOrWhiteSpace(workbookPath))
            {
                return Failure(workbookPath, string.Empty, 0, "Workbook path is required.");
            }

            var resolvedPath = workbookPath.Trim().Trim('"');
            if (!Path.IsPathRooted(resolvedPath))
            {
                resolvedPath = Path.GetFullPath(resolvedPath);
            }

            if (!File.Exists(resolvedPath))
            {
                return Failure(resolvedPath, string.Empty, 0, "Workbook not found: " + resolvedPath);
            }

            try
            {
                using var connection = new OleDbConnection(BuildConnectionString(resolvedPath));
                connection.Open();

                var schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null);
                if (!TryResolveWorksheetName(schema, out var tableName, out var worksheetName))
                {
                    return Failure(
                        resolvedPath,
                        string.Empty,
                        0,
                        "Workbook must contain a worksheet named 'ATSBUILD_Input' or 'Blank_Template'.");
                }

                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM [{tableName}]";
                using var adapter = new OleDbDataAdapter(command);
                var table = new DataTable();
                adapter.Fill(table);

                var client = GetCellString(table, 0, 1);
                if (string.IsNullOrWhiteSpace(client))
                {
                    return Failure(resolvedPath, worksheetName, 0, $"Client name is blank in {worksheetName}!B1.");
                }

                var zoneRaw = GetCellString(table, 0, 4);
                if (!TryParseZone(zoneRaw, out var zone))
                {
                    return Failure(resolvedPath, worksheetName, 0, $"Zone value in {worksheetName}!E1 is invalid: '{zoneRaw}'.");
                }

                if (!TryFindHeaderRow(table, out var headerRowIndex, out var quarterColumnIndex, out var sectionColumnIndex, out var townshipColumnIndex, out var rangeColumnIndex, out var meridianColumnIndex))
                {
                    return Failure(
                        resolvedPath,
                        worksheetName,
                        0,
                        $"Could not find the legal-description header row in worksheet '{worksheetName}'. Required headers: 1/4, Sec, Twp, Rge, M.");
                }

                var rowInputs = new List<SectionRequestRowInput>();
                var sourceRowCount = 0;
                for (var rowIndex = headerRowIndex + 1; rowIndex < table.Rows.Count; rowIndex++)
                {
                    var quarter = GetCellString(table, rowIndex, quarterColumnIndex);
                    var section = GetCellString(table, rowIndex, sectionColumnIndex);
                    var township = GetCellString(table, rowIndex, townshipColumnIndex);
                    var range = GetCellString(table, rowIndex, rangeColumnIndex);
                    var meridian = GetCellString(table, rowIndex, meridianColumnIndex);

                    var anyValue =
                        !string.IsNullOrWhiteSpace(quarter) ||
                        !string.IsNullOrWhiteSpace(section) ||
                        !string.IsNullOrWhiteSpace(township) ||
                        !string.IsNullOrWhiteSpace(range) ||
                        !string.IsNullOrWhiteSpace(meridian);
                    if (!anyValue)
                    {
                        continue;
                    }

                    sourceRowCount++;
                    rowInputs.Add(new SectionRequestRowInput(meridian, range, township, section, quarter));
                }

                if (rowInputs.Count == 0)
                {
                    return Failure(
                        resolvedPath,
                        worksheetName,
                        0,
                        $"No legal-description rows were found below the header row in worksheet '{worksheetName}'.");
                }

                var parseResult = SectionRequestParser.Parse(zone, rowInputs);
                if (!parseResult.IsSuccess)
                {
                    return Failure(
                        resolvedPath,
                        worksheetName,
                        sourceRowCount,
                        BuildParseFailureMessage(parseResult));
                }

                var input = AtsBuildInputFactory.Create(
                    client,
                    zone,
                    textHeight: 10.0,
                    maxOverlapAttempts: 25,
                    sectionRequests: parseResult.Requests,
                    options: BuildPresetOptions());

                logger.WriteLine(
                    $"ATSBUILD_XLS workbook parsed: path={resolvedPath}, sheet={worksheetName}, client={client}, zone={zone}, sourceRows={sourceRowCount}, requests={input.SectionRequests.Count}.");

                return new AtsBuildExcelInputLoadResult(input, resolvedPath, worksheetName, sourceRowCount, string.Empty);
            }
            catch (Exception ex)
            {
                logger.WriteLine("ATSBUILD_XLS workbook load failed: " + ex);
                return Failure(resolvedPath, string.Empty, 0, ex.Message);
            }
        }

        private static AtsBuildExcelInputLoadResult Failure(string workbookPath, string worksheetName, int sourceRowCount, string errorMessage)
        {
            return new AtsBuildExcelInputLoadResult(
                input: null,
                workbookPath: workbookPath,
                worksheetName: worksheetName,
                sourceRowCount: sourceRowCount,
                errorMessage: errorMessage);
        }

        private static AtsBuildOptionSelection BuildPresetOptions()
        {
            return new AtsBuildOptionSelection
            {
                IncludeDispositionLinework = true,
                IncludeDispositionLabels = true,
                AllowMultiQuarterDispositions = false,
                IncludeAtsFabric = true,
                DrawLsdSubdivisionLines = true,
                IncludeP3Shapefiles = true,
                IncludeCompassMapping = true,
                IncludeCrownReservations = true,
                AutoCheckUpdateShapefilesAlways = false,
                CheckPlsr = false,
                IncludeSurfaceImpact = false,
                IncludeQuarterSectionLabels = true,
            };
        }

        private static string BuildConnectionString(string workbookPath)
        {
            var extension = Path.GetExtension(workbookPath)?.Trim().ToLowerInvariant() ?? string.Empty;
            var extendedProperties = extension switch
            {
                ".xls" => "Excel 8.0;HDR=NO;IMEX=1",
                ".xlsb" => "Excel 12.0;HDR=NO;IMEX=1",
                _ => "Excel 12.0 Xml;HDR=NO;IMEX=1",
            };

            return
                $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={workbookPath};Extended Properties=\"{extendedProperties}\";";
        }

        private static bool TryResolveWorksheetName(DataTable? schema, out string tableName, out string worksheetName)
        {
            tableName = string.Empty;
            worksheetName = string.Empty;
            if (schema == null || schema.Rows.Count == 0)
            {
                return false;
            }

            foreach (var preferredName in PreferredWorksheetNames)
            {
                foreach (DataRow row in schema.Rows)
                {
                    var rawTableName = Convert.ToString(row["TABLE_NAME"], CultureInfo.InvariantCulture) ?? string.Empty;
                    var normalizedTableName = NormalizeWorksheetTableName(rawTableName);
                    if (!string.Equals(normalizedTableName, preferredName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    tableName = rawTableName;
                    worksheetName = normalizedTableName;
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeWorksheetTableName(string rawTableName)
        {
            if (string.IsNullOrWhiteSpace(rawTableName))
            {
                return string.Empty;
            }

            var trimmed = rawTableName.Trim().Trim('\'');
            if (trimmed.EndsWith("$", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            return trimmed.Trim();
        }

        private static string GetCellString(DataTable table, int rowIndex, int columnIndex)
        {
            if (table == null || rowIndex < 0 || columnIndex < 0)
            {
                return string.Empty;
            }

            if (rowIndex >= table.Rows.Count || columnIndex >= table.Columns.Count)
            {
                return string.Empty;
            }

            var value = table.Rows[rowIndex][columnIndex];
            if (value == null || value == DBNull.Value)
            {
                return string.Empty;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        }

        private static bool TryParseZone(string raw, out int zone)
        {
            zone = 0;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out zone))
            {
                return zone > 0;
            }

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleZone))
            {
                zone = (int)Math.Round(doubleZone, MidpointRounding.AwayFromZero);
                return zone > 0;
            }

            return false;
        }

        private static bool TryFindHeaderRow(
            DataTable table,
            out int headerRowIndex,
            out int quarterColumnIndex,
            out int sectionColumnIndex,
            out int townshipColumnIndex,
            out int rangeColumnIndex,
            out int meridianColumnIndex)
        {
            headerRowIndex = -1;
            quarterColumnIndex = -1;
            sectionColumnIndex = -1;
            townshipColumnIndex = -1;
            rangeColumnIndex = -1;
            meridianColumnIndex = -1;

            if (table == null)
            {
                return false;
            }

            for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
            {
                var localQuarterColumnIndex = -1;
                var localSectionColumnIndex = -1;
                var localTownshipColumnIndex = -1;
                var localRangeColumnIndex = -1;
                var localMeridianColumnIndex = -1;

                for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
                {
                    var cell = GetCellString(table, rowIndex, columnIndex);
                    if (!TryNormalizeHeaderKey(cell, out var headerKey))
                    {
                        continue;
                    }

                    switch (headerKey)
                    {
                        case "quarter":
                            localQuarterColumnIndex = columnIndex;
                            break;
                        case "section":
                            localSectionColumnIndex = columnIndex;
                            break;
                        case "township":
                            localTownshipColumnIndex = columnIndex;
                            break;
                        case "range":
                            localRangeColumnIndex = columnIndex;
                            break;
                        case "meridian":
                            localMeridianColumnIndex = columnIndex;
                            break;
                    }
                }

                if (localQuarterColumnIndex >= 0 &&
                    localSectionColumnIndex >= 0 &&
                    localTownshipColumnIndex >= 0 &&
                    localRangeColumnIndex >= 0 &&
                    localMeridianColumnIndex >= 0)
                {
                    headerRowIndex = rowIndex;
                    quarterColumnIndex = localQuarterColumnIndex;
                    sectionColumnIndex = localSectionColumnIndex;
                    townshipColumnIndex = localTownshipColumnIndex;
                    rangeColumnIndex = localRangeColumnIndex;
                    meridianColumnIndex = localMeridianColumnIndex;
                    return true;
                }
            }

            return false;
        }

        private static bool TryNormalizeHeaderKey(string rawHeader, out string headerKey)
        {
            headerKey = string.Empty;
            if (string.IsNullOrWhiteSpace(rawHeader))
            {
                return false;
            }

            var normalized = NormalizeHeader(rawHeader);
            switch (normalized)
            {
                case "14":
                case "QUARTER":
                case "QTR":
                case "QUART":
                    headerKey = "quarter";
                    return true;
                case "SEC":
                case "SECTION":
                    headerKey = "section";
                    return true;
                case "TWP":
                case "TOWNSHIP":
                    headerKey = "township";
                    return true;
                case "RGE":
                case "RANGE":
                case "RG":
                    headerKey = "range";
                    return true;
                case "M":
                case "MER":
                case "MERIDIAN":
                    headerKey = "meridian";
                    return true;
                default:
                    return false;
            }
        }

        private static string NormalizeHeader(string rawHeader)
        {
            var chars = new List<char>(rawHeader.Length);
            foreach (var ch in rawHeader.ToUpperInvariant())
            {
                if (char.IsLetterOrDigit(ch))
                {
                    chars.Add(ch);
                }
            }

            return new string(chars.ToArray());
        }

        private static string BuildParseFailureMessage(SectionRequestParseResult parseResult)
        {
            return parseResult.Failure switch
            {
                SectionRequestParseFailure.MissingMeridianRangeTownship =>
                    "One or more worksheet rows are missing M/Rge/Twp values.",
                SectionRequestParseFailure.MissingSection =>
                    "One or more worksheet rows are missing Sec.",
                SectionRequestParseFailure.InvalidQuarter =>
                    $"Invalid 1/4 value '{parseResult.InvalidQuarterValue}'. Use NW, NE, SW, SE, N, S, E, W, or leave it blank for a full section.",
                _ => "Worksheet rows could not be converted into ATSBUILD section requests.",
            };
        }
    }
}
