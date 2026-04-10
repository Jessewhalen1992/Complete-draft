using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Compass.ViewModels;

namespace Compass.UI;

public partial class CompassControl : UserControl
{
    private readonly ObservableCollection<CompassModuleDefinition> _modules = new();

    public CompassControl()
    {
        InitializeComponent();
        ModulesList.ItemsSource = _modules;
    }

    public static readonly DependencyProperty TitleTextProperty = DependencyProperty.Register(
        nameof(TitleText), typeof(string), typeof(CompassControl), new PropertyMetadata("Compass"));

    public static readonly DependencyProperty SubtitleTextProperty = DependencyProperty.Register(
        nameof(SubtitleText), typeof(string), typeof(CompassControl), new PropertyMetadata("Select a program to launch."));

    public string TitleText
    {
        get => (string)GetValue(TitleTextProperty);
        set => SetValue(TitleTextProperty, value);
    }

    public string SubtitleText
    {
        get => (string)GetValue(SubtitleTextProperty);
        set => SetValue(SubtitleTextProperty, value);
    }

    public event EventHandler<string>? ModuleRequested;

    public void LoadModules(params CompassModuleDefinition[] modules)
    {
        LoadModules(modules.AsEnumerable());
    }

    public void LoadModules(System.Collections.Generic.IEnumerable<CompassModuleDefinition> modules)
    {
        _modules.Clear();
        foreach (var module in modules.OrderBy(m => m.DisplayOrder))
        {
            _modules.Add(module);
        }
    }

    private void OnModuleButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string id)
        {
            ModuleRequested?.Invoke(this, id);
        }
    }
}
