using System.Windows;
using System.Windows.Interop;
using Compass.Models;
using Compass.ViewModels;

using AutoCADApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Compass.UI;

public partial class ExistingWellTableWindow : Window
{
    public ExistingWellTableWindow()
    {
        InitializeComponent();
        ViewModel = new ExistingWellTableDialogViewModel();
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

    public ExistingWellTableDialogViewModel ViewModel { get; }

    public ExistingWellTableConfiguration? Result { get; private set; }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.TryCreateConfiguration(out var configuration, out var error) || configuration == null)
        {
            MessageBox.Show(error, "Existing Well Table", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = configuration;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
