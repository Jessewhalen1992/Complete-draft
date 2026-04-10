using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Compass.Infrastructure.Acad;
using Compass.Infrastructure.Logging;

namespace Compass.Services;

public class CoordinateTableService
{
    private readonly ILog _log;

    public CoordinateTableService(ILog log)
    {
        _log = log;
    }

    public List<string> GetBottomHoleValues(Table table)
    {
        var results = new List<string>();
        if (table == null)
        {
            return results;
        }

        for (var row = 0; row < table.Rows.Count; row++)
        {
            var value = table.Cells[row, 0].TextString.Trim().ToUpperInvariant();
            if (value.Contains("BOTTOMHOLE"))
            {
                results.Add(table.Cells[row, 1].TextString.Trim());
            }
        }

        return results;
    }

    public Table? PromptForTable(string prompt)
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var editor = document.Editor;
        var options = new PromptEntityOptions(prompt)
        {
            AllowObjectOnLockedLayer = true
        };
        options.SetRejectMessage("Only table entities allowed");
        options.AddAllowedClass(typeof(Table), exactMatch: true);

        var result = editor.GetEntity(options);
        if (result.Status != PromptStatus.OK)
        {
            return null;
        }

        return AcadContext.Run(document.Database, write: false, transaction =>
            transaction.GetObject(result.ObjectId, OpenMode.ForRead) as Table);
    }
}
