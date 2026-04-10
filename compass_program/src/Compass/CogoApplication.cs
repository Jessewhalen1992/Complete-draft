using System;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Compass.Infrastructure;
using Compass.Modules;
using Compass.UI;
using Compass.ViewModels;

[assembly: CommandClass(typeof(Compass.CogoApplication))]

namespace Compass;

public class CogoApplication : IExtensionApplication
{
    private const string PrimaryRoot = @"C:\AUTOCAD-SETUP CG\CG_LISP";
    private const string FallbackRoot = @"C:\AUTOCAD-SETUP\Lisp_2000";

    private static readonly CogoToolDefinition[] Tools =
    {
        new ManagedModuleTool("cogo-ui", "COGO UI", "COGO PROGRAM WITH UI", new CogoProgramModule()),
        new LispToolDefinition("set-cogo-units", "Set COGO Units", "Set Units for Below COGO Buttons", "COMPASS\\COGO\\cogo.fas", "COGOUNITS"),
        new LispToolDefinition("azimuth", "Azimuth-traverse", "Create an Azimuth Polyline", "COMPASS\\COGO\\cogo.fas", "AZ"),
        new LispToolDefinition("bearing-traverse", "Bearing Traverse", "Create an Bearing Traverse Polyline", "COMPASS\\COGO\\cogo.fas", "BRG"),
        new LispToolDefinition("angle-traverse", "Angle Traverse", "Create an Angle Traverse Polyline", "COMPASS\\COGO\\cogo.fas", "AT"),
        new LispToolDefinition("meters-to-feet", "Meters to Feet", "Convert Text from meters to feet", "COMPASS\\COGO\\metersToFeet.fas", "M2ft")
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

    [CommandMethod("Ccogo", CommandFlags.Modal | CommandFlags.Session)]
    public static void ShowCogo()
    {
        CompassStartupDiagnostics.Log("Ccogo command invoked.");

        try
        {
            EnsurePalette();
            UnifiedPaletteHost.ShowPalette("COGO");
            CompassStartupDiagnostics.Log("Ccogo command completed.");
        }
        catch (System.Exception ex)
        {
            CompassStartupDiagnostics.LogException("Ccogo command", ex);
            Application.ShowAlertDialog("COGO failed to start. See " + CompassStartupDiagnostics.LogPath);
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
            TitleText = "COGO",
            SubtitleText = "Select a COGO tool to launch."
        };

        _control.ModuleRequested += OnToolRequested;
        _control.LoadModules(Tools.Select((tool, index) =>
            new CompassModuleDefinition(tool.Id, tool.DisplayName, tool.Description, index)));

        UnifiedPaletteHost.EnsurePalette();
        UnifiedPaletteHost.AddTab("COGO", _control);
    }

    private static void OnToolRequested(object? sender, string toolId)
    {
        var tool = Tools.FirstOrDefault(t => t.Id.Equals(toolId, StringComparison.OrdinalIgnoreCase));
        if (tool == null)
        {
            return;
        }

        switch (tool)
        {
            case ManagedModuleTool managedTool:
                managedTool.Module.Show();
                break;
            case LispToolDefinition lispTool:
                LaunchTool(lispTool);
                break;
        }
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

    private abstract class CogoToolDefinition
    {
        protected CogoToolDefinition(string id, string displayName, string description)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string Description { get; }
    }

    private sealed class ManagedModuleTool : CogoToolDefinition
    {
        public ManagedModuleTool(string id, string displayName, string description, ICompassModule module)
            : base(id, displayName, description)
        {
            Module = module;
        }

        public ICompassModule Module { get; }
    }

    private sealed class LispToolDefinition : CogoToolDefinition
    {
        public LispToolDefinition(string id, string displayName, string description, string relativePath, string commandName)
            : base(id, displayName, description)
        {
            RelativePath = relativePath;
            CommandName = commandName;
        }

        public string RelativePath { get; }

        public string CommandName { get; }
    }
}
