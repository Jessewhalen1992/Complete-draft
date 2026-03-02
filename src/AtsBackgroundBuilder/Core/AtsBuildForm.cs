/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
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
        public bool IncludeSurfaceImpact { get; set; } = false;

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
        private readonly CheckBox _includeSurfaceImpact = new CheckBox();
        private readonly CheckBox _allowMultiQuarterDispositions = new CheckBox();
        private readonly CheckBox _includeQuarterSectionLabels = new CheckBox();
        private readonly CheckBox _autoCheckUpdateShapesAlways = new CheckBox();
        private readonly DataGridView _grid = new DataGridView();
        private readonly Button _addGridRow = new Button();
        private readonly ComboBox _shapeTypeCombo = new ComboBox();
        private readonly Button _updateShape = new Button();
        private readonly Button _addSectionsFromBoundary = new Button();
        private readonly Button _build = new Button();
        private readonly Button _cancel = new Button();
        private readonly ToolTip _toolTip = new ToolTip();
        private readonly Config _config;
        private static readonly Color CanvasColor = Color.FromArgb(246, 248, 251);
        private static readonly Color CardColor = Color.White;
        private static readonly Color MutedTextColor = Color.FromArgb(98, 109, 127);
        private static readonly Color AccentColor = Color.FromArgb(37, 99, 235);
        private static readonly Color HeaderBackColor = Color.FromArgb(243, 244, 246);
        private static readonly Color InfoBackColor = Color.FromArgb(239, 246, 255);
        private static readonly Color InfoTextColor = Color.FromArgb(30, 64, 175);

        public AtsBuildForm(IEnumerable<string> clientNames, Config config)
        {
            _config = config ?? new Config();
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

            root.Controls.Add(BuildTopPanel(clientNames, _config), 0, 0);
            root.Controls.Add(BuildOptionsPanel(_config), 0, 1);
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
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, 0),
            };
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

            var optionCheckBoxes = CreateOptionCheckBoxMap();
            foreach (var option in AtsBuildOptionCatalog.Options)
            {
                if (!optionCheckBoxes.TryGetValue(option.Key, out var checkBox))
                {
                    continue;
                }

                ConfigureOptionCheckBox(checkBox, option.Label, option.ResolveDefaultChecked(config));
            }

            var groupedOptions = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = AtsBuildOptionCatalog.Groups.Count,
                RowCount = 1,
            };
            for (var i = 0; i < AtsBuildOptionCatalog.Groups.Count; i++)
            {
                groupedOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / AtsBuildOptionCatalog.Groups.Count));
            }

            settingsGrid.Controls.Add(numericStack, 0, 0);
            for (var i = 0; i < AtsBuildOptionCatalog.Groups.Count; i++)
            {
                var group = AtsBuildOptionCatalog.Groups[i];
                var groupCheckBoxes = AtsBuildOptionCatalog.Options
                    .Where(option => option.Group == group)
                    .Select(option => optionCheckBoxes[option.Key])
                    .ToArray();
                var groupCard = CreateOptionGroupPanel(
                    AtsBuildOptionCatalog.GetGroupTitle(group),
                    groupCheckBoxes);
                groupedOptions.Controls.Add(groupCard, i, 0);
            }
            settingsGrid.Controls.Add(groupedOptions, 1, 0);

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
            foreach (var supportedType in ShapeUpdateService.SupportedShapeTypes)
            {
                _shapeTypeCombo.Items.Add(supportedType);
            }
            _shapeTypeCombo.SelectedIndex = 0;
            leftActions.Controls.Add(_shapeTypeCombo);

            _updateShape.Text = "Update Shape";
            _updateShape.Width = 130;
            _updateShape.Height = 32;
            _updateShape.Margin = new Padding(0, 0, 0, 0);
            _updateShape.Click += (_, __) => OnUpdateShape();
            ConfigureOutlineButton(_updateShape);
            leftActions.Controls.Add(_updateShape);

            _addSectionsFromBoundary.Text = "ADD SECTIONS FROM BDY";
            _addSectionsFromBoundary.Width = 190;
            _addSectionsFromBoundary.Height = 32;
            _addSectionsFromBoundary.Margin = new Padding(10, 0, 0, 0);
            _addSectionsFromBoundary.Click += (_, __) => OnAddSectionsFromBoundary();
            ConfigureOutlineButton(_addSectionsFromBoundary);
            leftActions.Controls.Add(_addSectionsFromBoundary);

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

        private Dictionary<AtsBuildOptionKey, CheckBox> CreateOptionCheckBoxMap()
        {
            return new Dictionary<AtsBuildOptionKey, CheckBox>
            {
                [AtsBuildOptionKey.IncludeAtsFabric] = _includeAtsFabric,
                [AtsBuildOptionKey.IncludeLsds] = _includeLsds,
                [AtsBuildOptionKey.AllowMultiQuarterDispositions] = _allowMultiQuarterDispositions,
                [AtsBuildOptionKey.IncludeQuarterSectionLabels] = _includeQuarterSectionLabels,
                [AtsBuildOptionKey.IncludeDispoLinework] = _includeDispoLinework,
                [AtsBuildOptionKey.IncludeDispoLabels] = _includeDispoLabels,
                [AtsBuildOptionKey.IncludeCrownReservations] = _includeCrownReservations,
                [AtsBuildOptionKey.IncludeP3Shapes] = _includeP3Shapes,
                [AtsBuildOptionKey.IncludeCompassMapping] = _includeCompassMapping,
                [AtsBuildOptionKey.CheckPlsr] = _checkPlsr,
                [AtsBuildOptionKey.IncludeSurfaceImpact] = _includeSurfaceImpact,
            };
        }

        private static Control CreateOptionGroupPanel(string title, params CheckBox[] checkBoxes)
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(10, 8, 10, 4),
                Margin = new Padding(0, 0, 10, 0),
            };

            var stack = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0),
            };
            stack.Controls.Add(new Label
            {
                AutoSize = true,
                Text = title,
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(55, 65, 81),
                Margin = new Padding(0, 0, 0, 8),
            });

            foreach (var checkBox in checkBoxes)
            {
                stack.Controls.Add(checkBox);
            }

            panel.Controls.Add(stack);
            return panel;
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
            if (!ShapeUpdateService.TryPreparePlan(shapeType, out var plan, out var planError))
            {
                MessageBox.Show(this, planError, "Update Shape", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (plan == null)
            {
                MessageBox.Show(this, "Shape update plan was not created.", "Update Shape", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                this,
                plan.ConfirmationMessage,
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
                var copiedCount = ShapeUpdateService.ExecutePlan(plan);
                MessageBox.Show(
                    this,
                    $"Shape update complete.\n\nCopied {copiedCount} file(s) from:\n{plan.SourceDisplayPath}\n\nto:\n{plan.DestinationPath}",
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
        }

        private void OnAddSectionsFromBoundary()
        {
            try
            {
            var importedRows = new List<BoundarySectionImportService.SectionGridEntry>();
            var serviceMessage = string.Empty;
            var cancelled = false;
            var zone = _zone12Radio.Checked ? 12 : 11;
            var succeeded = BoundarySectionImportService.TryCollectEntriesFromBoundary(
                _config,
                zone,
                out importedRows,
                out serviceMessage,
                out cancelled,
                Handle);
            Activate();

            if (!succeeded)
            {
                if (!cancelled && !string.IsNullOrWhiteSpace(serviceMessage))
                {
                    MessageBox.Show(this, serviceMessage, "ATSBUILD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                return;
            }

            // If the grid only contains placeholder empty rows, clear them before appending imported rows.
            if (!HasAnyPopulatedGridRows())
            {
                for (var i = _grid.Rows.Count - 1; i >= 0; i--)
                {
                    var row = _grid.Rows[i];
                    if (row == null || row.IsNewRow)
                    {
                        continue;
                    }

                    _grid.Rows.RemoveAt(i);
                }
            }

            var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row == null || row.IsNewRow || !RowHasAnyValue(row))
                {
                    continue;
                }

                existingKeys.Add(BuildRowKey(
                    GetCell(row, "M"),
                    GetCell(row, "RGE"),
                    GetCell(row, "TWP"),
                    GetCell(row, "SEC"),
                    GetCell(row, "HQ")));
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

                _grid.Rows.Add(string.Empty, entry.Meridian, entry.Range, entry.Township, entry.Section, entry.Quarter);
                added++;
            }

            if (added > 0)
            {
                RefreshGridRowNumbers();
            }

            MessageBox.Show(
                this,
                BuildBoundaryImportResultMessage(serviceMessage, added, duplicates),
                "ATSBUILD",
                MessageBoxButtons.OK,
                added > 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                WriteUiException("AtsBuildForm.OnAddSectionsFromBoundary", ex);
                MessageBox.Show(
                    this,
                    "Boundary import failed:\n" + ex.Message,
                    "ATSBUILD",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private bool HasAnyPopulatedGridRows()
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row == null || row.IsNewRow)
                {
                    continue;
                }

                if (RowHasAnyValue(row))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool RowHasAnyValue(DataGridViewRow row)
        {
            if (row == null)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(row.Cells["M"]?.Value?.ToString()) ||
                   !string.IsNullOrWhiteSpace(row.Cells["RGE"]?.Value?.ToString()) ||
                   !string.IsNullOrWhiteSpace(row.Cells["TWP"]?.Value?.ToString()) ||
                   !string.IsNullOrWhiteSpace(row.Cells["SEC"]?.Value?.ToString()) ||
                   !string.IsNullOrWhiteSpace(row.Cells["HQ"]?.Value?.ToString());
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
                IncludeSurfaceImpact = _includeSurfaceImpact.Checked,
                IncludeQuarterSectionLabels = _includeQuarterSectionLabels.Checked,
                UseAlignedDimensions = true,
            };
            Result.SectionRequests.AddRange(requests);

            if (PlsrXmlSelectionService.RequiresXml(Result))
            {
                var candidatePaths = Array.Empty<string>();
                using (var dialog = new OpenFileDialog())
                {
                    dialog.Filter = PlsrXmlSelectionService.DialogFilter;
                    dialog.Multiselect = true;
                    dialog.Title = PlsrXmlSelectionService.DialogTitle;
                    dialog.InitialDirectory = Environment.CurrentDirectory;
                    var dialogResult = dialog.ShowDialog(this);
                    if (dialogResult == DialogResult.OK)
                    {
                        candidatePaths = dialog.FileNames;
                    }
                }

                if (!PlsrXmlSelectionService.TryGetValidPaths(
                        candidatePaths,
                        out var validXmlPaths,
                        out _))
                {
                    MessageBox.Show(this, PlsrXmlSelectionService.RequiredSelectionMessage, "ATSBUILD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                Result.PlsrXmlPaths.AddRange(validXmlPaths);
            }

            DialogResult = DialogResult.OK;
            Close();
            }
            catch (Exception ex)
            {
                WriteUiException("AtsBuildForm.OnBuild", ex);
                MessageBox.Show(
                    this,
                    "Build setup failed:\n" + ex.Message,
                    "ATSBUILD",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
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
