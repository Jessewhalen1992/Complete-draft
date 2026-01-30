using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Gis.Map;
using Autodesk.Gis.Map.ObjectData;

namespace AtsBackgroundBuilder
{
    public static class OdHelpers
    {
        public static Dictionary<string, string>? ReadObjectData(ObjectId objectId, Logger logger)
        {
            try
            {
                var tables = HostMapApplicationServices.Application.ActiveProject.ODTables;
                foreach (Table table in tables)
                {
                    var records = GetRecordsForObject(table, objectId, logger);
                    if (records == null || records.Count == 0)
                    {
                        continue;
                    }

                    var record = records[0];
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < record.Count; i++)
                    {
                        var field = record[i];
                        dict[field.FieldName] = field.Value?.ToString() ?? string.Empty;
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

        private static Records? GetRecordsForObject(Table table, ObjectId objectId, Logger logger)
        {
            try
            {
                return table.GetObjectRecords(0, objectId, Autodesk.Gis.Map.Constants.OpenMode.OpenForRead, false);
            }
            catch (Exception)
            {
                try
                {
                    return table.GetObjectRecords(objectId, Autodesk.Gis.Map.Constants.OpenMode.OpenForRead, false);
                }
                catch (Exception ex)
                {
                    logger.WriteLine("OD GetObjectRecords failed: " + ex.Message);
                    return null;
                }
            }
        }
    }
}
