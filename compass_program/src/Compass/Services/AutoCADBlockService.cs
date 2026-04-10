using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Compass.Infrastructure.Acad;

namespace Compass.Services;

public class AutoCADBlockService
{
    public string GetAttributeValue(BlockReference blockReference, string tag, Transaction transaction)
    {
        if (blockReference == null)
        {
            throw new ArgumentNullException(nameof(blockReference));
        }

        if (transaction == null)
        {
            throw new ArgumentNullException(nameof(transaction));
        }

        foreach (ObjectId attributeId in blockReference.AttributeCollection)
        {
            if (transaction.GetObject(attributeId, OpenMode.ForRead) is AttributeReference attributeReference &&
                TagMatches(attributeReference.Tag, tag))
            {
                return attributeReference.TextString.Trim();
            }
        }

        return string.Empty;
    }

    public void SwapAttribute(BlockReference blockReference, string tag, string newValue, Transaction transaction)
    {
        if (blockReference == null)
        {
            throw new ArgumentNullException(nameof(blockReference));
        }

        if (transaction == null)
        {
            throw new ArgumentNullException(nameof(transaction));
        }

        foreach (ObjectId attributeId in blockReference.AttributeCollection)
        {
            if (transaction.GetObject(attributeId, OpenMode.ForWrite) is AttributeReference attributeReference &&
                TagMatches(attributeReference.Tag, tag))
            {
                attributeReference.TextString = newValue;
            }
        }
    }

    public List<BlockReference> GetBlocksOnLayer(Database database, string layer)
    {
        if (database == null)
        {
            throw new ArgumentNullException(nameof(database));
        }

        if (string.IsNullOrWhiteSpace(layer))
        {
            throw new ArgumentException("Layer is required.", nameof(layer));
        }

        return AcadContext.Run(database, write: false, transaction =>
        {
            var results = new List<BlockReference>();
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in modelSpace)
            {
                if (transaction.GetObject(id, OpenMode.ForRead) is BlockReference block &&
                    block.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(block);
                }
            }

            return results;
        });
    }

    public static bool TagMatches(string attributeTag, string targetTag)
    {
        return NormalizeTag(attributeTag).Equals(NormalizeTag(targetTag), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTag(string tag)
    {
        return string.IsNullOrWhiteSpace(tag)
            ? string.Empty
            : tag.Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .Trim();
    }
}
