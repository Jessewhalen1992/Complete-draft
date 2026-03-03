using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace WildlifeSweeps
{
    internal static class BlockSelectionHelper
    {
        public static string GetEffectiveName(BlockReference block, Transaction tr)
        {
            if (block.IsDynamicBlock)
            {
                var record = (BlockTableRecord)tr.GetObject(block.DynamicBlockTableRecord, OpenMode.ForRead);
                return record.Name;
            }

            var definition = (BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead);
            return definition.Name;
        }

        public static List<ObjectId> PromptForBlocks(Document doc, Editor editor, string blockName)
        {
            var options = new PromptSelectionOptions
            {
                MessageForAdding = $"\nSelect {blockName} blocks (Enter for ALL in this space): ",
                AllowDuplicates = false
            };

            var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
            var result = editor.GetSelection(options, filter);

            var blocks = new List<ObjectId>();

            using var tr = doc.Database.TransactionManager.StartTransaction();
            var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForRead);

            if (result.Status == PromptStatus.OK)
            {
                foreach (SelectedObject? sel in result.Value)
                {
                    if (sel?.ObjectId == null)
                    {
                        continue;
                    }

                    var blockRef = tr.GetObject(sel.ObjectId, OpenMode.ForRead) as BlockReference;
                    if (blockRef != null && string.Equals(GetEffectiveName(blockRef, tr), blockName, StringComparison.OrdinalIgnoreCase))
                    {
                        blocks.Add(blockRef.ObjectId);
                    }
                }
            }
            else
            {
                foreach (ObjectId id in space)
                {
                    if (id.ObjectClass != RXObject.GetClass(typeof(BlockReference)))
                    {
                        continue;
                    }

                    var blockRef = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                    if (string.Equals(GetEffectiveName(blockRef, tr), blockName, StringComparison.OrdinalIgnoreCase))
                    {
                        blocks.Add(blockRef.ObjectId);
                    }
                }
            }

            tr.Commit();
            return blocks;
        }

        public static int? TryGetAttribute(BlockReference block, string tag, Transaction tr)
        {
            foreach (ObjectId attId in block.AttributeCollection)
            {
                if (!attId.IsValid)
                {
                    continue;
                }

                var attRef = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                if (attRef == null)
                {
                    continue;
                }

                if (string.Equals(attRef.Tag, tag, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(attRef.TextString, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }

            return null;
        }
    }
}
