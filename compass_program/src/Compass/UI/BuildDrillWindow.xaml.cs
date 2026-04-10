using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using Autodesk.AutoCAD.EditorInput;
using Compass.Infrastructure;
using Compass.Models;
using Compass.ViewModels;

using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;
using AutoCADApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Compass.UI;

public partial class BuildDrillWindow : Window
{
    public BuildDrillWindow(IEnumerable<string> drillNames)
    {
        InitializeComponent();
        ViewModel = new BuildDrillDialogViewModel(drillNames);
        DataContext = ViewModel;

        try
        {
            var mainWindow = AutoCADApplication.MainWindow;
            if (mainWindow != null)
            {
                var helper = new WindowInteropHelper(this)
                {
                    Owner = mainWindow.Handle
                };
            }
        }
        catch
        {
            // Best-effort owner assignment only; ShowDialog still works without it.
        }
    }

    public BuildDrillDialogViewModel ViewModel { get; }

    public BuildDrillRequest? Result { get; private set; }

    private void Build_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.TryCreateRequest(out var request, out var error) || request == null)
        {
            MessageBox.Show(error, "Build a Drill", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = request;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void GetSurface_Click(object sender, RoutedEventArgs e)
    {
        var document = AcadApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            MessageBox.Show("No active AutoCAD document is available.", "Build a Drill", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var windowHandle = new WindowInteropHelper(this).Handle;
            CompassStartupDiagnostics.Log("Build a Drill surface pick starting.");

            using var interaction = document.Editor.StartUserInteraction(windowHandle);
            var result = document.Editor.GetPoint(new PromptPointOptions("\nPick surface point for this drill:"));
            CompassStartupDiagnostics.Log($"Build a Drill surface pick completed with status {result.Status}.");
            if (result.Status == PromptStatus.OK)
            {
                ViewModel.SetSurfacePoint(result.Value);
            }
        }
        catch (Exception ex)
        {
            CompassStartupDiagnostics.LogException("Build a Drill surface pick", ex);
            MessageBox.Show($"Could not pick a surface point: {ex.Message}", "Build a Drill", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearSurface_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearSurfacePoint();
    }
}
