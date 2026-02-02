using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Gis.Map;
using Autodesk.Gis.Map.ObjectData;
using Autodesk.Gis.Map.Utilities;

namespace AtsBackgroundBuilder
{
    public static class OdHelpers
    {
        public static Dictionary<string, string>? ReadObjectData(ObjectId objectId, Logger logger)
        {
            try
            {
                var tables = HostMapApplicationServices.Application.ActiveProject.ODTables;
                var tableNames = tables.GetTableNames();
                if (tableNames == null || tableNames.Count == 0)
                {
                    return null;
                }

                foreach (var tableName in tableNames)
                {
                    Autodesk.Gis.Map.ObjectData.Table table = tables[tableName];
                    var records = GetRecordsForObject(table, objectId, logger);
                    if (records == null || records.Count == 0)
                    {
                        continue;
                    }

                    var record = records[0];
                    var fieldDefinitions = table.FieldDefinitions;
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < record.Count; i++)
                    {
                        var field = record[i];
                        var fieldName = fieldDefinitions[i].Name;
                        dict[fieldName] = MapValueToString(field);
                    }

                    return dict;
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
                    return value.ToString();
            }
        }
    }
}

