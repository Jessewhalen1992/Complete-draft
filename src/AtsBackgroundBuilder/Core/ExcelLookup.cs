/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.IO;
using System.Linq;

namespace AtsBackgroundBuilder.Core
{
    public sealed class LookupEntry
    {
        public LookupEntry(string value, string extra)
        {
            Value = value;
            Extra = extra;
        }

        public string Value { get; }
        public string Extra { get; }
    }

    /// <summary>
    /// Simple XLSX lookup reader:
    /// - Column 1: key
    /// - Column 2: value
    /// - Column 3 (optional): extra
    ///
    /// This loader uses HDR=NO to support headerless sheets (your current CompanyLookup.xlsx / PurposeLookup.xlsx).
    /// If the sheet has headers, typical header rows are auto-skipped.
    /// </summary>
    public sealed class ExcelLookup
    {
        private readonly Dictionary<string, LookupEntry> _lookup = new Dictionary<string, LookupEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _values = new List<string>();

        public bool IsLoaded { get; private set; }

        public static ExcelLookup Load(string xlsxPath, Logger logger)
        {
            var lookup = new ExcelLookup();
            if (!File.Exists(xlsxPath))
            {
                logger.WriteLine("Lookup not found: " + xlsxPath);
                return lookup;
            }

            try
            {
                var snapshotPath = CreateTemporarySnapshot(xlsxPath, logger);
                try
                {
#if NET8_0_WINDOWS
                    lookup.LoadWithOleDb(snapshotPath, logger, xlsxPath);
#else
                    lookup.LoadWithOleDb(snapshotPath, logger, xlsxPath);
#endif
                    lookup.IsLoaded = true;
                }
                finally
                {
                    TryDeleteTemporarySnapshot(snapshotPath, logger);
                }
            }
            catch (Exception ex)
            {
                logger.WriteLine("Lookup load failed: " + ex.Message);
            }

            return lookup;
        }

        public LookupEntry? Lookup(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (_lookup.TryGetValue(key.Trim(), out var entry))
            {
                return entry;
            }

            return null;
        }

        public IReadOnlyList<string> Values => _values;

        public IReadOnlyList<string> ValuesByExtra(string extraValue)
        {
            if (string.IsNullOrWhiteSpace(extraValue))
            {
                return Array.Empty<string>();
            }

            var marker = extraValue.Trim();
            return _lookup.Values
                .Where(e => string.Equals((e.Extra ?? string.Empty).Trim(), marker, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string CreateTemporarySnapshot(string xlsxPath, Logger logger)
        {
            var tempFolder = Path.Combine(Path.GetTempPath(), "AtsBackgroundBuilderLookup");
            Directory.CreateDirectory(tempFolder);

            var extension = Path.GetExtension(xlsxPath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".xlsx";
            }

            var tempPath = Path.Combine(
                tempFolder,
                $"{Path.GetFileNameWithoutExtension(xlsxPath)}-{Guid.NewGuid():N}{extension}");

            using (var source = new FileStream(
                       xlsxPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       bufferSize: 81920,
                       options: FileOptions.SequentialScan))
            using (var destination = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 81920,
                       options: FileOptions.SequentialScan))
            {
                source.CopyTo(destination);
            }

            logger.WriteLine("Lookup snapshot created: " + xlsxPath);
            return tempPath;
        }

        private static void TryDeleteTemporarySnapshot(string snapshotPath, Logger logger)
        {
            try
            {
                File.Delete(snapshotPath);
            }
            catch (Exception ex)
            {
                logger.WriteLine("Lookup snapshot cleanup failed: " + ex.Message);
            }
        }

        private void LoadWithOleDb(string xlsxPath, Logger logger, string displayPath)
        {
            // HDR=NO so first row is treated as data (lookup files are headerless)
            var builder = new OleDbConnectionStringBuilder
            {
                Provider = "Microsoft.ACE.OLEDB.12.0",
                DataSource = xlsxPath
            };
            builder["Extended Properties"] = "Excel 12.0 Xml;HDR=NO;IMEX=1";
            builder["Mode"] = "Read";

            using (var conn = new OleDbConnection(builder.ConnectionString))
            {
                conn.Open();

                // Discover first worksheet name
                var schema = conn.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null);
                if (schema == null || schema.Rows.Count == 0)
                {
                    logger.WriteLine("No worksheets found in: " + displayPath);
                    return;
                }

                var sheetName = schema.Rows[0]["TABLE_NAME"].ToString();
                if (string.IsNullOrWhiteSpace(sheetName))
                {
                    logger.WriteLine("Could not determine worksheet name in: " + displayPath);
                    return;
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT * FROM [{sheetName}]";
                    cmd.CommandTimeout = 5;

                    using (var adapter = new OleDbDataAdapter(cmd))
                    {
                        var dt = new DataTable();
                        adapter.Fill(dt);

                        if (dt.Columns.Count < 2)
                        {
                            logger.WriteLine("Lookup has fewer than 2 columns: " + displayPath);
                            return;
                        }

                        foreach (DataRow row in dt.Rows)
                        {
                            var key = row[0]?.ToString()?.Trim();
                            var value = row[1]?.ToString()?.Trim();
                            var extra = dt.Columns.Count >= 3 ? row[2]?.ToString()?.Trim() : "";

                            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                            {
                                continue;
                            }

                            if (IsHeaderRow(key, value))
                            {
                                continue;
                            }

                            if (!_lookup.ContainsKey(key))
                            {
                                _lookup[key] = new LookupEntry(value, extra ?? "");
                            }

                            if (!_values.Contains(value))
                            {
                                _values.Add(value);
                            }
                        }
                    }
                }
            }

            logger.WriteLine($"Loaded lookup ({_lookup.Count} rows): {displayPath}");
        }

        private static bool IsHeaderRow(string key, string value)
        {
            // CompanyLookup typical headers
            if (key.Equals("key", StringComparison.OrdinalIgnoreCase) && value.Equals("value", StringComparison.OrdinalIgnoreCase))
                return true;

            // PurposeLookup typical headers
            if (key.Equals("purpcd", StringComparison.OrdinalIgnoreCase))
                return true;

            // Generic
            if (key.Equals("company", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }
}
