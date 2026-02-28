/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AtsBackgroundBuilder.Core
{
    /// <summary>
    /// UI input bundle for ATSBUILD.
    /// </summary>
    public sealed class AtsBuildInput
    {
        public string CurrentClient { get; set; } = string.Empty;
        public int Zone { get; set; } = 11;
        public double TextHeight { get; set; } = 10.0;
        public int MaxOverlapAttempts { get; set; } = 25;
        public bool DrawLsdSubdivisionLines { get; set; } = false;
        public bool IncludeP3Shapefiles { get; set; } = false;
        public bool IncludeCompassMapping { get; set; } = false;
        public bool IncludeCrownReservations { get; set; } = false;
        public bool AutoCheckUpdateShapefilesAlways { get; set; } = false;

        /// <summary>
        /// When false, imported disposition linework is removed at cleanup.
        /// </summary>
        public bool IncludeDispositionLinework { get; set; } = false;
        public bool IncludeDispositionLabels { get; set; } = false;
        public bool AllowMultiQuarterDispositions { get; set; } = false;
        public bool IncludeQuarterSectionLabels { get; set; } = false;
        public bool UseAlignedDimensions { get; set; } = true;

        /// <summary>
        /// Placeholder for future feature layers.
        /// </summary>
        public bool IncludeAtsFabric { get; set; } = false;

        public List<SectionRequest> SectionRequests { get; } = new List<SectionRequest>();

        /// <summary>
        /// Run PLSR XML check against disposition labels.
        /// </summary>
        public bool CheckPlsr { get; set; } = false;

        public List<string> PlsrXmlPaths { get; } = new List<string>();
    }

    /// <summary>
    /// ATSBUILD UI form.
    /// Replaces the command line prompts with a single "BUILD" action.
    /// </summary>
    public sealed class AtsBuildForm : Form
    {
        private readonly ComboBox _clientCombo = new ComboBox();
        private readonly RadioButton _zone11Radio = new RadioButton();
        private readonly RadioButton _zone12Radio = new RadioButton();
        private readonly NumericUpDown _textHeight = new NumericUpDown();
        private readonly NumericUpDown _maxAttempts = new NumericUpDown();
        private readonly CheckBox _includeDispoLinework = new CheckBox();
        private readonly CheckBox _includeDispoLabels = new CheckBox();
        private readonly CheckBox _includeAtsFabric = new CheckBox();
        private readonly CheckBox _includeLsds = new CheckBox();
        private readonly CheckBox _includeP3Shapes = new CheckBox();
        private readonly CheckBox _includeCompassMapping = new CheckBox();
        private readonly CheckBox _includeCrownReservations = new CheckBox();
        private readonly CheckBox _checkPlsr = new CheckBox();
        private readonly CheckBox _allowMultiQuarterDispositions = new CheckBox();
        private readonly CheckBox _includeQuarterSectionLabels = new CheckBox();
        private readonly CheckBox _autoCheckUpdateShapesAlways = new CheckBox();
        private readonly DataGridView _grid = new DataGridView();
        private readonly Button _addGridRow = new Button();
        private readonly ComboBox _shapeTypeCombo = new ComboBox();
        private readonly Button _updateShape = new Button();
        private readonly Button _build = new Button();
        private readonly Button _cancel = new Button();
        private readonly ToolTip _toolTip = new ToolTip();
        private static readonly Color CanvasColor = Color.FromArgb(246, 248, 251);
        private static readonly Color CardColor = Color.White;
        private static readonly Color MutedTextColor = Color.FromArgb(98, 109, 127);
        private static readonly Color AccentColor = Color.FromArgb(37, 99, 235);
        private static readonly Color HeaderBackColor = Color.FromArgb(243, 244, 246);
        private static readonly Color InfoBackColor = Color.FromArgb(239, 246, 255);
        private static readonly Color InfoTextColor = Color.FromArgb(30, 64, 175);
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

        public AtsBuildForm(IEnumerable<string> clientNames, Config config)
        {
            Text = "ATS Background Builder";
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = CanvasColor;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            ClientSize = new Size(1280, 760);
            MinimumSize = new Size(1100, 660);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(16),
            };

            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            root.Controls.Add(BuildTopPanel(clientNames, config), 0, 0);
            root.Controls.Add(BuildOptionsPanel(config), 0, 1);
            root.Controls.Add(BuildGridPanel(), 0, 2);
            root.Controls.Add(BuildButtonsPanel(), 0, 3);

            AcceptButton = _build;
            CancelButton = _cancel;
        }

        public AtsBuildInput? Result { get; private set; }

        private Control BuildTopPanel(IEnumerable<string> clientNames, Config config)
        {
            _ = config;

            var card = CreateCardPanel();
            card.Margin = new Padding(0, 0, 0, 10);

            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 3,
            };
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Controls.Add(content);

            var headerRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 12),
            };

            var badge = new Panel
            {
                Size = new Size(44, 44),
                BackColor = AccentColor,
                Margin = new Padding(0, 0, 10, 0),
            };
            var badgeText = new Label
            {
                Dock = DockStyle.Fill,
                Text = "ATS",
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point),
            };
            badge.Controls.Add(badgeText);

            var titleStack = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 2, 0, 0),
            };

            titleStack.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "ATS Background Builder",
                Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(17, 24, 39),
                Margin = new Padding(0, 0, 0, 2),
            }, 0, 0);
            titleStack.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "Build section geometry from legal land descriptions",
                ForeColor = MutedTextColor,
                Margin = new Padding(0),
            }, 0, 1);

            headerRow.Controls.Add(badge);
            headerRow.Controls.Add(titleStack);
            content.Controls.Add(headerRow, 0, 0);

            var inputs = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Margin = new Padding(0, 0, 0, 8),
            };
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _clientCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _clientCombo.Width = 520;
            _clientCombo.Margin = new Padding(0, 2, 0, 0);
            var clients = (clientNames ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s)
                .ToList();
            foreach (var c in clients)
            {
                _clientCombo.Items.Add(c);
            }
            if (_clientCombo.Items.Count > 0)
            {
                _clientCombo.SelectedIndex = 0;
            }

            var clientPanel = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 0, 16, 0),
            };
            clientPanel.Controls.Add(CreateFieldLabel("Client"), 0, 0);
            clientPanel.Controls.Add(_clientCombo, 0, 1);

            _zone11Radio.Text = "11";
            _zone11Radio.AutoSize = true;
            _zone11Radio.Checked = true;
            _zone11Radio.Margin = new Padding(0, 2, 18, 0);

            _zone12Radio.Text = "12";
            _zone12Radio.AutoSize = true;
            _zone12Radio.Margin = new Padding(0, 2, 0, 0);

            var zoneFlow = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 0),
            };
            zoneFlow.Controls.Add(_zone11Radio);
            zoneFlow.Controls.Add(_zone12Radio);

            var zonePanel = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 2,
            };
            zonePanel.Controls.Add(CreateFieldLabel("Zone"), 0, 0);
            zonePanel.Controls.Add(zoneFlow, 0, 1);

            inputs.Controls.Add(clientPanel, 0, 0);
            inputs.Controls.Add(zonePanel, 1, 0);
            content.Controls.Add(inputs, 0, 1);

            content.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "M/RGE/TWP carry down when blank. Leave SEC blank on a row with M/RGE/TWP to build sections 1-36.",
                ForeColor = MutedTextColor,
                Margin = new Padding(0, 2, 0, 0),
            }, 0, 2);

            return card;
        }

        private Control BuildOptionsPanel(Config config)
        {
            var card = CreateCardPanel();
            card.Margin = new Padding(0, 0, 0, 10);

            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 2,
            };
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Controls.Add(content);

            content.Controls.Add(CreateSectionTitleLabel("Build Settings"), 0, 0);

            var settingsGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, 0),
            };
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _textHeight.DecimalPlaces = 2;
            _textHeight.Minimum = 1;
            _textHeight.Maximum = 100;
            _textHeight.Increment = 0.5m;
            _textHeight.Value = (decimal)Math.Max(1.0, Math.Min(100.0, config?.TextHeight ?? 10.0));
            _textHeight.Width = 90;

            _maxAttempts.Minimum = 1;
            _maxAttempts.Maximum = 200;
            _maxAttempts.Value = Math.Max(1, Math.Min(200, config?.MaxOverlapAttempts ?? 25));
            _maxAttempts.Width = 90;

            var numericStack = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 4,
                Margin = new Padding(0, 0, 24, 0),
            };
            numericStack.Controls.Add(CreateFieldLabel("Text Height"), 0, 0);
            numericStack.Controls.Add(_textHeight, 0, 1);
            numericStack.Controls.Add(CreateFieldLabel("Max Overlap Attempts"), 0, 2);
            numericStack.Controls.Add(_maxAttempts, 0, 3);

            ConfigureOptionCheckBox(_includeDispoLinework, "Disposition linework", false);
            ConfigureOptionCheckBox(_includeDispoLabels, "Disposition labels", false);
            ConfigureOptionCheckBox(_includeAtsFabric, "ATS fabric", false);
            ConfigureOptionCheckBox(_includeLsds, "LSDs", false);
            ConfigureOptionCheckBox(_includeP3Shapes, "Include P3 Shapes", false);
            ConfigureOptionCheckBox(_includeCompassMapping, "COMPASS MAPPING", false);
            ConfigureOptionCheckBox(_includeCrownReservations, "Crown Reservations", false);
            ConfigureOptionCheckBox(_checkPlsr, "Check PLSR", false);
            ConfigureOptionCheckBox(_allowMultiQuarterDispositions, "1/4 Definition", config?.AllowMultiQuarterDispositions ?? false);
            ConfigureOptionCheckBox(_includeQuarterSectionLabels, "1/4 SEC Labels", false);

            var toggleColumnA = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0, 0, 24, 0),
            };
            toggleColumnA.Controls.Add(_includeDispoLinework);
            toggleColumnA.Controls.Add(_includeDispoLabels);
            toggleColumnA.Controls.Add(_includeAtsFabric);
            toggleColumnA.Controls.Add(_includeLsds);

            var toggleColumnB = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 0),
            };
            toggleColumnB.Controls.Add(_includeP3Shapes);
            toggleColumnB.Controls.Add(_includeCompassMapping);
            toggleColumnB.Controls.Add(_includeCrownReservations);
            toggleColumnB.Controls.Add(_checkPlsr);
            toggleColumnB.Controls.Add(_allowMultiQuarterDispositions);
            toggleColumnB.Controls.Add(_includeQuarterSectionLabels);

            settingsGrid.Controls.Add(numericStack, 0, 0);
            settingsGrid.Controls.Add(toggleColumnA, 1, 0);
            settingsGrid.Controls.Add(toggleColumnB, 2, 0);

            content.Controls.Add(settingsGrid, 0, 1);
            return card;
        }

        private Control BuildGridPanel()
        {
            var card = CreateCardPanel();
            card.Margin = new Padding(0, 0, 0, 10);

            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 4,
            };
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Controls.Add(content);

            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, 8),
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var infoButton = new Label
            {
                AutoSize = true,
                Text = "i",
                ForeColor = MutedTextColor,
                Margin = new Padding(0, 2, 0, 0),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point),
                Cursor = Cursors.Hand,
            };
            _toolTip.SetToolTip(
                infoButton,
                "Grid behavior:\n" +
                "- M/RGE/TWP carry down from row above when blank\n" +
                "- If SEC is blank with M/RGE/TWP provided, sections 1-36 are built\n" +
                "- Quarter values: NW, NE, SW, SE, N, S, E, W, ALL");

            header.Controls.Add(CreateSectionTitleLabel("Section Grid"), 0, 0);
            header.Controls.Add(infoButton, 1, 0);
            content.Controls.Add(header, 0, 0);

            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = true;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _grid.MultiSelect = false;
            _grid.ScrollBars = ScrollBars.Both;
            _grid.BackgroundColor = CardColor;
            _grid.BorderStyle = BorderStyle.FixedSingle;
            _grid.EnableHeadersVisualStyles = false;
            _grid.GridColor = Color.FromArgb(224, 228, 235);
            _grid.ColumnHeadersHeight = 32;
            _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = HeaderBackColor;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(55, 65, 81);
            _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
            _grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(17, 24, 39);
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 250, 252);
            _grid.RowTemplate.Height = 30;
            _grid.RowsAdded += (_, __) => RefreshGridRowNumbers();
            _grid.RowsRemoved += (_, __) => RefreshGridRowNumbers();

            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ROW",
                HeaderText = "#",
                ReadOnly = true,
                FillWeight = 25,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "M", HeaderText = "M", FillWeight = 70 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RGE", HeaderText = "RGE", FillWeight = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TWP", HeaderText = "TWP", FillWeight = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "SEC", HeaderText = "SEC", FillWeight = 80 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "HQ", HeaderText = "H/QS", FillWeight = 110 });

            _grid.Rows.Add();
            RefreshGridRowNumbers();

            content.Controls.Add(_grid, 0, 1);

            _addGridRow.Text = "Add Row";
            _addGridRow.Width = 96;
            _addGridRow.Height = 30;
            _addGridRow.Margin = new Padding(0, 8, 0, 8);
            _addGridRow.Click += (_, __) => _grid.Rows.Add();
            ConfigureOutlineButton(_addGridRow);
            content.Controls.Add(_addGridRow, 0, 2);

            var helperPanel = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                BackColor = InfoBackColor,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(10, 8, 10, 8),
                Margin = new Padding(0),
            };
            helperPanel.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(1000, 0),
                Text = "Tip: M/RGE/TWP values carry down when blank. Leave SEC empty to build all sections 1-36. Quarter values: NW, NE, SW, SE, N, S, E, W, ALL.",
                ForeColor = InfoTextColor,
            });
            content.Controls.Add(helperPanel, 0, 3);

            return card;
        }

        private Control BuildButtonsPanel()
        {
            var card = CreateCardPanel();
            card.Margin = new Padding(0);

            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1,
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            card.Controls.Add(panel);

            var leftActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
            };

            leftActions.Controls.Add(CreateFieldLabel("Shape Type", margin: new Padding(0, 7, 8, 0)));

            _shapeTypeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _shapeTypeCombo.Width = 180;
            _shapeTypeCombo.Margin = new Padding(0, 2, 10, 0);
            _shapeTypeCombo.Items.Add("Disposition");
            _shapeTypeCombo.Items.Add("Compass Mapping");
            _shapeTypeCombo.Items.Add("Crown Reservations");
            _shapeTypeCombo.SelectedIndex = 0;
            leftActions.Controls.Add(_shapeTypeCombo);

            _updateShape.Text = "Update Shape";
            _updateShape.Width = 130;
            _updateShape.Height = 32;
            _updateShape.Margin = new Padding(0, 0, 0, 0);
            _updateShape.Click += (_, __) => OnUpdateShape();
            ConfigureOutlineButton(_updateShape);
            leftActions.Controls.Add(_updateShape);

            ConfigureOptionCheckBox(_autoCheckUpdateShapesAlways, "CHECK/UPDATE SHAPES ALWAYS", false);
            _autoCheckUpdateShapesAlways.Margin = new Padding(10, 7, 0, 0);
            leftActions.Controls.Add(_autoCheckUpdateShapesAlways);

            _build.Text = "BUILD";
            _build.Width = 120;
            _build.Height = 32;
            _build.Margin = new Padding(0, 0, 0, 0);
            _build.Click += (_, __) => OnBuild();
            ConfigurePrimaryButton(_build);

            _cancel.Text = "Cancel";
            _cancel.Width = 90;
            _cancel.Height = 32;
            _cancel.Margin = new Padding(0, 0, 10, 0);
            _cancel.DialogResult = DialogResult.Cancel;
            ConfigureOutlineButton(_cancel);

            var rightActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
            };
            rightActions.Controls.Add(_cancel);
            rightActions.Controls.Add(_build);

            panel.Controls.Add(leftActions, 0, 0);
            panel.Controls.Add(rightActions, 1, 0);

            return card;
        }

        private static Panel CreateCardPanel()
        {
            return new Panel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                BackColor = CardColor,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(16, 14, 16, 14),
            };
        }

        private static Label CreateSectionTitleLabel(string text)
        {
            return new Label
            {
                AutoSize = true,
                Text = text,
                Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(31, 41, 55),
                Margin = new Padding(0, 0, 0, 10),
            };
        }

        private static Label CreateFieldLabel(string text, Padding? margin = null)
        {
            return new Label
            {
                AutoSize = true,
                Text = text,
                ForeColor = Color.FromArgb(55, 65, 81),
                Margin = margin ?? new Padding(0, 0, 0, 4),
            };
        }

        private static void ConfigureOptionCheckBox(CheckBox checkBox, string text, bool isChecked)
        {
            checkBox.Text = text;
            checkBox.Checked = isChecked;
            checkBox.AutoSize = true;
            checkBox.Margin = new Padding(0, 0, 0, 7);
        }

        private static void ConfigurePrimaryButton(Button button)
        {
            button.BackColor = AccentColor;
            button.ForeColor = Color.White;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
            button.UseVisualStyleBackColor = false;
        }

        private static void ConfigureOutlineButton(Button button)
        {
            button.BackColor = Color.White;
            button.ForeColor = Color.FromArgb(31, 41, 55);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(209, 213, 219);
            button.FlatAppearance.BorderSize = 1;
            button.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            button.UseVisualStyleBackColor = false;
        }

        private void RefreshGridRowNumbers()
        {
            if (!_grid.Columns.Contains("ROW"))
            {
                return;
            }

            for (var i = 0; i < _grid.Rows.Count; i++)
            {
                var row = _grid.Rows[i];
                if (row.IsNewRow)
                {
                    continue;
                }

                row.Cells["ROW"].Value = (i + 1).ToString(CultureInfo.InvariantCulture);
            }
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
                    MessageBox.Show(this, newestFolderError, "Update Shape", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (confirm != DialogResult.Yes)
                {
                    return;
                }

                _updateShape.Enabled = false;
                var previousCursor = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;
                try
                {
                    var copiedCount = ReplaceDirectoryContents(newestFolder, DispositionShapeDestinationFolder);
                    MessageBox.Show(
                        this,
                        $"Shape update complete.\n\nCopied {copiedCount} file(s) from:\n{newestFolder}\n\nto:\n{DispositionShapeDestinationFolder}",
                        "Update Shape",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        "Shape update failed:\n" + ex.Message,
                        "Update Shape",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                finally
                {
                    if (previousCursor != null)
                    {
                        Cursor.Current = previousCursor;
                    }
                    _updateShape.Enabled = true;
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
                    MessageBox.Show(this, rootError, "Update Shape", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (confirm != DialogResult.Yes)
                {
                    return;
                }

                _updateShape.Enabled = false;
                var previousCursor = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;
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
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        "Shape update failed:\n" + ex.Message,
                        "Update Shape",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                finally
                {
                    if (previousCursor != null)
                    {
                        Cursor.Current = previousCursor;
                    }
                    _updateShape.Enabled = true;
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
                    MessageBox.Show(this, rootError, "Update Shape", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (confirm != DialogResult.Yes)
                {
                    return;
                }

                _updateShape.Enabled = false;
                var previousCursor = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;
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
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        "Shape update failed:\n" + ex.Message,
                        "Update Shape",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                finally
                {
                    if (previousCursor != null)
                    {
                        Cursor.Current = previousCursor;
                    }
                    _updateShape.Enabled = true;
                }
                return;
            }

            MessageBox.Show(this, $"Unsupported shape type: {shapeType}", "Update Shape", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

            foreach (var root in roots)
            {
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
                // Unambiguous: dd-MM-yyyy
                day = first;
                month = second;
            }
            else if (second > 12 && first <= 12)
            {
                // Unambiguous: MM-dd-yyyy
                month = first;
                day = second;
            }
            else
            {
                // Ambiguous (both <= 12): default to dd-MM-yyyy to match FTP folder naming.
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

        private void OnBuild()
        {
            var client = _clientCombo.SelectedItem?.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(client))
            {
                MessageBox.Show(this, "Client is required.", "ATSBUILD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var zone = _zone12Radio.Checked ? 12 : 11;

            var requests = ParseSectionRequests(zone);
            if (requests.Count == 0)
            {
                MessageBox.Show(this, "At least one section row is required.", "ATSBUILD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Result = new AtsBuildInput
            {
                CurrentClient = client,
                Zone = zone,
                TextHeight = (double)_textHeight.Value,
                MaxOverlapAttempts = (int)_maxAttempts.Value,
                IncludeDispositionLinework = _includeDispoLinework.Checked,
                IncludeDispositionLabels = _includeDispoLabels.Checked,
                AllowMultiQuarterDispositions = _allowMultiQuarterDispositions.Checked,
                IncludeAtsFabric = _includeAtsFabric.Checked,
                DrawLsdSubdivisionLines = _includeLsds.Checked,
                IncludeP3Shapefiles = _includeP3Shapes.Checked,
                IncludeCompassMapping = _includeCompassMapping.Checked,
                IncludeCrownReservations = _includeCrownReservations.Checked,
                AutoCheckUpdateShapefilesAlways = _autoCheckUpdateShapesAlways.Checked,
                CheckPlsr = _checkPlsr.Checked,
                IncludeQuarterSectionLabels = _includeQuarterSectionLabels.Checked,
                UseAlignedDimensions = true,
            };
            Result.SectionRequests.AddRange(requests);

            if (Result.CheckPlsr)
            {
                using (var dialog = new OpenFileDialog())
                {
                    dialog.Filter = "PLSR XML (*.xml)|*.xml|All files (*.*)|*.*";
                    dialog.Multiselect = true;
                    dialog.Title = "Select PLSR XML file(s)";
                    dialog.InitialDirectory = Environment.CurrentDirectory;
                    if (dialog.ShowDialog(this) != DialogResult.OK || dialog.FileNames.Length == 0)
                    {
                        MessageBox.Show(this, "PLSR check requires at least one XML file.", "ATSBUILD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    foreach (var path in dialog.FileNames)
                    {
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                            Result.PlsrXmlPaths.Add(path);
                    }
                }

                if (Result.PlsrXmlPaths.Count == 0)
                {
                    MessageBox.Show(this, "PLSR check requires at least one XML file.", "ATSBUILD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private List<SectionRequest> ParseSectionRequests(int zone)
        {
            var requests = new List<SectionRequest>();

            string lastMeridian = string.Empty;
            string lastRange = string.Empty;
            string lastTownship = string.Empty;
            string lastSection = string.Empty;

            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow)
                    continue;

                string m = GetCell(row, "M");
                string rge = GetCell(row, "RGE");
                string twp = GetCell(row, "TWP");
                string sec = GetCell(row, "SEC");
                string q = GetCell(row, "HQ");

                bool anyFilled =
                    !string.IsNullOrWhiteSpace(m) ||
                    !string.IsNullOrWhiteSpace(rge) ||
                    !string.IsNullOrWhiteSpace(twp) ||
                    !string.IsNullOrWhiteSpace(sec) ||
                    !string.IsNullOrWhiteSpace(q);

                if (!anyFilled)
                    continue;

                var hasExplicitMeridian = !string.IsNullOrWhiteSpace(m);
                var hasExplicitRange = !string.IsNullOrWhiteSpace(rge);
                var hasExplicitTownship = !string.IsNullOrWhiteSpace(twp);
                var hasExplicitSection = !string.IsNullOrWhiteSpace(sec);

                // Carry-down behavior (only when row is active).
                if (string.IsNullOrWhiteSpace(m)) m = lastMeridian;
                if (string.IsNullOrWhiteSpace(rge)) rge = lastRange;
                if (string.IsNullOrWhiteSpace(twp)) twp = lastTownship;
                var expandAllSections =
                    !hasExplicitSection &&
                    (hasExplicitMeridian || hasExplicitRange || hasExplicitTownship);
                if (!expandAllSections && string.IsNullOrWhiteSpace(sec))
                {
                    sec = lastSection;
                }

                // Quarter defaults to ALL if blank.
                if (string.IsNullOrWhiteSpace(q)) q = "ALL";

                // Validate carry-down didn't leave required values missing.
                if (string.IsNullOrWhiteSpace(m) || string.IsNullOrWhiteSpace(rge) || string.IsNullOrWhiteSpace(twp))
                {
                    MessageBox.Show(
                        this,
                        "Row is missing M/RGE/TWP and no value above to carry down.\n\n" +
                        "Tip: Fill the first row completely, then you can leave repeated values blank on lower rows.",
                        "ATSBUILD",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return new List<SectionRequest>();
                }

                if (!expandAllSections && string.IsNullOrWhiteSpace(sec))
                {
                    MessageBox.Show(
                        this,
                        "SEC is blank and there is no section above to carry down.\n\n" +
                        "Tip: Enter SEC, or provide M/RGE/TWP on that row with SEC blank to build sections 1-36.",
                        "ATSBUILD",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return new List<SectionRequest>();
                }

                lastMeridian = m;
                lastRange = rge;
                lastTownship = twp;

                if (!TryParseQuarter(q, out var quarter))
                {
                    MessageBox.Show(this, $"Invalid quarter value: '{q}'. Use NW, NE, SW, SE, N, S, E, W, or ALL.", "ATSBUILD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return new List<SectionRequest>();
                }

                if (expandAllSections)
                {
                    for (var sectionNumber = 1; sectionNumber <= 36; sectionNumber++)
                    {
                        var key = new SectionKey(zone, sectionNumber.ToString(CultureInfo.InvariantCulture), twp, rge, m);
                        requests.Add(new SectionRequest(quarter, key, "AUTO"));
                    }

                    // Keep section carry-down explicit after an all-sections expansion row.
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

        private static string GetCell(DataGridViewRow row, string columnName)
        {
            try
            {
                var value = row.Cells[columnName].Value;
                return value?.ToString()?.Trim() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryParseQuarter(string raw, out QuarterSelection quarter)
        {
            quarter = QuarterSelection.None;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

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

    }
}

/////////////////////////////////////////////////////////////////////
