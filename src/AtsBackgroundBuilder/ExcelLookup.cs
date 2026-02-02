using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.IO;

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

        public IReadOnlyList<string> GetAllValues()
        {
            return _values.AsReadOnly();
        }

        private void LoadWithOleDb(string xlsxPath, Logger logger)
        {
            var connString =
                "Provider=Microsoft.ACE.OLEDB.12.0;" +
                "Data Source=" + xlsxPath + ";" +
                "Extended Properties='Excel 12.0 Xml;HDR=YES;IMEX=1';";

            using (var connection = new OleDbConnection(connString))
            {
                connection.Open();
                var schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null);
                if (schema == null || schema.Rows.Count == 0)
                {
                    throw new InvalidOperationException("Excel file has no sheets.");
                }

                var sheetName = schema.Rows[0]["TABLE_NAME"].ToString();
                if (string.IsNullOrWhiteSpace(sheetName))
                {
                    throw new InvalidOperationException("Excel sheet name not found.");
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM [" + sheetName + "]";
                    using (var adapter = new OleDbDataAdapter(command))
                    {
                        var table = new DataTable();
                        adapter.Fill(table);
                        foreach (DataRow row in table.Rows)
                        {
                            var key = row.ItemArray.Length > 0 ? row[0]?.ToString() : null;
                            if (string.IsNullOrWhiteSpace(key))
                            {
                                continue;
                            }

                            var value = row.ItemArray.Length > 1 ? row[1]?.ToString() ?? string.Empty : string.Empty;
                            var extra = row.ItemArray.Length > 2 ? row[2]?.ToString() ?? string.Empty : string.Empty;

                            if (!_lookup.ContainsKey(key))
                            {
                                _lookup.Add(key, new LookupEntry(value, extra));
                                if (!string.IsNullOrWhiteSpace(value) && !_values.Contains(value))
                                {
                                    _values.Add(value);
                                }
                            }
                        }
                    }
                }
            }

            logger.WriteLine("Loaded lookup entries: " + _lookup.Count);
        }
    }
}

