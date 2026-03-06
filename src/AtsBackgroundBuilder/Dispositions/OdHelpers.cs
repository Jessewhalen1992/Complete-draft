/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Gis.Map;
using Autodesk.Gis.Map.ObjectData;
using Autodesk.Gis.Map.Utilities;

namespace AtsBackgroundBuilder.Dispositions
{
    public static class OdHelpers
    {
        private static readonly object FailedOdTablesLock = new object();
        private static readonly HashSet<string> FailedOdTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static Dictionary<string, string>? ReadObjectData(ObjectId objectId, Logger logger)
        {
            try
            {
                var project = HostMapApplicationServices.Application?.ActiveProject;
                if (project == null)
                {
                    return null;
                }

                var tables = project.ODTables;
                var tableNames = tables.GetTableNames();
                if (tableNames == null || tableNames.Count == 0)
                {
                    return null;
                }

                foreach (var tableNameObj in tableNames)
                {
                    var tableName = tableNameObj as string ?? tableNameObj?.ToString();
                    if (string.IsNullOrWhiteSpace(tableName))
                    {
                        continue;
                    }

                    if (IsFailedTable(tableName))
                    {
                        continue;
                    }

                    Autodesk.Gis.Map.ObjectData.Table table;
                    try
                    {
                        table = tables[tableName];
                    }
                    catch (Exception ex)
                    {
                        MarkTableFailed(tableName, logger, "OD table lookup failed for '", ex);
                        continue;
                    }

                    using (table)
                    using (var records = GetRecordsForObject(table, tableName, objectId, logger))
                    {
                        if (records == null || records.Count == 0)
                        {
                            continue;
                        }

                        var record = records[0];
                        var fieldDefinitions = table.FieldDefinitions;
                        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        var fieldCount = Math.Min(record.Count, fieldDefinitions.Count);
                        for (var i = 0; i < fieldCount; i++)
                        {
                            var field = record[i];
                            var fieldName = fieldDefinitions[i].Name;
                            if (string.IsNullOrWhiteSpace(fieldName))
                            {
                                continue;
                            }

                            dict[fieldName] = MapValueToString(field);
                        }

                        if (dict.Count > 0)
                        {
                            return dict;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.WriteLine("OD read failed: " + ex.Message);
            }

            return null;
        }

        private static Records? GetRecordsForObject(Autodesk.Gis.Map.ObjectData.Table table, ObjectId objectId, Logger logger)
        {
            try
            {
                return table.GetObjectTableRecords(0, objectId, Autodesk.Gis.Map.Constants.OpenMode.OpenForRead, false);
            }
            catch (Exception ex)
            {
                logger.WriteLine("OD GetObjectRecords failed: " + ex.Message);
                return null;
            }
        }

        private static Records? GetRecordsForObject(
            Autodesk.Gis.Map.ObjectData.Table table,
            string tableName,
            ObjectId objectId,
            Logger logger)
        {
            try
            {
                return table.GetObjectTableRecords(0, objectId, Autodesk.Gis.Map.Constants.OpenMode.OpenForRead, false);
            }
            catch (Exception ex)
            {
                MarkTableFailed(tableName, logger, "OD GetObjectRecords failed for '", ex);
                return null;
            }
        }

        private static bool IsFailedTable(string tableName)
        {
            lock (FailedOdTablesLock)
            {
                return FailedOdTables.Contains(tableName);
            }
        }

        private static void MarkTableFailed(string tableName, Logger logger, string prefix, Exception ex)
        {
            var shouldLog = false;
            lock (FailedOdTablesLock)
            {
                if (!FailedOdTables.Contains(tableName))
                {
                    FailedOdTables.Add(tableName);
                    shouldLog = true;
                }
            }

            if (shouldLog)
            {
                logger.WriteLine(prefix + tableName + "': " + ex.Message);
            }
        }

        private static string MapValueToString(MapValue value)
        {
            switch (value.Type)
            {
                case Autodesk.Gis.Map.Constants.DataType.Integer:
                    return value.Int32Value.ToString();
                case Autodesk.Gis.Map.Constants.DataType.Real:
                    return value.DoubleValue.ToString();
                case Autodesk.Gis.Map.Constants.DataType.Character:
                    return value.StrValue ?? string.Empty;
                default:
                    return value.ToString() ?? string.Empty;
            }
        }
    }
}

/////////////////////////////////////////////////////////////////////
