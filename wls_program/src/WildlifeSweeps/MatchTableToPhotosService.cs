using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace WildlifeSweeps
{
    public sealed class MatchTableToPhotosService
    {
        public void Execute(Document doc, Editor editor)
        {
            if (doc == null)
            {
                return;
            }

            if (editor == null)
            {
                return;
            }

            using (doc.LockDocument())
            {
                var tableId = PromptForTable(editor);
                if (tableId.IsNull)
                {
                    return;
                }

                using var tr = doc.Database.TransactionManager.StartTransaction();
                if (tr.GetObject(tableId, OpenMode.ForRead, false) is not Table table || table.IsErased)
                {
                    editor.WriteMessage("\nThe selected object is not a readable AutoCAD table.");
                    return;
                }

                var captionsByPhotoNumber = ReadTableCaptionsByPhotoNumber(table);
                if (captionsByPhotoNumber.Count == 0)
                {
                    editor.WriteMessage("\nThe selected table does not contain any numbered finding rows with wording to match.");
                    return;
                }

                if (table.OwnerId.IsNull ||
                    tr.GetObject(table.OwnerId, OpenMode.ForWrite, false) is not BlockTableRecord ownerSpace)
                {
                    editor.WriteMessage("\nCould not open the selected table's drawing space.");
                    return;
                }

                var matchedTableNumbers = new HashSet<int>();
                var photoLabelCount = 0;
                var updatedCount = 0;
                var alreadyMatchedCount = 0;

                foreach (ObjectId entityId in ownerSpace)
                {
                    if (entityId.ObjectClass != Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(MText)))
                    {
                        continue;
                    }

                    if (tr.GetObject(entityId, OpenMode.ForWrite, false) is not MText label ||
                        label.IsErased ||
                        !string.Equals(label.Layer, PhotoLayoutHelper.PhotoLabelLayerName, StringComparison.OrdinalIgnoreCase) ||
                        !PhotoLayoutHelper.TryParsePhotoLabel(label, out var photoNumber, out _))
                    {
                        continue;
                    }

                    photoLabelCount++;
                    if (!captionsByPhotoNumber.TryGetValue(photoNumber, out var caption))
                    {
                        continue;
                    }

                    matchedTableNumbers.Add(photoNumber);
                    var updatedContents = PhotoLayoutHelper.BuildPhotoLabelContents(photoNumber, caption);
                    if (string.Equals(label.Contents ?? string.Empty, updatedContents, StringComparison.Ordinal))
                    {
                        alreadyMatchedCount++;
                        continue;
                    }

                    label.Contents = updatedContents;
                    updatedCount++;
                }

                if (photoLabelCount == 0)
                {
                    editor.WriteMessage("\nNo PHOTO # labels were found in the same space as the selected table.");
                    return;
                }

                tr.Commit();

                var unmatchedTableNumbers = captionsByPhotoNumber.Keys
                    .Where(number => !matchedTableNumbers.Contains(number))
                    .OrderBy(number => number)
                    .ToList();

                editor.WriteMessage(
                    $"\nMatch Table to Photos complete. Updated {updatedCount} label(s), already matched {alreadyMatchedCount}, table rows without PHOTO labels {unmatchedTableNumbers.Count}.");

                if (unmatchedTableNumbers.Count > 0)
                {
                    var preview = string.Join(", ", unmatchedTableNumbers.Take(10));
                    editor.WriteMessage($"\nTable numbers with no matching PHOTO label: {preview}{(unmatchedTableNumbers.Count > 10 ? ", ..." : string.Empty)}");
                }
            }
        }

        private static ObjectId PromptForTable(Editor editor)
        {
            var options = new PromptEntityOptions("\nSelect summary table to match to photo labels: ");
            options.SetRejectMessage("\nOnly AutoCAD tables are supported.");
            options.AddAllowedClass(typeof(Table), false);

            var result = editor.GetEntity(options);
            return result.Status == PromptStatus.OK ? result.ObjectId : ObjectId.Null;
        }

        private static Dictionary<int, string> ReadTableCaptionsByPhotoNumber(Table table)
        {
            var captionsByPhotoNumber = new Dictionary<int, string>();
            for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
            {
                var numberText = GetTableCellText(table, rowIndex, 0);
                if (!int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var photoNumber))
                {
                    continue;
                }

                var caption = GetTableCellText(table, rowIndex, 1);
                if (string.IsNullOrWhiteSpace(caption))
                {
                    continue;
                }

                captionsByPhotoNumber[photoNumber] = caption;
            }

            return captionsByPhotoNumber;
        }

        private static string GetTableCellText(Table table, int rowIndex, int columnIndex)
        {
            return (table.Cells[rowIndex, columnIndex].TextString ?? string.Empty).Trim();
        }
    }
}
