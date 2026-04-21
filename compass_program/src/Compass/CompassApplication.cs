using Compass;
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Compass.Infrastructure;
using Compass.Modules;
using Compass.UI;
using Compass.ViewModels;

[assembly: ExtensionApplication(typeof(Compass.CompassApplication))]
[assembly: CommandClass(typeof(Compass.CompassApplication))]
[assembly: CommandClass(typeof(Compass.BuildDrillCommandBridge))]

namespace Compass;

public class CompassApplication : IExtensionApplication
{
    private static readonly Dictionary<string, ICompassModule> Modules = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<ICompassModule> ModuleList = new();
    private static CompassControl? _compassControl;

    public void Initialize()
    {
        CompassStartupDiagnostics.Log("CompassApplication.Initialize started.");
        CompassEnvironment.Initialize();
        if (CompassHostMode.IsHeadless)
        {
            CompassStartupDiagnostics.Log("CompassApplication.Initialize detected headless host mode; skipping palette initialization.");
            return;
        }

        EnsureModules();
        EnsureCompassPaletteTab();

        // Initialize companion tabs so the full palette is ready immediately after NETLOAD.
        TryInitializeCompanionTab("Format Tables", () => new FormatTablesApplication().Initialize());
        TryInitializeCompanionTab("Tools", () => new CompassToolsApplication().Initialize());
        TryInitializeCompanionTab("Legal", () => new CompassLegalApplication().Initialize());
        TryInitializeCompanionTab("COGO", () => new CogoApplication().Initialize());
        UnifiedPaletteHost.ShowPalette("Compass");
        CompassStartupDiagnostics.Log("CompassApplication.Initialize completed.");
    }

    public void Terminate()
    {
        try
        {
            var drillManagerModule = ModuleList.OfType<DrillManagerModule>().FirstOrDefault();
            drillManagerModule?.SaveState();
        }
        catch (System.Exception)
        {
            // ignore shutdown failures
        }

        if (_compassControl != null)
        {
            _compassControl.ModuleRequested -= OnModuleRequested;
            _compassControl = null;
        }
    }

    [CommandMethod("Compass", CommandFlags.Modal | CommandFlags.Session)]
    public static void ShowCompass()
    {
        CompassStartupDiagnostics.Log("Compass command invoked.");

        try
        {
            EnsureModules();
            EnsureCompassPaletteTab();
            UnifiedPaletteHost.ShowPalette("Compass");
            CompassStartupDiagnostics.Log("Compass command completed.");
        }
        catch (System.Exception ex)
        {
            CompassStartupDiagnostics.LogException("Compass command", ex);
            Application.ShowAlertDialog("Compass failed to start. See " + CompassStartupDiagnostics.LogPath);
        }
    }

    private static void EnsureModules()
    {
        if (Modules.Count > 0)
        {
            return;
        }

        CompassStartupDiagnostics.Log("Registering Compass modules.");
        RegisterModule(new DrillManagerModule());
        RegisterModule(new ProfileManagerModule());
        RegisterModule(new SectionGeneratorModule());
        RegisterModule(new SurfaceDevelopmentModule());
        RegisterModule(new CrossingManagerModule());
        RegisterModule(new HybridManagerModule());
        RegisterModule(new WorkspaceManagerModule());
        RegisterModule(new AreaManagerModule());
        RegisterModule(new OnestopManagerModule());
        // Register the Surface Impact Table module so it's available under the Compass tab
        RegisterModule(new SurfaceImpactTableModule());
    }

    public static void RegisterModule(ICompassModule module)
    {
        if (Modules.TryGetValue(module.Id, out var existing))
        {
            ModuleList.Remove(existing);
        }

        Modules[module.Id] = module;
        ModuleList.Add(module);

        if (_compassControl != null)
        {
            var definitions = ModuleList
                .Select((m, index) => new CompassModuleDefinition(m.Id, m.DisplayName, m.Description, index))
                .ToArray();

            _compassControl.LoadModules(definitions);
        }
    }

    private static void EnsureCompassPaletteTab()
    {
        if (_compassControl != null)
        {
            return;
        }

        CompassStartupDiagnostics.Log("Creating Compass palette tab.");
        _compassControl = new CompassControl();
        _compassControl.ModuleRequested += OnModuleRequested;

        var definitions = ModuleList
            .Select((module, index) => new CompassModuleDefinition(module.Id, module.DisplayName, module.Description, index))
            .ToArray();

        _compassControl.LoadModules(definitions);

        UnifiedPaletteHost.EnsurePalette();
        UnifiedPaletteHost.AddTab("Compass", _compassControl);
    }

    private static void OnModuleRequested(object? sender, string moduleId)
    {
        if (Modules.TryGetValue(moduleId, out var module))
        {
            module.Show();
        }
    }

    internal static DrillManagerModule GetDrillManagerModule()
    {
        EnsureModules();

        var module = ModuleList.OfType<DrillManagerModule>().FirstOrDefault();
        if (module == null)
        {
            module = new DrillManagerModule();
            RegisterModule(module);
        }

        return module;
    }

    private static void TryInitializeCompanionTab(string tabName, Action initialize)
    {
        CompassStartupDiagnostics.Log("Initializing companion tab '" + tabName + "'.");

        try
        {
            initialize();
            CompassStartupDiagnostics.Log("Initialized companion tab '" + tabName + "'.");
        }
        catch (System.Exception ex)
        {
            CompassStartupDiagnostics.LogException("Initialize companion tab '" + tabName + "'", ex);
        }
    }
}
