using System;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Compass.Infrastructure;
using Compass.UI;
using Compass.ViewModels;

[assembly: CommandClass(typeof(Compass.CompassLegalApplication))]

namespace Compass;

public class CompassLegalApplication : IExtensionApplication
{
    // Folders where the scripts are expected to be found
    private const string PrimaryRoot = @"C:\AUTOCAD-SETUP CG\CG_LISP";
    private const string FallbackRoot = @"C:\AUTOCAD-SETUP\Lisp_2000";

    // Each tool: id, title, description, base file name, command name
    private static readonly LispToolDefinition[] LegalTools =
    {
        new("plan-to-legal", "Plan to Legal", "Convert Plan to Legal Plan", "leglayers", "legf"),
        new("legal-layer-convert", "Legal Layer Convert", "Convert Layers to Legal Layers", "PLTO", "PLTO"),
        new("lto-check", "LTO Check", "Check LTO Layers (Use in Paper Space)", "LTO_Check", "check")
    };

    private static CompassControl? _control;

    public void Initialize() => EnsurePalette();

    public void Terminate()
    {
        if (_control != null)
        {
            _control.ModuleRequested -= OnToolRequested;
            _control = null;
        }
    }

    [CommandMethod("Clegal", CommandFlags.Modal | CommandFlags.Session)]
    public static void ShowCompassLegal()
    {
        CompassStartupDiagnostics.Log("Clegal command invoked.");

        try
        {
            EnsurePalette();
            UnifiedPaletteHost.ShowPalette("Legal");
            CompassStartupDiagnostics.Log("Clegal command completed.");
        }
        catch (System.Exception ex)
        {
            CompassStartupDiagnostics.LogException("Clegal command", ex);
            Application.ShowAlertDialog("Compass Legal failed to start. See " + CompassStartupDiagnostics.LogPath);
        }
    }

    private static void EnsurePalette()
    {
        if (_control != null) return;

        _control = new CompassControl
        {
            TitleText = "Compass Legal",
            SubtitleText = "Select a legal tool to launch."
        };

        _control.ModuleRequested += OnToolRequested;
        _control.LoadModules(LegalTools.Select((tool, index) =>
            new CompassModuleDefinition(tool.Id, tool.DisplayName, tool.Description, index)));

        UnifiedPaletteHost.EnsurePalette();
        UnifiedPaletteHost.AddTab("Legal", _control);
    }

    private static void OnToolRequested(object? sender, string toolId)
    {
        var tool = LegalTools.FirstOrDefault(t => t.Id.Equals(toolId, StringComparison.OrdinalIgnoreCase));
        if (tool != null) LaunchTool(tool);
    }

    private static void LaunchTool(LispToolDefinition tool)
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            Application.ShowAlertDialog("Open a drawing first.");
            return;
        }

        // Build candidate file paths: try .fas then .lsp in both roots
        var candidates = new[]
        {
            Path.Combine(PrimaryRoot, tool.BaseFileName + ".fas"),
            Path.Combine(PrimaryRoot, tool.BaseFileName + ".lsp"),
            Path.Combine(FallbackRoot, tool.BaseFileName + ".fas"),
            Path.Combine(FallbackRoot, tool.BaseFileName + ".lsp")
        };

        var fullPath = candidates.FirstOrDefault(File.Exists);

        if (string.IsNullOrWhiteSpace(fullPath))
        {
            var msg = string.Join("\n", candidates.Select(p => $" • {p}"));
            Application.ShowAlertDialog($"{tool.DisplayName} is unavailable; none of the following files were found:\n{msg}");
            return;
        }

        // Trust the folder if secure load is on
        var folder = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(folder)) TrustPathIfNeeded(folder);

        var escapedPath = fullPath.Replace("\\", "\\\\");
        // Cancel any current command
        doc.SendStringToExecute("\u001B\u001B", true, false, false);
        // Load the script
        doc.SendStringToExecute($"(load \"{escapedPath}\") ", true, false, false);
        // Run the command with an actual newline terminator
        doc.SendStringToExecute($"{tool.CommandName}\n", true, false, false);
    }

    // Add the folder to TRUSTEDPATHS when SECURELOAD is enabled
    private static void TrustPathIfNeeded(string folder)
    {
        short secureLoad = 0;
        try
        {
            object value = Application.GetSystemVariable("SECURELOAD");
            secureLoad = Convert.ToInt16(value);
        }
        catch { }

        if (secureLoad == 0) return;

        try
        {
            var current = Convert.ToString(Application.GetSystemVariable("TRUSTEDPATHS")) ?? string.Empty;
            var normalized = folder.EndsWith("\\", StringComparison.Ordinal) ? folder : folder + "\\";
            var paths = current.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim());
            if (!paths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                var updated = string.IsNullOrEmpty(current) ? normalized : $"{current};{normalized}";
                Application.SetSystemVariable("TRUSTEDPATHS", updated);
            }
        }
        catch { /* Non‑fatal: user can add trusted paths manually */ }
    }

    private sealed class LispToolDefinition
    {
        public LispToolDefinition(string id, string displayName, string description, string baseFileName, string commandName)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            BaseFileName = baseFileName;
            CommandName = commandName;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string Description { get; }

        public string BaseFileName { get; }

        public string CommandName { get; }
    }
}
