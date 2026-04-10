using System;
using System.Drawing;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Windows;
using Compass.Infrastructure;
using Compass.Infrastructure.Logging;
using Compass.Services;
using Compass.UI;
using Compass.ViewModels;

namespace Compass.Modules;

public class DrillManagerModule : ICompassModule
{
    private PaletteSet? _paletteSet;
    private DrillManagerControl? _control;
    private readonly JsonSettingsService _settingsService;
    private readonly ILog _log;
    private bool _stateLoaded;
    private string? _activeDocumentName;

    public string Id => "drill-manager";
    public string DisplayName => "Drill Manager";
    public string Description => "Manage Drill names and Generate Coordinates";

    public DrillManagerModule()
    {
        CompassEnvironment.Initialize();
        _log = CompassEnvironment.Log;
        _settingsService = new JsonSettingsService(CompassEnvironment.AppSettings);

        try
        {
            var documentManager = Application.DocumentManager;
            _activeDocumentName = documentManager.MdiActiveDocument?.Name;
            documentManager.DocumentActivated += OnDocumentActivated;
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to subscribe to document events: {ex.Message}");
        }
    }

    internal DrillManagerControl Control => _control ??= CreateControl();

    public void Show()
    {
        var control = Control;
        EnsureStateLoaded(control.ViewModel);

        if (_paletteSet == null)
        {
            _paletteSet = CreatePalette();
            _paletteSet.AddVisual("Drill Manager", control);
        }

        _paletteSet.Visible = true;
        _paletteSet.Activate(0);
    }

    private PaletteSet CreatePalette()
    {
        var palette = new PaletteSet(DisplayName, new Guid("879b5f68-8aa6-4b67-86f0-744f30c58f7b"))
        {
            Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowPropertiesMenu | PaletteSetStyles.Snappable,
            DockEnabled = DockSides.Left | DockSides.Right | DockSides.Top | DockSides.Bottom,
            MinimumSize = new Size(360, 480)
        };

        return palette;
    }

    private DrillManagerControl CreateControl()
    {
        var control = new DrillManagerControl();
        EnsureStateLoaded(control.ViewModel);
        return control;
    }

    private void EnsureStateLoaded(DrillManagerViewModel viewModel)
    {
        viewModel.StateChanged -= OnViewModelStateChanged;
        viewModel.StateChanged += OnViewModelStateChanged;

        if (_stateLoaded)
        {
            return;
        }

        try
        {
            var state = _settingsService.Load(DrillManagerViewModel.MaximumDrills);
            viewModel.ApplyState(state);
            _stateLoaded = true;
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to load drill state: {ex.Message}");
            _stateLoaded = true;
        }
    }

    public void SaveState()
    {
        if (_control == null)
        {
            return;
        }

        try
        {
            var state = _control.ViewModel.CaptureState();
            _settingsService.Save(state);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to save drill state", ex);
        }
    }

    private void OnViewModelStateChanged(object? sender, EventArgs e)
    {
        if (sender is not DrillManagerViewModel viewModel)
        {
            return;
        }

        try
        {
            var state = viewModel.CaptureState();
            _settingsService.Save(state);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to save drill state", ex);
        }
    }

    private void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
    {
        var documentName = e.Document?.Name;
        if (string.Equals(_activeDocumentName, documentName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _activeDocumentName = documentName;
        _stateLoaded = false;

        if (_control is not { } control)
        {
            return;
        }

        var dispatcher = control.Dispatcher;
        if (dispatcher?.CheckAccess() == true)
        {
            EnsureStateLoaded(control.ViewModel);
        }
        else
        {
            dispatcher?.BeginInvoke(new Action(() => EnsureStateLoaded(control.ViewModel)));
        }
    }
}
