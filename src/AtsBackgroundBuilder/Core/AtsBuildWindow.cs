using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace AtsBackgroundBuilder.Core
{
    public sealed class AtsBuildWindow : Window
    {
        private readonly ComboBox _clientCombo = new ComboBox();
        private readonly RadioButton _zone11Radio = new RadioButton();
        private readonly RadioButton _zone12Radio = new RadioButton();
        private readonly TextBox _textHeightBox = new TextBox();
        private readonly TextBox _maxAttemptsBox = new TextBox();
        private readonly CheckBox _includeDispoLinework = new CheckBox();
        private readonly CheckBox _includeDispoLabels = new CheckBox();
        private readonly CheckBox _includeAtsFabric = new CheckBox();
        private readonly CheckBox _includeLsds = new CheckBox();
        private readonly CheckBox _includeP3Shapes = new CheckBox();
        private readonly CheckBox _includeCompassMapping = new CheckBox();
        private readonly CheckBox _includeCrownReservations = new CheckBox();
        private readonly CheckBox _checkPlsr = new CheckBox();
        private readonly CheckBox _includeSurfaceImpact = new CheckBox();
        private readonly CheckBox _allowMultiQuarterDispositions = new CheckBox();
        private readonly CheckBox _includeQuarterSectionLabels = new CheckBox();
        private readonly CheckBox _autoCheckUpdateShapesAlways = new CheckBox();
        private readonly ComboBox _shapeTypeCombo = new ComboBox();
        private readonly Button _updateShape = new Button();
        private readonly Button _addSectionsFromBoundary = new Button();
        private readonly Button _build = new Button();
        private readonly Button _cancel = new Button();
        private readonly DataGrid _grid = new DataGrid();
        private readonly ObservableCollection<GridInputRow> _rows = new ObservableCollection<GridInputRow>();
        private readonly Config _config;
        private readonly List<string> _lastSelectedPlsrXmlPaths = new List<string>();
        private bool _explicitCancelRequested;
        private bool _buildRequested;
        private bool _buildAttempted;
        private bool _boundaryImportRoundTripUsed;
        private string _buildAttemptTrace = "none";
        private static readonly object PersistedPlsrStateLock = new object();
        private static bool PersistedPlsrStateLoaded;
        private static readonly List<string> PersistedPlsrXmlPaths = new List<string>();
        private static string PersistedPlsrStateFilePath = string.Empty;

        private static readonly string[] DispositionShapeUpdateSourceRoots =
        {
            @"N:\Mapping\FTP Updates\AltaLIS",
            @"O:\Mapping\FTP Updates\AltaLIS",
        };
        private static readonly string[] CompassMappingShapeUpdateSourceRoots =
        {
            @"N:\Mapping\Mapping\COMPASS_SURVEYED\SHP",
            @"O:\Mapping\Mapping\COMPASS_SURVEYED\SHP",
        };
        private static readonly string[] CrownReservationsShapeUpdateSourceRoots =
        {
            @"N:\Mapping\FTP Updates\GoA",
            @"O:\Mapping\FTP Updates\GoA",
        };
        private static readonly string[] CompassMappingShapeBaseNames =
        {
            "SURVEYED_POLYGON_N83UTMZ11",
            "SURVEYED_POLYGON_N83UTMZ12",
        };
        private static readonly string[] CrownReservationsShapeBaseNames =
        {
            "CrownLandReservations",
        };

        private const string DispositionShapeDestinationFolder = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\DISPOS";
        private const string CompassMappingShapeDestinationFolder = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\COMPASS MAPPING";
        private const string CrownReservationsShapeDestinationFolder = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\CLR";

        public AtsBuildWindow(IEnumerable<string> clientNames, Config config, AtsBuildInput? seedInput = null)
        {
            _config = config ?? new Config();
            Title = "PREDRAFT BUILDER";
            Width = 1300;
            Height = 780;
            MinWidth = 1080;
            MinHeight = 660;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));

            if (TryGetPersistedPlsrXmlPaths(out var persistedPlsrXmlPaths))
            {
                _lastSelectedPlsrXmlPaths.AddRange(persistedPlsrXmlPaths);
            }

            BuildLayout(clientNames, _config);

            if (seedInput != null)
            {
                ApplySeedInput(seedInput);
            }

            Closing += (_, __) =>
            {
                if (Result == null && !_buildRequested && !_buildAttempted)
                {
                    _explicitCancelRequested = true;
                }
            };
        }

        public AtsBuildInput? Result { get; private set; }
        public bool ExplicitCancelRequested => _explicitCancelRequested;
        public bool BuildRequested => _buildRequested;
        public bool BuildAttempted => _buildAttempted;
        public bool BoundaryImportRoundTripUsed => _boundaryImportRoundTripUsed;
        public string BuildAttemptTrace => _buildAttemptTrace;

        private void ApplySeedInput(AtsBuildInput seedInput)
        {
            if (seedInput == null)
            {
                return;
            }

            var seedClient = seedInput.CurrentClient?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(seedClient))
            {
                var matched = false;
                foreach (var item in _clientCombo.Items)
                {
                    if (string.Equals(item?.ToString(), seedClient, StringComparison.OrdinalIgnoreCase))
                    {
                        _clientCombo.SelectedItem = item;
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    _clientCombo.Items.Add(seedClient);
                    _clientCombo.SelectedItem = seedClient;
                }
            }

            _zone12Radio.IsChecked = seedInput.Zone == 12;
            _zone11Radio.IsChecked = seedInput.Zone != 12;
            _textHeightBox.Text = seedInput.TextHeight > 0
                ? seedInput.TextHeight.ToString("0.##", CultureInfo.InvariantCulture)
                : "10";
            _maxAttemptsBox.Text = Math.Max(1, seedInput.MaxOverlapAttempts).ToString(CultureInfo.InvariantCulture);

            _includeDispoLinework.IsChecked = seedInput.IncludeDispositionLinework;
            _includeDispoLabels.IsChecked = seedInput.IncludeDispositionLabels;
            _includeAtsFabric.IsChecked = seedInput.IncludeAtsFabric;
            _includeLsds.IsChecked = seedInput.DrawLsdSubdivisionLines;
            _includeP3Shapes.IsChecked = seedInput.IncludeP3Shapefiles;
            _includeCompassMapping.IsChecked = seedInput.IncludeCompassMapping;
            _includeCrownReservations.IsChecked = seedInput.IncludeCrownReservations;
            _autoCheckUpdateShapesAlways.IsChecked = seedInput.AutoCheckUpdateShapefilesAlways;
            _checkPlsr.IsChecked = seedInput.CheckPlsr;
            _includeSurfaceImpact.IsChecked = seedInput.IncludeSurfaceImpact;
            _allowMultiQuarterDispositions.IsChecked = seedInput.AllowMultiQuarterDispositions;
            _includeQuarterSectionLabels.IsChecked = seedInput.IncludeQuarterSectionLabels;

            _rows.Clear();
            if (seedInput.SectionRequests.Count == 0)
            {
                _rows.Add(new GridInputRow());
            }
            else
            {
                foreach (var request in seedInput.SectionRequests)
                {
                    _rows.Add(new GridInputRow
                    {
                        M = request.Key.Meridian ?? string.Empty,
                        RGE = request.Key.Range ?? string.Empty,
                        TWP = request.Key.Township ?? string.Empty,
                        SEC = request.Key.Section ?? string.Empty,
                        HQ = QuarterToGridToken(request.Quarter),
                    });
                }
            }

            _lastSelectedPlsrXmlPaths.Clear();
            foreach (var xmlPath in seedInput.PlsrXmlPaths.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)))
            {
                _lastSelectedPlsrXmlPaths.Add(xmlPath);
            }

            if (_lastSelectedPlsrXmlPaths.Count > 0)
            {
                PersistPlsrXmlPaths(_lastSelectedPlsrXmlPaths);
            }
        }

        private static string QuarterToGridToken(QuarterSelection quarter)
        {
            return quarter switch
            {
                QuarterSelection.NorthWest => "NW",
                QuarterSelection.NorthEast => "NE",
                QuarterSelection.SouthWest => "SW",
                QuarterSelection.SouthEast => "SE",
                QuarterSelection.NorthHalf => "N",
                QuarterSelection.SouthHalf => "S",
                QuarterSelection.EastHalf => "E",
                QuarterSelection.WestHalf => "W",
                QuarterSelection.All => "ALL",
                _ => "ALL",
            };
        }

        public static bool TryGetPersistedPlsrXmlPaths(out List<string> xmlPaths)
        {
            EnsurePersistedPlsrStateLoaded();
            lock (PersistedPlsrStateLock)
            {
                var validPaths = PersistedPlsrXmlPaths
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (validPaths.Count != PersistedPlsrXmlPaths.Count)
                {
                    PersistedPlsrXmlPaths.Clear();
                    PersistedPlsrXmlPaths.AddRange(validPaths);
                    SavePersistedPlsrXmlPathsUnsafe();
                }

                xmlPaths = validPaths;
                return xmlPaths.Count > 0;
            }
        }

        private static void PersistPlsrXmlPaths(IEnumerable<string> xmlPaths)
        {
            EnsurePersistedPlsrStateLoaded();
            lock (PersistedPlsrStateLock)
            {
                PersistedPlsrXmlPaths.Clear();
                if (xmlPaths != null)
                {
                    foreach (var path in xmlPaths)
                    {
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && !PersistedPlsrXmlPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                        {
                            PersistedPlsrXmlPaths.Add(path);
                        }
                    }
                }

                SavePersistedPlsrXmlPathsUnsafe();
            }
        }

        private static void EnsurePersistedPlsrStateLoaded()
        {
            lock (PersistedPlsrStateLock)
            {
                if (PersistedPlsrStateLoaded)
                {
                    return;
                }

                PersistedPlsrXmlPaths.Clear();
                PersistedPlsrStateFilePath = ResolvePersistedPlsrStateFilePath();
                if (File.Exists(PersistedPlsrStateFilePath))
                {
                    try
                    {
                        foreach (var line in File.ReadAllLines(PersistedPlsrStateFilePath))
                        {
                            var candidate = line?.Trim();
                            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate) && !PersistedPlsrXmlPaths.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                            {
                                PersistedPlsrXmlPaths.Add(candidate);
                            }
                        }
                    }
                    catch
                    {
                        // best effort only
                    }
                }

                PersistedPlsrStateLoaded = true;
            }
        }

        private static string ResolvePersistedPlsrStateFilePath()
        {
            try
            {
                var dllFolder = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory;
                return Path.Combine(dllFolder, "AtsBackgroundBuilder.plsr.paths.txt");
            }
            catch
            {
                return Path.Combine(Environment.CurrentDirectory, "AtsBackgroundBuilder.plsr.paths.txt");
            }
        }

        private static void SavePersistedPlsrXmlPathsUnsafe()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(PersistedPlsrStateFilePath))
                {
                    PersistedPlsrStateFilePath = ResolvePersistedPlsrStateFilePath();
                }

                File.WriteAllLines(PersistedPlsrStateFilePath, PersistedPlsrXmlPaths);
            }
            catch
            {
                // best effort only
            }
        }

        private void BuildLayout(IEnumerable<string> clientNames, Config config)
        {
            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = BuildHeaderCard(clientNames);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var settings = BuildSettingsCard(_config);
            Grid.SetRow(settings, 1);
            root.Children.Add(settings);

            var gridCard = BuildGridCard();
            Grid.SetRow(gridCard, 2);
            root.Children.Add(gridCard);

            var actionCard = BuildActionCard();
            Grid.SetRow(actionCard, 3);
            root.Children.Add(actionCard);

            Content = root;
        }

        private Border BuildHeaderCard(IEnumerable<string> clientNames)
        {
            var card = CreateCard();
            card.Margin = new Thickness(0, 0, 0, 10);

            var stack = new StackPanel();

            var headerRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 12),
            };

            var badge = new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock
                {
                    Text = "ATS",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                }
            };
            headerRow.Children.Add(badge);

            var titleStack = new StackPanel();
            titleStack.Children.Add(new TextBlock
            {
                Text = "PREDRAFT BUILDER",
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "Build section geometry from legal land descriptions",
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(98, 109, 127)),
            });
            headerRow.Children.Add(titleStack);
            stack.Children.Add(headerRow);

            var inputGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var clientPanel = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
            clientPanel.Children.Add(CreateFieldLabel("Client"));
            _clientCombo.MinWidth = 520;
            _clientCombo.Height = 30;
            _clientCombo.VerticalContentAlignment = VerticalAlignment.Center;
            _clientCombo.Padding = new Thickness(6, 0, 6, 0);
            _clientCombo.ItemsSource = (clientNames ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s)
                .ToList();
            if (_clientCombo.Items.Count > 0)
            {
                _clientCombo.SelectedIndex = 0;
            }
            clientPanel.Children.Add(_clientCombo);
            inputGrid.Children.Add(clientPanel);

            var zonePanel = new StackPanel();
            zonePanel.Children.Add(CreateFieldLabel("Zone"));
            var zoneRow = new StackPanel { Orientation = Orientation.Horizontal };
            _zone11Radio.Content = "11";
            _zone11Radio.IsChecked = true;
            _zone11Radio.Margin = new Thickness(0, 4, 16, 0);
            _zone12Radio.Content = "12";
            _zone12Radio.Margin = new Thickness(0, 4, 0, 0);
            zoneRow.Children.Add(_zone11Radio);
            zoneRow.Children.Add(_zone12Radio);
            zonePanel.Children.Add(zoneRow);
            Grid.SetColumn(zonePanel, 1);
            inputGrid.Children.Add(zonePanel);

            stack.Children.Add(inputGrid);
            stack.Children.Add(new TextBlock
            {
                Text = "M/RGE/TWP carry down when blank. Leave SEC blank on a row with M/RGE/TWP to build sections 1-36.",
                Foreground = new SolidColorBrush(Color.FromRgb(98, 109, 127)),
            });

            card.Child = stack;
            return card;
        }

        private Border BuildSettingsCard(Config config)
        {
            var card = CreateCard();
            card.Margin = new Thickness(0, 0, 0, 10);

            var content = new StackPanel();
            content.Children.Add(CreateSectionTitle("Build Settings"));

            var settingsGrid = new Grid();
            settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var numericStack = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
            numericStack.Children.Add(CreateFieldLabel("Text Height"));
            _textHeightBox.Text = (config?.TextHeight ?? 10.0).ToString("0.##", CultureInfo.InvariantCulture);
            _textHeightBox.Width = 90;
            _textHeightBox.Height = 28;
            _textHeightBox.VerticalContentAlignment = VerticalAlignment.Center;
            _textHeightBox.Padding = new Thickness(6, 0, 6, 0);
            numericStack.Children.Add(_textHeightBox);
            numericStack.Children.Add(CreateFieldLabel("Max Overlap Attempts", top: 8));
            _maxAttemptsBox.Text = Math.Max(1, config?.MaxOverlapAttempts ?? 25).ToString(CultureInfo.InvariantCulture);
            _maxAttemptsBox.Width = 90;
            _maxAttemptsBox.Height = 28;
            _maxAttemptsBox.VerticalContentAlignment = VerticalAlignment.Center;
            _maxAttemptsBox.Padding = new Thickness(6, 0, 6, 0);
            numericStack.Children.Add(_maxAttemptsBox);
            settingsGrid.Children.Add(numericStack);

            ConfigureOptionCheckBox(_includeDispoLinework, "Disposition linework", false);
            ConfigureOptionCheckBox(_includeDispoLabels, "Disposition labels", false);
            ConfigureOptionCheckBox(_includeAtsFabric, "ATS fabric", false);
            ConfigureOptionCheckBox(_includeLsds, "LSDs", false);
            ConfigureOptionCheckBox(_includeP3Shapes, "Include P3 Shapes", false);
            ConfigureOptionCheckBox(_includeCompassMapping, "COMPASS MAPPING", false);
            ConfigureOptionCheckBox(_includeCrownReservations, "Crown Reservations", false);
            ConfigureOptionCheckBox(_checkPlsr, "Check PLSR", false);
            ConfigureOptionCheckBox(_includeSurfaceImpact, "Surface Impact", false);
            ConfigureOptionCheckBox(_allowMultiQuarterDispositions, "1/4 Definition", config?.AllowMultiQuarterDispositions ?? false);
            ConfigureOptionCheckBox(_includeQuarterSectionLabels, "1/4 SEC Labels", false);

            var leftToggleStack = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
            leftToggleStack.Children.Add(_includeDispoLinework);
            leftToggleStack.Children.Add(_includeDispoLabels);
            leftToggleStack.Children.Add(_includeAtsFabric);
            leftToggleStack.Children.Add(_includeLsds);
            Grid.SetColumn(leftToggleStack, 1);
            settingsGrid.Children.Add(leftToggleStack);

            var rightToggleStack = new StackPanel();
            rightToggleStack.Children.Add(_includeP3Shapes);
            rightToggleStack.Children.Add(_includeCompassMapping);
            rightToggleStack.Children.Add(_includeCrownReservations);
            rightToggleStack.Children.Add(_checkPlsr);
            rightToggleStack.Children.Add(_includeSurfaceImpact);
            rightToggleStack.Children.Add(_allowMultiQuarterDispositions);
            rightToggleStack.Children.Add(_includeQuarterSectionLabels);
            Grid.SetColumn(rightToggleStack, 2);
            settingsGrid.Children.Add(rightToggleStack);

            content.Children.Add(settingsGrid);
            card.Child = content;
            return card;
        }

        private Border BuildGridCard()
        {
            var card = CreateCard();
            card.Margin = new Thickness(0, 0, 0, 10);

            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerRow.Margin = new Thickness(0, 0, 0, 8);
            headerRow.Children.Add(CreateSectionTitle("Section Grid"));
            var info = new TextBlock
            {
                Text = "i",
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(98, 109, 127)),
                Margin = new Thickness(0, 2, 0, 0),
                ToolTip = "Grid behavior:\n- M/RGE/TWP carry down from row above when blank\n- If SEC is blank with M/RGE/TWP provided, sections 1-36 are built\n- Quarter values: NW, NE, SW, SE, N, S, E, W, ALL",
            };
            Grid.SetColumn(info, 1);
            headerRow.Children.Add(info);
            Grid.SetRow(headerRow, 0);
            content.Children.Add(headerRow);

            _grid.AutoGenerateColumns = false;
            _grid.CanUserAddRows = false;
            _grid.CanUserDeleteRows = true;
            _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
            _grid.GridLinesVisibility = DataGridGridLinesVisibility.All;
            _grid.Margin = new Thickness(0, 0, 0, 8);
            _grid.RowHeight = 30;
            _grid.ColumnHeaderHeight = 32;
            _grid.ItemsSource = _rows;
            _grid.Columns.Add(CreateTextColumn("M", nameof(GridInputRow.M)));
            _grid.Columns.Add(CreateTextColumn("RGE", nameof(GridInputRow.RGE)));
            _grid.Columns.Add(CreateTextColumn("TWP", nameof(GridInputRow.TWP)));
            _grid.Columns.Add(CreateTextColumn("SEC", nameof(GridInputRow.SEC)));
            _grid.Columns.Add(CreateTextColumn("H/QS", nameof(GridInputRow.HQ)));

            _rows.Add(new GridInputRow());

            Grid.SetRow(_grid, 1);
            content.Children.Add(_grid);

            var addRow = new Button
            {
                Content = "Add Row",
                Width = 96,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 8),
            };
            addRow.Click += (_, __) => _rows.Add(new GridInputRow());
            ConfigureOutlineButton(addRow);
            Grid.SetRow(addRow, 2);
            content.Children.Add(addRow);

            var tipBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(239, 246, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(191, 219, 254)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                Child = new TextBlock
                {
                    Text = "Tip: M/RGE/TWP values carry down when blank. Leave SEC empty to build all sections 1-36. Quarter values: NW, NE, SW, SE, N, S, E, W, ALL.",
                    Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175)),
                    TextWrapping = TextWrapping.Wrap,
                }
            };
            Grid.SetRow(tipBorder, 3);
            content.Children.Add(tipBorder);

            card.Child = content;
            return card;
        }

        private Border BuildActionCard()
        {
            var card = CreateCard();
            card.Margin = new Thickness(0);

            var actions = new Grid();
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            left.Children.Add(new TextBlock
            {
                Text = "Shape Type",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
            });

            _shapeTypeCombo.Width = 180;
            _shapeTypeCombo.Height = 30;
            _shapeTypeCombo.VerticalContentAlignment = VerticalAlignment.Center;
            _shapeTypeCombo.Padding = new Thickness(6, 0, 6, 0);
            _shapeTypeCombo.Margin = new Thickness(0, 0, 10, 0);
            _shapeTypeCombo.Items.Add("Disposition");
            _shapeTypeCombo.Items.Add("Compass Mapping");
            _shapeTypeCombo.Items.Add("Crown Reservations");
            _shapeTypeCombo.SelectedIndex = 0;
            left.Children.Add(_shapeTypeCombo);

            _updateShape.Content = "Update Shape";
            _updateShape.Width = 130;
            _updateShape.Height = 32;
            _updateShape.Click += (_, __) => OnUpdateShape();
            ConfigureOutlineButton(_updateShape);
            left.Children.Add(_updateShape);

            _addSectionsFromBoundary.Content = "ADD SECTIONS FROM BDY";
            _addSectionsFromBoundary.Width = 190;
            _addSectionsFromBoundary.Height = 32;
            _addSectionsFromBoundary.Margin = new Thickness(10, 0, 0, 0);
            _addSectionsFromBoundary.Click += (_, __) => OnAddSectionsFromBoundary();
            ConfigureOutlineButton(_addSectionsFromBoundary);
            left.Children.Add(_addSectionsFromBoundary);

            ConfigureOptionCheckBox(_autoCheckUpdateShapesAlways, "CHECK/UPDATE SHAPES ALWAYS", false);
            _autoCheckUpdateShapesAlways.Margin = new Thickness(10, 7, 0, 0);
            left.Children.Add(_autoCheckUpdateShapesAlways);
            actions.Children.Add(left);

            var right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            _cancel.Content = "Cancel";
            _cancel.Width = 90;
            _cancel.Height = 32;
            _cancel.Margin = new Thickness(0, 0, 10, 0);
            _cancel.IsCancel = true;
            _cancel.Click += (_, __) =>
            {
                _explicitCancelRequested = true;
                _buildRequested = false;
                _buildAttemptTrace = "cancel_button_click";
                CloseAsDialogResultOrWindow(false);
            };
            ConfigureOutlineButton(_cancel);

            _build.Content = "BUILD";
            _build.Width = 120;
            _build.Height = 32;
            _build.IsDefault = true;
            _build.Click += (_, __) =>
            {
                _buildAttemptTrace = "build_button_click";
                _buildAttempted = true;
                _buildRequested = true;
                OnBuild();
            };
            ConfigurePrimaryButton(_build);

            right.Children.Add(_cancel);
            right.Children.Add(_build);
            Grid.SetColumn(right, 1);
            actions.Children.Add(right);

            card.Child = actions;
            return card;
        }

        private static Border CreateCard()
        {
            return new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(224, 228, 235)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 14, 16, 14),
            };
        }

        private static TextBlock CreateSectionTitle(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                Margin = new Thickness(0, 0, 0, 10),
            };
        }

        private static TextBlock CreateFieldLabel(string text, double top = 0)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                Margin = new Thickness(0, top, 0, 4),
            };
        }

        private static DataGridTextColumn CreateTextColumn(string header, string path)
        {
            return new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            };
        }

        private static void ConfigureOptionCheckBox(CheckBox checkBox, string text, bool isChecked)
        {
            checkBox.Content = text;
            checkBox.IsChecked = isChecked;
            checkBox.Margin = new Thickness(0, 0, 0, 7);
        }

        private static void ConfigurePrimaryButton(Button button)
        {
            button.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
            button.Foreground = Brushes.White;
            button.BorderBrush = button.Background;
            button.BorderThickness = new Thickness(1);
            button.FontWeight = FontWeights.SemiBold;
            button.Cursor = Cursors.Hand;
        }

        private static void ConfigureOutlineButton(Button button)
        {
            button.Background = Brushes.White;
            button.Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55));
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219));
            button.BorderThickness = new Thickness(1);
            button.Cursor = Cursors.Hand;
        }

        private void OnAddSectionsFromBoundary()
        {
            try
            {
            var importedRows = new List<BoundarySectionImportService.SectionGridEntry>();
            var serviceMessage = string.Empty;
            var cancelled = false;
            var zone = _zone12Radio.IsChecked == true ? 12 : 11;
            bool succeeded;
            try
            {
                _boundaryImportRoundTripUsed = true;
                Hide();
                succeeded = BoundarySectionImportService.TryCollectEntriesFromBoundary(
                    _config,
                    zone,
                    out importedRows,
                    out serviceMessage,
                    out cancelled);
            }
            finally
            {
                if (!IsVisible)
                {
                    Show();
                }

                Activate();
            }

            if (!succeeded)
            {
                if (!cancelled && !string.IsNullOrWhiteSpace(serviceMessage))
                {
                    MessageBox.Show(this, serviceMessage, "ATSBUILD", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                return;
            }

            // If the grid only contains placeholder empty rows, clear them before appending imported rows.
            if (!_rows.Any(RowHasAnyValue))
            {
                _rows.Clear();
            }

            var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _rows)
            {
                if (!RowHasAnyValue(row))
                {
                    continue;
                }

                existingKeys.Add(BuildRowKey(row.M, row.RGE, row.TWP, row.SEC, row.HQ));
            }

            var added = 0;
            var duplicates = 0;
            foreach (var entry in importedRows)
            {
                var key = BuildRowKey(entry.Meridian, entry.Range, entry.Township, entry.Section, entry.Quarter);
                if (!existingKeys.Add(key))
                {
                    duplicates++;
                    continue;
                }

                _rows.Add(new GridInputRow
                {
                    M = entry.Meridian,
                    RGE = entry.Range,
                    TWP = entry.Township,
                    SEC = entry.Section,
                    HQ = entry.Quarter,
                });
                added++;
            }

            MessageBox.Show(
                this,
                BuildBoundaryImportResultMessage(serviceMessage, added, duplicates),
                "ATSBUILD",
                MessageBoxButton.OK,
                added > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                WriteUiException("AtsBuildWindow.OnAddSectionsFromBoundary", ex);
                MessageBox.Show(
                    this,
                    "Boundary import failed:\n" + ex.Message,
                    "ATSBUILD",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static bool RowHasAnyValue(GridInputRow row)
        {
            if (row == null)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(row.M) ||
                   !string.IsNullOrWhiteSpace(row.RGE) ||
                   !string.IsNullOrWhiteSpace(row.TWP) ||
                   !string.IsNullOrWhiteSpace(row.SEC) ||
                   !string.IsNullOrWhiteSpace(row.HQ);
        }

        private static string BuildRowKey(string m, string rge, string twp, string sec, string hq)
        {
            return string.Join(
                "|",
                NormalizeRowToken(m),
                NormalizeRowToken(rge),
                NormalizeRowToken(twp),
                NormalizeRowToken(sec),
                NormalizeRowToken(hq));
        }

        private static string NormalizeRowToken(string value)
        {
            return value?.Trim().ToUpperInvariant() ?? string.Empty;
        }

        private static string BuildBoundaryImportResultMessage(string serviceMessage, int added, int duplicates)
        {
            var prefix = string.IsNullOrWhiteSpace(serviceMessage)
                ? string.Empty
                : serviceMessage.Trim() + Environment.NewLine + Environment.NewLine;
            if (added <= 0)
            {
                return prefix + "No new section rows were added." +
                       (duplicates > 0 ? $" Skipped {duplicates} duplicate row(s)." : string.Empty);
            }

            return prefix + $"Added {added} row(s) to the section input list." +
                   (duplicates > 0 ? $" Skipped {duplicates} duplicate row(s)." : string.Empty);
        }

        private void OnBuild()
        {
            try
            {
            _buildAttemptTrace = "onbuild_start";
            var client = _clientCombo.SelectedItem?.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(client))
            {
                _buildRequested = false;
                _buildAttemptTrace = "onbuild_abort_client_missing";
                MessageBox.Show(this, "Client is required.", "ATSBUILD", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var zone = _zone12Radio.IsChecked == true ? 12 : 11;

            if (!TryParsePositiveDouble(_textHeightBox.Text, out var textHeight))
            {
                _buildRequested = false;
                _buildAttemptTrace = "onbuild_abort_text_height_invalid";
                MessageBox.Show(this, "Text height must be a valid number greater than 0.", "ATSBUILD", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryParsePositiveInt(_maxAttemptsBox.Text, out var maxOverlapAttempts))
            {
                _buildRequested = false;
                _buildAttemptTrace = "onbuild_abort_max_attempts_invalid";
                MessageBox.Show(this, "Max overlap attempts must be a valid whole number greater than 0.", "ATSBUILD", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var requests = ParseSectionRequests(zone);
            if (requests.Count == 0)
            {
                _buildRequested = false;
                _buildAttemptTrace = "onbuild_abort_requests_empty_or_invalid";
                MessageBox.Show(this, "At least one section row is required.", "ATSBUILD", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var input = new AtsBuildInput
            {
                CurrentClient = client,
                Zone = zone,
                TextHeight = textHeight,
                MaxOverlapAttempts = maxOverlapAttempts,
                IncludeDispositionLinework = _includeDispoLinework.IsChecked == true,
                IncludeDispositionLabels = _includeDispoLabels.IsChecked == true,
                AllowMultiQuarterDispositions = _allowMultiQuarterDispositions.IsChecked == true,
                IncludeAtsFabric = _includeAtsFabric.IsChecked == true,
                DrawLsdSubdivisionLines = _includeLsds.IsChecked == true,
                IncludeP3Shapefiles = _includeP3Shapes.IsChecked == true,
                IncludeCompassMapping = _includeCompassMapping.IsChecked == true,
                IncludeCrownReservations = _includeCrownReservations.IsChecked == true,
                AutoCheckUpdateShapefilesAlways = _autoCheckUpdateShapesAlways.IsChecked == true,
                CheckPlsr = _checkPlsr.IsChecked == true,
                IncludeSurfaceImpact = _includeSurfaceImpact.IsChecked == true,
                IncludeQuarterSectionLabels = _includeQuarterSectionLabels.IsChecked == true,
                UseAlignedDimensions = true,
            };
            input.SectionRequests.AddRange(requests);

                var requiresPlsrXml = input.CheckPlsr || input.IncludeSurfaceImpact;
                if (requiresPlsrXml)
                {
                    var dialog = new OpenFileDialog
                {
                    Filter = "PLSR XML (*.xml)|*.xml|All files (*.*)|*.*",
                    Multiselect = true,
                    Title = "Select PLSR/Surface XML file(s)",
                    InitialDirectory = Environment.CurrentDirectory,
                };

                    if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0)
                    {
                        _buildRequested = false;
                        _buildAttemptTrace = "onbuild_abort_plsr_xml_dialog_cancelled";
                        MessageBox.Show(this, "Check PLSR / Surface Impact requires at least one XML file.", "ATSBUILD", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    _lastSelectedPlsrXmlPaths.Clear();
                    foreach (var path in dialog.FileNames)
                    {
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        {
                            input.PlsrXmlPaths.Add(path);
                            _lastSelectedPlsrXmlPaths.Add(path);
                        }
                    }
                    PersistPlsrXmlPaths(_lastSelectedPlsrXmlPaths);


                if (input.PlsrXmlPaths.Count == 0)
                {
                    _buildRequested = false;
                    _buildAttemptTrace = "onbuild_abort_plsr_xml_no_valid_paths";
                    MessageBox.Show(this, "Check PLSR / Surface Impact requires at least one XML file.", "ATSBUILD", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            _buildAttemptTrace = "onbuild_success";
            Result = input;
            CloseAsDialogResultOrWindow(true);
            }
            catch (Exception ex)
            {
                _buildRequested = false;
                _buildAttemptTrace = "onbuild_exception";
                WriteUiException("AtsBuildWindow.OnBuild", ex);
                MessageBox.Show(
                    this,
                    "Build setup failed:\n" + ex.Message,
                    "ATSBUILD",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public bool TryBuildInputSnapshot(out AtsBuildInput? input, out string reason)
        {
            input = null;
            reason = string.Empty;

            var client = _clientCombo.SelectedItem?.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(client))
            {
                reason = "client_missing";
                return false;
            }

            var zone = _zone12Radio.IsChecked == true ? 12 : 11;
            if (!TryParsePositiveDouble(_textHeightBox.Text, out var textHeight))
            {
                reason = "text_height_invalid";
                return false;
            }

            if (!TryParsePositiveInt(_maxAttemptsBox.Text, out var maxOverlapAttempts))
            {
                reason = "max_overlap_attempts_invalid";
                return false;
            }

            var requests = ParseSectionRequests(zone);
            if (requests.Count == 0)
            {
                reason = "section_requests_empty";
                return false;
            }

            var snapshot = new AtsBuildInput
            {
                CurrentClient = client,
                Zone = zone,
                TextHeight = textHeight,
                MaxOverlapAttempts = maxOverlapAttempts,
                IncludeDispositionLinework = _includeDispoLinework.IsChecked == true,
                IncludeDispositionLabels = _includeDispoLabels.IsChecked == true,
                AllowMultiQuarterDispositions = _allowMultiQuarterDispositions.IsChecked == true,
                IncludeAtsFabric = _includeAtsFabric.IsChecked == true,
                DrawLsdSubdivisionLines = _includeLsds.IsChecked == true,
                IncludeP3Shapefiles = _includeP3Shapes.IsChecked == true,
                IncludeCompassMapping = _includeCompassMapping.IsChecked == true,
                IncludeCrownReservations = _includeCrownReservations.IsChecked == true,
                AutoCheckUpdateShapefilesAlways = _autoCheckUpdateShapesAlways.IsChecked == true,
                CheckPlsr = _checkPlsr.IsChecked == true,
                IncludeSurfaceImpact = _includeSurfaceImpact.IsChecked == true,
                IncludeQuarterSectionLabels = _includeQuarterSectionLabels.IsChecked == true,
                UseAlignedDimensions = true,
            };
            snapshot.SectionRequests.AddRange(requests);

            var requiresPlsrXml = snapshot.CheckPlsr || snapshot.IncludeSurfaceImpact;
            if (requiresPlsrXml)
            {
                var xmlPaths = _lastSelectedPlsrXmlPaths
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (xmlPaths.Count == 0)
                {
                    reason = "plsr_xml_missing";
                    return false;
                }

                snapshot.PlsrXmlPaths.AddRange(xmlPaths);
            }

            input = snapshot;
            return true;
        }

        private void CloseAsDialogResultOrWindow(bool result)
        {
            try
            {
                DialogResult = result;
            }
            catch (InvalidOperationException)
            {
                // Window was not shown via ShowDialog(); fallback to regular close.
                Close();
            }
        }

        private static void WriteUiException(string source, Exception ex)
        {
            try
            {
                var dllFolder = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory;
                var crashLogPath = Path.Combine(dllFolder, "AtsBackgroundBuilder.crash.log");
                var lines = new[]
                {
                    "---- ATSBUILD UI EXCEPTION ----",
                    "source: " + (source ?? string.Empty),
                    "local: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    "details:",
                    ex?.ToString() ?? "null",
                    string.Empty
                };
                File.AppendAllLines(crashLogPath, lines);
            }
            catch
            {
                // best effort only
            }
        }

        private List<SectionRequest> ParseSectionRequests(int zone)
        {
            var requests = new List<SectionRequest>();

            var lastMeridian = string.Empty;
            var lastRange = string.Empty;
            var lastTownship = string.Empty;
            var lastSection = string.Empty;

            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                var m = NormalizeCell(row.M);
                var rge = NormalizeCell(row.RGE);
                var twp = NormalizeCell(row.TWP);
                var sec = NormalizeCell(row.SEC);
                var q = NormalizeCell(row.HQ);

                var anyFilled =
                    !string.IsNullOrWhiteSpace(m) ||
                    !string.IsNullOrWhiteSpace(rge) ||
                    !string.IsNullOrWhiteSpace(twp) ||
                    !string.IsNullOrWhiteSpace(sec) ||
                    !string.IsNullOrWhiteSpace(q);
                if (!anyFilled)
                {
                    continue;
                }

                var hasExplicitMeridian = !string.IsNullOrWhiteSpace(m);
                var hasExplicitRange = !string.IsNullOrWhiteSpace(rge);
                var hasExplicitTownship = !string.IsNullOrWhiteSpace(twp);
                var hasExplicitSection = !string.IsNullOrWhiteSpace(sec);

                if (string.IsNullOrWhiteSpace(m))
                {
                    m = lastMeridian;
                }

                if (string.IsNullOrWhiteSpace(rge))
                {
                    rge = lastRange;
                }

                if (string.IsNullOrWhiteSpace(twp))
                {
                    twp = lastTownship;
                }

                var expandAllSections =
                    !hasExplicitSection &&
                    (hasExplicitMeridian || hasExplicitRange || hasExplicitTownship);
                if (!expandAllSections && string.IsNullOrWhiteSpace(sec))
                {
                    sec = lastSection;
                }

                if (string.IsNullOrWhiteSpace(q))
                {
                    q = "ALL";
                }

                if (string.IsNullOrWhiteSpace(m) || string.IsNullOrWhiteSpace(rge) || string.IsNullOrWhiteSpace(twp))
                {
                    MessageBox.Show(
                        this,
                        "Row is missing M/RGE/TWP and no value above to carry down.\n\n" +
                        "Tip: Fill the first row completely, then you can leave repeated values blank on lower rows.",
                        "ATSBUILD",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return new List<SectionRequest>();
                }

                if (!expandAllSections && string.IsNullOrWhiteSpace(sec))
                {
                    MessageBox.Show(
                        this,
                        "SEC is blank and there is no section above to carry down.\n\n" +
                        "Tip: Enter SEC, or provide M/RGE/TWP on that row with SEC blank to build sections 1-36.",
                        "ATSBUILD",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return new List<SectionRequest>();
                }

                lastMeridian = m;
                lastRange = rge;
                lastTownship = twp;

                if (!TryParseQuarter(q, out var quarter))
                {
                    MessageBox.Show(
                        this,
                        $"Invalid quarter value: '{q}'. Use NW, NE, SW, SE, N, S, E, W, or ALL.",
                        "ATSBUILD",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return new List<SectionRequest>();
                }

                if (expandAllSections)
                {
                    for (var sectionNumber = 1; sectionNumber <= 36; sectionNumber++)
                    {
                        var key = new SectionKey(zone, sectionNumber.ToString(CultureInfo.InvariantCulture), twp, rge, m);
                        requests.Add(new SectionRequest(quarter, key, "AUTO"));
                    }

                    lastSection = string.Empty;
                }
                else
                {
                    lastSection = sec;
                    var key = new SectionKey(zone, sec, twp, rge, m);
                    requests.Add(new SectionRequest(quarter, key, "AUTO"));
                }
            }

            return requests;
        }

        private static string NormalizeCell(string? value)
        {
            return value?.Trim() ?? string.Empty;
        }

        private static bool TryParseQuarter(string raw, out QuarterSelection quarter)
        {
            quarter = QuarterSelection.None;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var s = raw.Trim().ToUpperInvariant();
            switch (s)
            {
                case "NW":
                    quarter = QuarterSelection.NorthWest;
                    return true;
                case "NE":
                    quarter = QuarterSelection.NorthEast;
                    return true;
                case "SW":
                    quarter = QuarterSelection.SouthWest;
                    return true;
                case "SE":
                    quarter = QuarterSelection.SouthEast;
                    return true;
                case "N":
                    quarter = QuarterSelection.NorthHalf;
                    return true;
                case "S":
                    quarter = QuarterSelection.SouthHalf;
                    return true;
                case "E":
                    quarter = QuarterSelection.EastHalf;
                    return true;
                case "W":
                    quarter = QuarterSelection.WestHalf;
                    return true;
                case "ALL":
                case "A":
                    quarter = QuarterSelection.All;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryParsePositiveDouble(string raw, out double value)
        {
            value = 0.0;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && value > 0.0)
            {
                return true;
            }

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0.0)
            {
                return true;
            }

            return false;
        }

        private static bool TryParsePositiveInt(string raw, out int value)
        {
            value = 0;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value > 0;
        }

        private void OnUpdateShape()
        {
            var shapeType = _shapeTypeCombo.SelectedItem?.ToString()?.Trim() ?? string.Empty;
            if (string.Equals(shapeType, "Disposition", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveNewestDidsFolderAcrossRoots(
                        out var sourceRoot,
                        out var newestFolder,
                        out var newestDate,
                        out var newestFolderError))
                {
                    MessageBox.Show(this, newestFolderError, "Update Shape", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var confirm = MessageBox.Show(
                    this,
                    "Copy latest Disposition shape files?\n\n" +
                    $"Source root: {sourceRoot}\n" +
                    $"Latest folder: {newestFolder}\n" +
                    $"Detected date: {newestDate:yyyy-MM-dd}\n\n" +
                    $"Destination: {DispositionShapeDestinationFolder}\n\n" +
                    "This will replace current destination contents.",
                    "Update Shape",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }

                _updateShape.IsEnabled = false;
                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    var copiedCount = ReplaceDirectoryContents(newestFolder, DispositionShapeDestinationFolder);
                    MessageBox.Show(
                        this,
                        $"Shape update complete.\n\nCopied {copiedCount} file(s) from:\n{newestFolder}\n\nto:\n{DispositionShapeDestinationFolder}",
                        "Update Shape",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        "Shape update failed:\n" + ex.Message,
                        "Update Shape",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                    _updateShape.IsEnabled = true;
                }
                return;
            }

            if (string.Equals(shapeType, "Compass Mapping", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveFirstExistingRootAcrossRoots(
                        CompassMappingShapeUpdateSourceRoots,
                        "COMPASS MAPPING update folder",
                        out var sourceRoot,
                        out var rootError))
                {
                    MessageBox.Show(this, rootError, "Update Shape", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var confirm = MessageBox.Show(
                    this,
                    "Copy COMPASS MAPPING shape files?\n\n" +
                    $"Source: {sourceRoot}\n\n" +
                    $"Shape sets: {string.Join(", ", CompassMappingShapeBaseNames)}\n\n" +
                    $"Destination: {CompassMappingShapeDestinationFolder}\n\n" +
                    "This will replace current destination contents.",
                    "Update Shape",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }

                _updateShape.IsEnabled = false;
                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    var copiedCount = ReplaceDirectoryContentsWithSelectedShapeSets(
                        sourceRoot,
                        CompassMappingShapeDestinationFolder,
                        CompassMappingShapeBaseNames);
                    MessageBox.Show(
                        this,
                        $"Shape update complete.\n\nCopied {copiedCount} file(s) from:\n{sourceRoot}\n\nto:\n{CompassMappingShapeDestinationFolder}",
                        "Update Shape",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        "Shape update failed:\n" + ex.Message,
                        "Update Shape",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                    _updateShape.IsEnabled = true;
                }
                return;
            }

            if (string.Equals(shapeType, "Crown Reservations", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveNewestDatedFolderAcrossRoots(
                        CrownReservationsShapeUpdateSourceRoots,
                        "Crown Reservations update folder",
                        out var sourceRoot,
                        out var newestFolder,
                        out var newestDate,
                        out var rootError))
                {
                    MessageBox.Show(this, rootError, "Update Shape", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var confirm = MessageBox.Show(
                    this,
                    "Copy Crown Reservations shape files?\n\n" +
                    $"Source root: {sourceRoot}\n" +
                    $"Latest folder: {newestFolder}\n" +
                    $"Detected date: {newestDate:yyyy-MM-dd}\n\n" +
                    $"Shape sets: {string.Join(", ", CrownReservationsShapeBaseNames)}\n\n" +
                    $"Destination: {CrownReservationsShapeDestinationFolder}\n\n" +
                    "This will replace current destination contents.",
                    "Update Shape",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }

                _updateShape.IsEnabled = false;
                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    var copiedCount = ReplaceDirectoryContentsWithSelectedShapeSets(
                        newestFolder,
                        CrownReservationsShapeDestinationFolder,
                        CrownReservationsShapeBaseNames);
                    MessageBox.Show(
                        this,
                        $"Shape update complete.\n\nCopied {copiedCount} file(s) from:\n{newestFolder}\n\nto:\n{CrownReservationsShapeDestinationFolder}",
                        "Update Shape",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        "Shape update failed:\n" + ex.Message,
                        "Update Shape",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                    _updateShape.IsEnabled = true;
                }
                return;
            }

            MessageBox.Show(this, $"Unsupported shape type: {shapeType}", "Update Shape", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static bool TryFindNewestDidsFolder(string sourceRoot, out string newestFolder, out DateTime newestDate, out string error)
        {
            newestFolder = string.Empty;
            newestDate = default;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                error = $"Source root not found: {sourceRoot}";
                return false;
            }

            var candidates = new List<(string FolderPath, DateTime Date)>();
            foreach (var folder in Directory.GetDirectories(sourceRoot, "dids_*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(folder) ?? string.Empty;
                if (TryParseDateFromFolderName(name, out var parsedDate))
                {
                    candidates.Add((folder, parsedDate));
                }
            }

            if (candidates.Count == 0)
            {
                error = $"No dated dids_* folders found under:\n{sourceRoot}";
                return false;
            }

            var selected = candidates
                .OrderByDescending(c => c.Date)
                .ThenByDescending(c => Path.GetFileName(c.FolderPath), StringComparer.OrdinalIgnoreCase)
                .First();

            newestFolder = selected.FolderPath;
            newestDate = selected.Date;
            return true;
        }

        private static bool TryResolveNewestDidsFolderAcrossRoots(
            out string selectedSourceRoot,
            out string newestFolder,
            out DateTime newestDate,
            out string error)
        {
            selectedSourceRoot = string.Empty;
            newestFolder = string.Empty;
            newestDate = default;
            error = string.Empty;

            var existingRoots = DispositionShapeUpdateSourceRoots
                .Where(Directory.Exists)
                .ToList();
            if (existingRoots.Count == 0)
            {
                error = "Unable to find AltaLIS FTP update folder.\nChecked:\n" + string.Join("\n", DispositionShapeUpdateSourceRoots);
                return false;
            }

            var foundAny = false;
            var bestDate = DateTime.MinValue;
            var bestFolder = string.Empty;
            var bestRoot = string.Empty;
            var diagnostics = new List<string>();

            foreach (var root in existingRoots)
            {
                if (!TryFindNewestDidsFolder(root, out var candidateFolder, out var candidateDate, out var rootError))
                {
                    diagnostics.Add(rootError);
                    continue;
                }

                if (!foundAny || candidateDate > bestDate)
                {
                    foundAny = true;
                    bestDate = candidateDate;
                    bestFolder = candidateFolder;
                    bestRoot = root;
                }
            }

            if (!foundAny)
            {
                error = "No dated dids_* folders were found in available AltaLIS roots.\n" + string.Join("\n", diagnostics);
                return false;
            }

            selectedSourceRoot = bestRoot;
            newestFolder = bestFolder;
            newestDate = bestDate;
            return true;
        }

        private static bool TryResolveFirstExistingRootAcrossRoots(
            IReadOnlyList<string> roots,
            string sourceDescription,
            out string selectedRoot,
            out string error)
        {
            selectedRoot = string.Empty;
            error = string.Empty;
            if (roots == null || roots.Count == 0)
            {
                error = $"No candidate roots configured for {sourceDescription}.";
                return false;
            }

            for (var i = 0; i < roots.Count; i++)
            {
                var root = roots[i];
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    selectedRoot = root;
                    return true;
                }
            }

            error = $"Unable to find {sourceDescription}.\nChecked:\n" + string.Join("\n", roots);
            return false;
        }

        private static bool TryResolveNewestDatedFolderAcrossRoots(
            IReadOnlyList<string> roots,
            string sourceDescription,
            out string selectedSourceRoot,
            out string newestFolder,
            out DateTime newestDate,
            out string error)
        {
            selectedSourceRoot = string.Empty;
            newestFolder = string.Empty;
            newestDate = default;
            error = string.Empty;

            var existingRoots = roots
                .Where(Directory.Exists)
                .ToList();
            if (existingRoots.Count == 0)
            {
                error = $"Unable to find {sourceDescription}.\nChecked:\n" + string.Join("\n", roots);
                return false;
            }

            var foundAny = false;
            var bestDate = DateTime.MinValue;
            var bestFolder = string.Empty;
            var bestRoot = string.Empty;
            var diagnostics = new List<string>();
            foreach (var root in existingRoots)
            {
                if (!TryFindNewestDatedSubfolder(root, out var candidateFolder, out var candidateDate, out var rootError))
                {
                    diagnostics.Add(rootError);
                    continue;
                }

                if (!foundAny || candidateDate > bestDate)
                {
                    foundAny = true;
                    bestDate = candidateDate;
                    bestFolder = candidateFolder;
                    bestRoot = root;
                }
            }

            if (!foundAny)
            {
                error = $"No dated folders were found in available roots for {sourceDescription}.\n" + string.Join("\n", diagnostics);
                return false;
            }

            selectedSourceRoot = bestRoot;
            newestFolder = bestFolder;
            newestDate = bestDate;
            return true;
        }

        private static bool TryFindNewestDatedSubfolder(string sourceRoot, out string newestFolder, out DateTime newestDate, out string error)
        {
            newestFolder = string.Empty;
            newestDate = default;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                error = $"Source root not found: {sourceRoot}";
                return false;
            }

            var candidates = new List<(string FolderPath, DateTime Date)>();
            foreach (var folder in Directory.GetDirectories(sourceRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(folder) ?? string.Empty;
                if (TryParseDateFromFolderName(name, out var parsedDate))
                {
                    candidates.Add((folder, parsedDate));
                }
            }

            if (candidates.Count == 0)
            {
                error = $"No dated folders found under:\n{sourceRoot}";
                return false;
            }

            var selected = candidates
                .OrderByDescending(c => c.Date)
                .ThenByDescending(c => Path.GetFileName(c.FolderPath), StringComparer.OrdinalIgnoreCase)
                .First();

            newestFolder = selected.FolderPath;
            newestDate = selected.Date;
            return true;
        }

        private static bool TryParseDateFromFolderName(string folderName, out DateTime date)
        {
            date = default;
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return false;
            }

            var match = Regex.Match(folderName, @"(?<a>\d{1,2})-(?<b>\d{1,2})-(?<y>\d{2,4})");
            if (!match.Success)
            {
                return false;
            }

            if (!int.TryParse(match.Groups["a"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var first) ||
                !int.TryParse(match.Groups["b"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var second) ||
                !int.TryParse(match.Groups["y"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
            {
                return false;
            }

            if (year < 100)
            {
                year += 2000;
            }

            int month;
            int day;
            if (first > 12 && second <= 12)
            {
                day = first;
                month = second;
            }
            else if (second > 12 && first <= 12)
            {
                month = first;
                day = second;
            }
            else
            {
                day = first;
                month = second;
            }

            if (month < 1 || month > 12 || day < 1)
            {
                return false;
            }

            var maxDay = DateTime.DaysInMonth(year, month);
            if (day > maxDay)
            {
                return false;
            }

            date = new DateTime(year, month, day);
            return true;
        }

        private static int ReplaceDirectoryContents(string sourceDirectory, string destinationDirectory)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException("Source folder not found: " + sourceDirectory);
            }

            Directory.CreateDirectory(destinationDirectory);
            ClearDirectoryContents(destinationDirectory);

            var copiedCount = 0;
            foreach (var sourcePath in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
                var destinationPath = Path.Combine(destinationDirectory, relativePath);
                var destinationFolder = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationFolder))
                {
                    Directory.CreateDirectory(destinationFolder);
                }

                File.Copy(sourcePath, destinationPath, overwrite: true);
                copiedCount++;
            }

            return copiedCount;
        }

        private static int ReplaceDirectoryContentsWithSelectedShapeSets(
            string sourceDirectory,
            string destinationDirectory,
            IReadOnlyList<string> shapeBaseNames)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException("Source folder not found: " + sourceDirectory);
            }

            Directory.CreateDirectory(destinationDirectory);
            ClearDirectoryContents(destinationDirectory);

            var selectedBaseNames = new HashSet<string>(
                shapeBaseNames?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var copiedCount = 0;
            foreach (var sourcePath in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var baseName = Path.GetFileNameWithoutExtension(sourcePath);
                if (string.IsNullOrWhiteSpace(baseName) || !selectedBaseNames.Contains(baseName))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
                var destinationPath = Path.Combine(destinationDirectory, relativePath);
                var destinationFolder = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationFolder))
                {
                    Directory.CreateDirectory(destinationFolder);
                }

                File.Copy(sourcePath, destinationPath, overwrite: true);
                copiedCount++;
            }

            return copiedCount;
        }

        private static void ClearDirectoryContents(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            var directory = new DirectoryInfo(directoryPath);
            foreach (var file in directory.GetFiles("*", SearchOption.TopDirectoryOnly))
            {
                file.IsReadOnly = false;
                file.Delete();
            }

            foreach (var childDirectory in directory.GetDirectories("*", SearchOption.TopDirectoryOnly))
            {
                childDirectory.Delete(recursive: true);
            }
        }

        private sealed class GridInputRow
        {
            public string M { get; set; } = string.Empty;
            public string RGE { get; set; } = string.Empty;
            public string TWP { get; set; } = string.Empty;
            public string SEC { get; set; } = string.Empty;
            public string HQ { get; set; } = string.Empty;
        }
    }
}










