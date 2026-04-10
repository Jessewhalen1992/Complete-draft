using System;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Compass.Infrastructure;
using Compass.UI;
using Compass.ViewModels;

[assembly: CommandClass(typeof(Compass.FormatTablesApplication))]

namespace Compass;

public class FormatTablesApplication : IExtensionApplication
{
    private static readonly MacroToolDefinition[] FormatTableTools =
    {
        new(
            "format-surface-impact-box",
            "Format Surface Impact Box",
            "Fix Surface Impact Cell and Text Sizes",
            @"sift"),
        new(
            "format-hybrid-table",
            "Format Hybrid Table",
            "Fix Hybrid Cell and Text Sizes",
            @"HFT"),
        new(
            "format-well-coordinate-table",
            "Format Well Coordinate Table",
            "Fix Well Coordinate Cell and Text Sizes",
            @"WFT"),
        new(
            "format-workspace-table",
            "Format Workspace Table",
            "Fix Workspace Cell and Text Sizes",
            @"WSFT")
    };

    private static CompassControl? _control;

    public void Initialize()
    {
        EnsurePalette();
    }

    public void Terminate()
    {
        if (_control != null)
        {
            _control.ModuleRequested -= OnToolRequested;
            _control = null;
        }
    }

    [CommandMethod("Cformat", CommandFlags.Modal | CommandFlags.Session)]
    public static void ShowFormatTables()
    {
        CompassStartupDiagnostics.Log("Cformat command invoked.");

        try
        {
            EnsurePalette();
            UnifiedPaletteHost.ShowPalette("Format Tables");
            CompassStartupDiagnostics.Log("Cformat command completed.");
        }
        catch (System.Exception ex)
        {
            CompassStartupDiagnostics.LogException("Cformat command", ex);
            Application.ShowAlertDialog("Format Tables failed to start. See " + CompassStartupDiagnostics.LogPath);
        }
    }

    private static void EnsurePalette()
    {
        if (_control != null)
        {
            return;
        }

        _control = new CompassControl
        {
            TitleText = "FORMAT TABLES",
            SubtitleText = "Select a formatting tool to launch."
        };

        _control.ModuleRequested += OnToolRequested;
        _control.LoadModules(FormatTableTools.Select((tool, index) =>
            new CompassModuleDefinition(tool.Id, tool.DisplayName, tool.Description, index)));

        UnifiedPaletteHost.EnsurePalette();
        UnifiedPaletteHost.AddTab("Format Tables", _control);
    }

    private static void OnToolRequested(object? sender, string toolId)
    {
        var tool = FormatTableTools.FirstOrDefault(t => t.Id.Equals(toolId, StringComparison.OrdinalIgnoreCase));
        if (tool == null)
        {
            return;
        }

        LaunchTool(tool);
    }

    private static void LaunchTool(MacroToolDefinition tool)
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            Application.ShowAlertDialog("Open a drawing first.");
            return;
        }

        document.SendStringToExecute("\u001B\u001B", true, false, false);
        document.SendStringToExecute($"{tool.Macro}\n", true, false, false);
    }

    private sealed class MacroToolDefinition
    {
        public MacroToolDefinition(string id, string displayName, string description, string macro)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            Macro = macro;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string Description { get; }

        public string Macro { get; }
    }
}
