using System;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Compass.Infrastructure;
using Compass.UI;
using Compass.ViewModels;

[assembly: CommandClass(typeof(Compass.CompassToolsApplication))]

namespace Compass;

public class CompassToolsApplication : IExtensionApplication
{
    private const string PrimaryRoot = @"C:\AUTOCAD-SETUP CG\CG_LISP";
    private const string FallbackRoot = @"C:\AUTOCAD-SETUP\Lisp_2000";

    private static readonly LispToolDefinition[] Tools =
    {
        new("rename-layouts", "Rename Layouts", "Rename/Renumber Layouts", "COMPASS\\MISC TOOLS\\Rename Layouts.fas", "RenameLayouts"),
        new("clean-drawing", "Clean Drawing", "Clean Drawings for Clients", "COMPASS\\MISC TOOLS\\cleandwg.fas", "cleandwg"),
        new("bend-table", "Bend Table", "Generate Bend Table With Bubbles", "COMPASS\\MISC TOOLS\\BendTable with bubbles.fas", "bt"),
        new("fnc", "FNC", "Convert linework to FNC Plan", "COMPASS\\MISC TOOLS\\FNC.fas", "FNC"),
        new("iop-maker", "IOP Maker", "Automatically Make IOP Page", "COMPASS\\MISC TOOLS\\iop maker.fas", "createiop"),
        new("dim-perp", "Dim Perp", "Measure From Point to a Perpendicular Line", "COMPASS\\MISC TOOLS\\DimPerp.fas", "dimperp"),
        new("renumber-workspace", "Renumber Workspace", "Renumber workspace based on a Polyline", "COMPASS\\MISC TOOLS\\RENUMBERWORKSPACE V1.fas", "RNW"),
        new("tie-text", "Tie Text", "Add \"(Tie)\" to end of a text", "COMPASS\\MISC TOOLS\\Tie.fas", "tie"),
        new("sum-values", "Sum Values", "Add up Values of Text/Mtext", "COMPASS\\MISC TOOLS\\sumvalue.fas", "sv"),
        new("delete-data-links", "Delete Data Links", "Remove all Data Links From Current Drawing", "COMPASS\\MISC TOOLS\\deletedatalinks.fas", "DDL"),
        new("convert-text-styles", "Convert Text Styles", "Convert Old Text Style to New", "COMPASS\\MISC TOOLS\\convertxt.fas", "CTS"),
        new("round-bearing-distances", "Round Bearing/Distances", "Round Survey B/Ds to Sketch B/Ds", "COMPASS\\MISC TOOLS\\rdtxt.fas", "rdtxt")
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

    [CommandMethod("Ctools", CommandFlags.Modal | CommandFlags.Session)]
    public static void ShowCompassTools()
    {
        CompassStartupDiagnostics.Log("Ctools command invoked.");

        try
        {
            EnsurePalette();
            UnifiedPaletteHost.ShowPalette("Tools");
            CompassStartupDiagnostics.Log("Ctools command completed.");
        }
        catch (System.Exception ex)
        {
            CompassStartupDiagnostics.LogException("Ctools command", ex);
            Application.ShowAlertDialog("Compass Tools failed to start. See " + CompassStartupDiagnostics.LogPath);
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
            TitleText = "Compass Tools",
            SubtitleText = "Select a program to launch."
        };

        _control.ModuleRequested += OnToolRequested;
        _control.LoadModules(Tools.Select((tool, index) => new CompassModuleDefinition(tool.Id, tool.DisplayName, tool.Description, index)));

        UnifiedPaletteHost.EnsurePalette();
        UnifiedPaletteHost.AddTab("Tools", _control);
    }

    private static void OnToolRequested(object? sender, string toolId)
    {
        var tool = Tools.FirstOrDefault(t => t.Id.Equals(toolId, StringComparison.OrdinalIgnoreCase));
        if (tool == null)
        {
            return;
        }

        LaunchTool(tool);
    }

    private static void LaunchTool(LispToolDefinition tool)
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            Application.ShowAlertDialog("Open a drawing first.");
            return;
        }

        var candidatePaths = new[]
        {
            Path.Combine(PrimaryRoot, tool.RelativePath),
            Path.Combine(FallbackRoot, tool.RelativePath)
        };

        var fullPath = candidatePaths.FirstOrDefault(File.Exists);

        if (string.IsNullOrWhiteSpace(fullPath))
        {
            var message = string.Join("\n", candidatePaths.Select(p => $" • {p}"));
            Application.ShowAlertDialog($"{tool.DisplayName} is unavailable because the script could not be found in either location:\n{message}");
            return;
        }

        var folder = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            TrustPathIfNeeded(folder);
        }

        var escapedPath = fullPath.Replace("\\", "\\\\");

        document.SendStringToExecute("\u001B\u001B", true, false, false);
        document.SendStringToExecute($"(load \"{escapedPath}\") ", true, false, false);
        document.SendStringToExecute($"{tool.CommandName}\n", true, false, false);
    }

    private static void TrustPathIfNeeded(string folder)
    {
        short secureLoad = 0;

        try
        {
            object value = Application.GetSystemVariable("SECURELOAD");
            secureLoad = Convert.ToInt16(value);
        }
        catch
        {
            // Ignore SECURELOAD probing failures.
        }

        if (secureLoad == 0)
        {
            return;
        }

        try
        {
            var current = Convert.ToString(Application.GetSystemVariable("TRUSTEDPATHS")) ?? string.Empty;
            var normalized = folder.EndsWith("\\", StringComparison.Ordinal) ? folder : folder + "\\";
            var paths = current.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim());

            if (!paths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                var updated = string.IsNullOrEmpty(current)
                    ? normalized
                    : $"{current};{normalized}";

                Application.SetSystemVariable("TRUSTEDPATHS", updated);
            }
        }
        catch
        {
            // Non-fatal. If TRUSTEDPATHS can't be updated the user can add it manually.
        }
    }

    private sealed class LispToolDefinition
    {
        public LispToolDefinition(string id, string displayName, string description, string relativePath, string commandName)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            RelativePath = relativePath;
            CommandName = commandName;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string Description { get; }

        public string RelativePath { get; }

        public string CommandName { get; }
    }
}
