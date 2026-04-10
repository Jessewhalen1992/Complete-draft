using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace Compass.Services;

public static class AutoCADHelper
{
    public static Transaction StartTransaction()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        return document.Database.TransactionManager.StartTransaction();
    }

    public static bool GetInsertionPoint(string promptMessage, out Point3d insertionPoint)
    {
        insertionPoint = Point3d.Origin;
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            return false;
        }

        var editor = document.Editor;
        var options = new PromptPointOptions("\n" + promptMessage);
        var result = editor.GetPoint(options);

        if (result.Status == PromptStatus.OK)
        {
            insertionPoint = result.Value;
            return true;
        }

        return false;
    }

    public static ObjectId InsertBlock(string blockName, Point3d insertionPoint, IReadOnlyDictionary<string, string> attributes, double scale = 1.0)
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var database = document.Database;
        var blockId = ObjectId.Null;

        using (document.LockDocument())
        using (var transaction = database.TransactionManager.StartTransaction())
        {
            if (!EnsureBlockIsLoaded(database, blockName))
            {
                return ObjectId.Null;
            }

            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            if (!blockTable.Has(blockName))
            {
                return ObjectId.Null;
            }

            var blockDefinitionId = blockTable[blockName];
            var blockDefinition = (BlockTableRecord)transaction.GetObject(blockDefinitionId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            var blockReference = new BlockReference(insertionPoint, blockDefinitionId)
            {
                ScaleFactors = new Scale3d(scale)
            };

            if (!CreateLayerIfMissing(database, "CG-NOTES"))
            {
                blockReference.Layer = "0";
            }
            else
            {
                blockReference.Layer = "CG-NOTES";
            }

            modelSpace.AppendEntity(blockReference);
            transaction.AddNewlyCreatedDBObject(blockReference, true);

            foreach (ObjectId id in blockDefinition)
            {
                if (transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition attributeDefinition)
                {
                    var attributeReference = new AttributeReference();
                    attributeReference.SetAttributeFromBlock(attributeDefinition, blockReference.BlockTransform);
                    if (attributes != null && attributes.TryGetValue(attributeDefinition.Tag, out var value))
                    {
                        attributeReference.TextString = value;
                    }
                    else
                    {
                        attributeReference.TextString = attributeDefinition.TextString;
                    }

                    attributeReference.Layer = blockReference.Layer;
                    blockReference.AttributeCollection.AppendAttribute(attributeReference);
                    transaction.AddNewlyCreatedDBObject(attributeReference, true);
                }
            }

            transaction.Commit();
            blockId = blockReference.ObjectId;
        }

        return blockId;
    }

    private static bool EnsureBlockIsLoaded(Database database, string blockName)
    {
        var exists = false;
        using (var transaction = database.TransactionManager.StartTransaction())
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            exists = blockTable.Has(blockName);
            transaction.Commit();
        }

        if (exists)
        {
            return true;
        }

        var baseFolder = @"C:\\AUTOCAD-SETUP\\Lisp_2000\\Drill Properties";
        var dwgPath = Path.Combine(baseFolder, blockName + ".dwg");
        if (!File.Exists(dwgPath))
        {
            return false;
        }

        return ImportBlockDefinition(database, dwgPath, blockName) != ObjectId.Null;
    }

    private static ObjectId ImportBlockDefinition(Database destinationDatabase, string sourceDrawingPath, string blockName)
    {
        if (destinationDatabase == null || string.IsNullOrEmpty(sourceDrawingPath) || !File.Exists(sourceDrawingPath))
        {
            return ObjectId.Null;
        }

        var result = ObjectId.Null;
        using (var sourceDatabase = new Database(false, true))
        {
            try
            {
                sourceDatabase.ReadDwgFile(sourceDrawingPath, FileShare.Read, allowCPConversion: true, password: string.Empty);
                using (var transaction = sourceDatabase.TransactionManager.StartTransaction())
                {
                    var blockTable = (BlockTable)transaction.GetObject(sourceDatabase.BlockTableId, OpenMode.ForRead);
                    if (!blockTable.Has(blockName))
                    {
                        return ObjectId.Null;
                    }

                    var blockDefinitionId = blockTable[blockName];
                    var idsToClone = new ObjectIdCollection { blockDefinitionId };
                    transaction.Commit();

                    var mapping = new IdMapping();
                    destinationDatabase.WblockCloneObjects(idsToClone, destinationDatabase.BlockTableId, mapping, DuplicateRecordCloning.Replace, false);
                }

                using (var destinationTransaction = destinationDatabase.TransactionManager.StartTransaction())
                {
                    var destinationBlockTable = (BlockTable)destinationTransaction.GetObject(destinationDatabase.BlockTableId, OpenMode.ForRead);
                    if (destinationBlockTable.Has(blockName))
                    {
                        result = destinationBlockTable[blockName];
                    }

                    destinationTransaction.Commit();
                }
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        return result;
    }

    private static bool CreateLayerIfMissing(Database database, string layerName)
    {
        var success = false;
        using (var transaction = database.TransactionManager.StartTransaction())
        {
            var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (layerTable.Has(layerName))
            {
                success = true;
            }
            else
            {
                layerTable.UpgradeOpen();
                var layerRecord = new LayerTableRecord { Name = layerName };
                layerTable.Add(layerRecord);
                transaction.AddNewlyCreatedDBObject(layerRecord, true);
                success = true;
            }

            transaction.Commit();
        }

        return success;
    }
}
