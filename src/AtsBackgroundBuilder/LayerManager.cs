/////////////////////////////////////////////////////////////////////

using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Colors;

namespace AtsBackgroundBuilder
{
    public sealed class LayerManager
    {
        private readonly Database _database;

        public LayerManager(Database database)
        {
            _database = database;
        }

        public ObjectId EnsureLayer(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
            {
                return _database.Clayer;
            }

            using (var transaction = _database.TransactionManager.StartTransaction())
            {
                var table = (LayerTable)transaction.GetObject(_database.LayerTableId, OpenMode.ForRead);
                if (table.Has(layerName))
                {
                    return table[layerName];
                }

                table.UpgradeOpen();
                var record = new LayerTableRecord
                {
                    Name = layerName,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, 7)
                };
                var id = table.Add(record);
                transaction.AddNewlyCreatedDBObject(record, true);
                transaction.Commit();
                return id;
            }
        }

        public static string NormalizeSuffix(string? suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix))
            {
                return string.Empty;
            }

            var normalized = suffix.Trim();
            if (normalized.StartsWith("-", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(1);
            }

            return normalized.Trim();
        }
    }
}

/////////////////////////////////////////////////////////////////////
