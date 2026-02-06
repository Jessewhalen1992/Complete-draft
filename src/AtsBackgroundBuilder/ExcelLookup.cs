/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.IO;
using System.Linq;

namespace AtsBackgroundBuilder
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
#if NET8_0_WINDOWS
                lookup.LoadWithOleDb(xlsxPath, logger);
#else
                lookup.LoadWithOleDb(xlsxPath, logger);
#endif
                lookup.IsLoaded = true;
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

        private void LoadWithOleDb(string xlsxPath, Logger logger)
        {
            // HDR=NO so first row is treated as data (lookup files are headerless)
            var connString =
                $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={xlsxPath};Extended Properties=\"Excel 12.0 Xml;HDR=NO;IMEX=1\";";

            using (var conn = new OleDbConnection(connString))
            {
                conn.Open();

                // Discover first worksheet name
                var schema = conn.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null);
                if (schema == null || schema.Rows.Count == 0)
                {
                    logger.WriteLine("No worksheets found in: " + xlsxPath);
                    return;
                }

                var sheetName = schema.Rows[0]["TABLE_NAME"].ToString();
                if (string.IsNullOrWhiteSpace(sheetName))
                {
                    logger.WriteLine("Could not determine worksheet name in: " + xlsxPath);
                    return;
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT * FROM [{sheetName}]";

                    using (var adapter = new OleDbDataAdapter(cmd))
                    {
                        var dt = new DataTable();
                        adapter.Fill(dt);

                        if (dt.Columns.Count < 2)
                        {
                            logger.WriteLine("Lookup has fewer than 2 columns: " + xlsxPath);
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

            logger.WriteLine($"Loaded lookup ({_lookup.Count} rows): {xlsxPath}");
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
