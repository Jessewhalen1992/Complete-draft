using System;
using System.Collections.Generic;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Compass.Infrastructure.Logging;

using AutoCADApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Compass.Services;

public sealed class DrillUpdateResult
{
    public DrillUpdateResult(bool success, int updatedAttributes, int matchingUpdates)
    {
        Success = success;
        UpdatedAttributes = updatedAttributes;
        MatchingUpdates = matchingUpdates;
    }

    public bool Success { get; }

    public int UpdatedAttributes { get; }

    public int MatchingUpdates { get; }

    public static DrillUpdateResult Failed() => new(false, 0, 0);
}

public class DrillAttributeSyncService
{
    private readonly AutoCADBlockService _blockService;
    private readonly ILog _log;

    public DrillAttributeSyncService(ILog log, AutoCADBlockService blockService)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _blockService = blockService ?? throw new ArgumentNullException(nameof(blockService));
    }

    public IReadOnlyList<string>? GetDrillNamesFromSelection(int drillCount)
    {
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            MessageBox.Show("No active AutoCAD document is available.", "Update", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        if (drillCount <= 0)
        {
            return new string[0];
        }

        var editor = document.Editor;
        var selection = editor.GetSelection();
        if (selection.Status != PromptStatus.OK)
        {
            MessageBox.Show("No objects selected.", "Update", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        try
        {
            var values = new string[drillCount];
            var updated = new bool[drillCount];

            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null)
                    {
                        continue;
                    }

                    if (transaction.GetObject(selected.ObjectId, OpenMode.ForRead) is not BlockReference block)
                    {
                        continue;
                    }

                    for (var index = 0; index < drillCount; index++)
                    {
                        var tag = $"DRILL_{index + 1}";
                        var value = _blockService.GetAttributeValue(block, tag, transaction);
                        if (!string.IsNullOrEmpty(value))
                        {
                            values[index] = value;
                            updated[index] = true;
                        }
                    }
                }

                transaction.Commit();
            }

            for (var i = 0; i < drillCount; i++)
            {
                if (!updated[i])
                {
                    values[i] = $"DRILL_{i + 1}";
                }
            }

            return values;
        }
        catch (System.Exception ex)
        {
            _log.Error("Failed to update drill names from block attributes.", ex);
            MessageBox.Show($"Error updating from block attributes: {ex.Message}", "Update", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    public DrillUpdateResult SetDrillName(int index, string newName, string oldName, bool updateMatchingValues)
    {
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            MessageBox.Show("No active AutoCAD document is available.", "Set Drill", MessageBoxButton.OK, MessageBoxImage.Warning);
            return DrillUpdateResult.Failed();
        }

        var database = document.Database;
        var trimmedNew = newName?.Trim() ?? string.Empty;
        var trimmedOld = oldName?.Trim() ?? string.Empty;

        try
        {
            var updatedAttributes = 0;
            var matchingUpdates = 0;

            using (document.LockDocument())
            {
                using (var transaction = database.TransactionManager.StartTransaction())
                {
                    updatedAttributes = UpdateDrillAttribute(transaction, database, index, trimmedNew);
                    transaction.Commit();
                }

                if (updateMatchingValues)
                {
                    matchingUpdates = UpdateMatchingAttributes(document, trimmedOld, trimmedNew);
                }
            }

            return new DrillUpdateResult(true, updatedAttributes, matchingUpdates);
        }
        catch (System.Exception ex)
        {
            _log.Error($"Error setting DRILL_{index}: {ex.Message}", ex);
            MessageBox.Show($"An error occurred while setting drill attributes: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return DrillUpdateResult.Failed();
        }
    }

    public bool SwapDrillNames(int firstIndex, int secondIndex, string firstName, string secondName)
    {
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            MessageBox.Show("No active AutoCAD document is available.", "Swap", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var database = document.Database;
        var firstTag = $"DRILL_{firstIndex}";
        var secondTag = $"DRILL_{secondIndex}";
        var normalizedFirst = firstName?.Trim() ?? string.Empty;
        var normalizedSecond = secondName?.Trim() ?? string.Empty;

        try
        {
            using (document.LockDocument())
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId blockId in blockTable)
                {
                    BlockTableRecord record;
                    try
                    {
                        record = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
                    }
                    catch
                    {
                        continue;
                    }

                    if (record == null || record.IsErased)
                    {
                        continue;
                    }

                    foreach (ObjectId entityId in record)
                    {
                        var blockReference = TryGetForWrite<BlockReference>(transaction, database, entityId);
                        if (blockReference == null || blockReference.IsErased)
                        {
                            continue;
                        }

                        SwapAttribute(blockReference, transaction, database, firstTag, normalizedSecond);
                        SwapAttribute(blockReference, transaction, database, secondTag, normalizedFirst);
                    }
                }

                transaction.Commit();
            }

            return true;
        }
        catch (System.Exception ex)
        {
            _log.Error($"Failed to swap drill names {firstTag} and {secondTag}.", ex);
            MessageBox.Show($"Error swapping drill names: {ex.Message}", "Swap", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private int UpdateDrillAttribute(Transaction transaction, Database database, int index, string newValue)
    {
        UnlockCgNotesLayer(transaction, database);

        var updated = 0;
        var tag = $"DRILL_{index}";
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId blockId in blockTable)
        {
            BlockTableRecord record;
            try
            {
                record = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            }
            catch
            {
                continue;
            }

            if (record == null || record.IsErased)
            {
                continue;
            }

            foreach (ObjectId entityId in record)
            {
                var blockReference = TryGetForWrite<BlockReference>(transaction, database, entityId);
                if (blockReference == null || blockReference.IsErased)
                {
                    continue;
                }

                foreach (ObjectId attributeId in blockReference.AttributeCollection)
                {
                    var attribute = TryGetForWrite<AttributeReference>(transaction, database, attributeId);
                    if (attribute == null || attribute.IsErased)
                    {
                        continue;
                    }

                    if (AutoCADBlockService.TagMatches(attribute.Tag, tag))
                    {
                        attribute.TextString = newValue;
                        updated++;
                    }
                }
            }
        }

        return updated;
    }

    private int UpdateMatchingAttributes(Document document, string oldValue, string newValue)
    {
        if (string.IsNullOrWhiteSpace(oldValue) || string.Equals(oldValue, newValue, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var trimmedOld = oldValue.Trim();
        var trimmedNew = newValue.Trim();
        var database = document.Database;
        var updated = 0;

        using (var transaction = database.TransactionManager.StartTransaction())
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId blockId in blockTable)
            {
                BlockTableRecord record;
                try
                {
                    record = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
                }
                catch
                {
                    continue;
                }

                if (record == null || record.IsErased)
                {
                    continue;
                }

                foreach (ObjectId entityId in record)
                {
                    var entity = TryGetForWrite<Entity>(transaction, database, entityId);
                    if (entity == null || entity.IsErased)
                    {
                        continue;
                    }

                    if (entity is BlockReference blockReference)
                    {
                        foreach (ObjectId attributeId in blockReference.AttributeCollection)
                        {
                            var attribute = TryGetForWrite<AttributeReference>(transaction, database, attributeId);
                            if (attribute == null || attribute.IsErased)
                            {
                                continue;
                            }

                            if (string.Equals(attribute.TextString.Trim(), trimmedOld, StringComparison.OrdinalIgnoreCase))
                            {
                                attribute.TextString = trimmedNew;
                                updated++;
                            }
                        }
                    }
                    else if (entity is MText mText)
                    {
                        if (string.Equals(mText.Contents.Trim(), trimmedOld, StringComparison.OrdinalIgnoreCase))
                        {
                            mText.Contents = trimmedNew;
                            updated++;
                        }
                    }
                }
            }

            transaction.Commit();
        }

        return updated;
    }

    private void UnlockCgNotesLayer(Transaction transaction, Database database)
    {
        const string targetLayer = "CG-NOTES";
        var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        if (!layerTable.Has(targetLayer))
        {
            return;
        }

        var layerRecord = (LayerTableRecord)transaction.GetObject(layerTable[targetLayer], OpenMode.ForWrite);
        if (layerRecord.IsLocked)
        {
            layerRecord.IsLocked = false;
        }
    }

    private static T? TryGetForWrite<T>(Transaction transaction, Database database, ObjectId objectId)
        where T : DBObject
    {
        try
        {
            return transaction.GetObject(objectId, OpenMode.ForWrite, false) as T;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.OnLockedLayer)
        {
            return database.TransactionManager.GetObject(objectId, OpenMode.ForWrite, false, true) as T;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.WasErased)
        {
            return null;
        }
    }

    private void SwapAttribute(BlockReference blockReference, Transaction transaction, Database database, string tag, string newValue)
    {
        foreach (ObjectId attributeId in blockReference.AttributeCollection)
        {
            var attribute = TryGetForWrite<AttributeReference>(transaction, database, attributeId);
            if (attribute == null || attribute.IsErased)
            {
                continue;
            }

            if (AutoCADBlockService.TagMatches(attribute.Tag, tag))
            {
                attribute.TextString = newValue;
            }
        }
    }
}
