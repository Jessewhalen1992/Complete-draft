/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AtsBackgroundBuilder
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

        /// <summary>
        /// When false, imported disposition linework is removed at cleanup.
        /// </summary>
        public bool IncludeDispositionLinework { get; set; } = true;
        public bool IncludeDispositionLabels { get; set; } = true;
        public bool IncludeQuarterSectionLabels { get; set; } = false;

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
        private readonly ComboBox _zoneCombo = new ComboBox();
        private readonly NumericUpDown _textHeight = new NumericUpDown();
        private readonly NumericUpDown _maxAttempts = new NumericUpDown();
        private readonly CheckBox _includeDispoLinework = new CheckBox();
        private readonly CheckBox _includeDispoLabels = new CheckBox();
        private readonly CheckBox _includeAtsFabric = new CheckBox();
        private readonly CheckBox _includeLsds = new CheckBox();
        private readonly CheckBox _includeP3Shapes = new CheckBox();
        private readonly CheckBox _checkPlsr = new CheckBox();
        private readonly CheckBox _includeQuarterSectionLabels = new CheckBox();
        private readonly DataGridView _grid = new DataGridView();
        private readonly Button _build = new Button();
        private readonly Button _cancel = new Button();

        public AtsBuildForm(IEnumerable<string> clientNames, Config config)
        {
            Text = "ATS Background Builder";
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;

            // Slightly larger to comfortably fit the grid.
            ClientSize = new Size(1200, 650);
            MinimumSize = new Size(1000, 600);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(10),
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
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 4,
                RowCount = 2,
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            var clientLabel = new Label
            {
                Text = "Client",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 6, 6, 6)
            };

            _clientCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _clientCombo.Width = 260;
            var clients = (clientNames ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
            foreach (var c in clients)
                _clientCombo.Items.Add(c);
            if (_clientCombo.Items.Count > 0)
                _clientCombo.SelectedIndex = 0;

            var zoneLabel = new Label
            {
                Text = "Zone",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(15, 6, 6, 6)
            };

            _zoneCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _zoneCombo.Width = 80;
            _zoneCombo.Items.Add(11);
            _zoneCombo.Items.Add(12);
            _zoneCombo.SelectedIndex = 0;

            // Optional: preload from config default if you decide to add one later.

            var help = new Label
            {
                Text = "Enter sections / quarters below. M, RGE, TWP, SEC, SEC TYPE carry down when left blank.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 8, 0, 0)
            };

            panel.Controls.Add(clientLabel, 0, 0);
            panel.Controls.Add(_clientCombo, 1, 0);
            panel.Controls.Add(zoneLabel, 2, 0);
            panel.Controls.Add(_zoneCombo, 3, 0);
            panel.Controls.Add(help, 0, 1);
            panel.SetColumnSpan(help, 4);

            return panel;
        }

        private Control BuildOptionsPanel(Config config)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 9,
                RowCount = 2,
                Margin = new Padding(0, 10, 0, 10)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var textHeightLabel = new Label
            {
                Text = "Text height",
                AutoSize = true,
                Margin = new Padding(0, 6, 6, 6)
            };

            _textHeight.DecimalPlaces = 2;
            _textHeight.Minimum = 1;
            _textHeight.Maximum = 100;
            _textHeight.Increment = 0.5m;
            _textHeight.Value = (decimal)Math.Max(1.0, Math.Min(100.0, config?.TextHeight ?? 10.0));
            _textHeight.Width = 80;

            var maxAttemptsLabel = new Label
            {
                Text = "Max overlap attempts",
                AutoSize = true,
                Margin = new Padding(15, 6, 6, 6)
            };

            _maxAttempts.Minimum = 1;
            _maxAttempts.Maximum = 200;
            _maxAttempts.Value = Math.Max(1, Math.Min(200, config?.MaxOverlapAttempts ?? 25));
            _maxAttempts.Width = 80;

            _includeDispoLinework.Text = "Disposition linework";
            _includeDispoLinework.Checked = true;
            _includeDispoLinework.AutoSize = true;
            _includeDispoLinework.Margin = new Padding(0, 6, 10, 6);

            _includeDispoLabels.Text = "Disposition labels";
            _includeDispoLabels.Checked = true;
            _includeDispoLabels.AutoSize = true;
            _includeDispoLabels.Margin = new Padding(0, 6, 10, 6);

            _includeAtsFabric.Text = "ATS fabric";
            _includeAtsFabric.Checked = false;
            _includeAtsFabric.AutoSize = true;
            _includeAtsFabric.Margin = new Padding(0, 6, 10, 6);

            _includeLsds.Text = "LSDs";
            _includeLsds.Checked = false;
            _includeLsds.AutoSize = true;
            _includeLsds.Margin = new Padding(0, 6, 10, 6);

            _includeP3Shapes.Text = "Include P3 Shapes";
            _includeP3Shapes.Checked = false;
            _includeP3Shapes.AutoSize = true;
            _includeP3Shapes.Margin = new Padding(0, 6, 10, 6);

            _checkPlsr.Text = "Check PLSR";
            _checkPlsr.Checked = false;
            _checkPlsr.AutoSize = true;
            _checkPlsr.Margin = new Padding(0, 6, 10, 6);

            _includeQuarterSectionLabels.Text = "1/4 SEC. LABELS";
            _includeQuarterSectionLabels.Checked = false;
            _includeQuarterSectionLabels.AutoSize = true;
            _includeQuarterSectionLabels.Margin = new Padding(0, 6, 10, 6);

            var qHelp = new Label
            {
                Text = "Quarter values: NW, NE, SW, SE, N, S, E, W, ALL. SEC TYPE values: L-USEC, L-SEC",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 0, 0, 0)
            };

            panel.Controls.Add(textHeightLabel, 0, 0);
            panel.Controls.Add(_textHeight, 1, 0);
            panel.Controls.Add(maxAttemptsLabel, 2, 0);
            panel.Controls.Add(_maxAttempts, 3, 0);
            panel.Controls.Add(_includeDispoLinework, 0, 1);
            panel.Controls.Add(_includeDispoLabels, 1, 1);
            panel.Controls.Add(_includeAtsFabric, 2, 1);
            panel.Controls.Add(_includeLsds, 3, 1);
            panel.Controls.Add(_includeP3Shapes, 4, 1);
            panel.Controls.Add(_checkPlsr, 5, 1);
            panel.Controls.Add(_includeQuarterSectionLabels, 6, 1);
            panel.Controls.Add(qHelp, 7, 1);
            panel.SetColumnSpan(qHelp, 2);

            return panel;
        }

        private Control BuildGridPanel()
        {
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = true;
            _grid.AllowUserToDeleteRows = true;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _grid.MultiSelect = false;
            _grid.ScrollBars = ScrollBars.Both;

            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "M", HeaderText = "M" });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RGE", HeaderText = "RGE" });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TWP", HeaderText = "TWP" });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "SEC", HeaderText = "SEC" });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "HQ", HeaderText = "H/QS" });
            var secTypeColumn = new DataGridViewComboBoxColumn
            {
                Name = "SECTYPE",
                HeaderText = "SEC TYPE",
                DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
                FlatStyle = FlatStyle.Standard
            };
            secTypeColumn.Items.Add(string.Empty);
            secTypeColumn.Items.Add("L-USEC");
            secTypeColumn.Items.Add("L-SEC");
            _grid.Columns.Add(secTypeColumn);

            // Seed a few empty rows.
            for (int i = 0; i < 50; i++)
                _grid.Rows.Add();

            var panel = new Panel { Dock = DockStyle.Fill };
            panel.Controls.Add(_grid);
            return panel;
        }

        private Control BuildButtonsPanel()
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true
            };

            _build.Text = "BUILD";
            _build.Width = 120;
            _build.Height = 32;
            _build.Click += (_, __) => OnBuild();

            _cancel.Text = "Cancel";
            _cancel.Width = 90;
            _cancel.Height = 32;
            _cancel.DialogResult = DialogResult.Cancel;

            panel.Controls.Add(_build);
            panel.Controls.Add(_cancel);

            return panel;
        }

        private void OnBuild()
        {
            var client = _clientCombo.SelectedItem?.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(client))
            {
                MessageBox.Show(this, "Client is required.", "ATSBUILD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int zone = 11;
            if (_zoneCombo.SelectedItem is int zi)
                zone = zi;
            else
            {
                // Should never happen with DropDownList.
                _ = int.TryParse(_zoneCombo.SelectedItem?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out zone);
            }

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
                IncludeAtsFabric = _includeAtsFabric.Checked,
                DrawLsdSubdivisionLines = _includeLsds.Checked,
                IncludeP3Shapefiles = _includeP3Shapes.Checked,
                CheckPlsr = _checkPlsr.Checked,
                IncludeQuarterSectionLabels = _includeQuarterSectionLabels.Checked,
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
            string lastSecType = "L-USEC";

            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow)
                    continue;

                string m = GetCell(row, "M");
                string rge = GetCell(row, "RGE");
                string twp = GetCell(row, "TWP");
                string sec = GetCell(row, "SEC");
                string q = GetCell(row, "HQ");
                string secType = GetCell(row, "SECTYPE");

                bool anyFilled =
                    !string.IsNullOrWhiteSpace(m) ||
                    !string.IsNullOrWhiteSpace(rge) ||
                    !string.IsNullOrWhiteSpace(twp) ||
                    !string.IsNullOrWhiteSpace(sec) ||
                    !string.IsNullOrWhiteSpace(q) ||
                    !string.IsNullOrWhiteSpace(secType);

                if (!anyFilled)
                    continue;

                // Carry-down behavior (only when row is active).
                if (string.IsNullOrWhiteSpace(m)) m = lastMeridian;
                if (string.IsNullOrWhiteSpace(rge)) rge = lastRange;
                if (string.IsNullOrWhiteSpace(twp)) twp = lastTownship;
                if (string.IsNullOrWhiteSpace(sec)) sec = lastSection;
                if (string.IsNullOrWhiteSpace(secType)) secType = lastSecType;

                // Quarter defaults to ALL if blank.
                if (string.IsNullOrWhiteSpace(q)) q = "ALL";

                // Validate carry-down didn't leave required values missing.
                if (string.IsNullOrWhiteSpace(m) || string.IsNullOrWhiteSpace(rge) ||
                    string.IsNullOrWhiteSpace(twp) || string.IsNullOrWhiteSpace(sec))
                {
                    MessageBox.Show(
                        this,
                        "Row is missing M/RGE/TWP/SEC and no value above to carry down.\n\n" +
                        "Tip: Fill the first row completely, then you can leave repeated values blank on lower rows.",
                        "ATSBUILD",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return new List<SectionRequest>();
                }

                lastMeridian = m;
                lastRange = rge;
                lastTownship = twp;
                lastSection = sec;
                if (!TryNormalizeSecType(secType, out var normalizedSecType))
                {
                    MessageBox.Show(this, $"Invalid SEC TYPE value: '{secType}'. Use L-USEC or L-SEC.", "ATSBUILD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return new List<SectionRequest>();
                }
                lastSecType = normalizedSecType;

                if (!TryParseQuarter(q, out var quarter))
                {
                    MessageBox.Show(this, $"Invalid quarter value: '{q}'. Use NW, NE, SW, SE, N, S, E, W, or ALL.", "ATSBUILD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return new List<SectionRequest>();
                }

                var key = new SectionKey(zone, sec, twp, rge, m);
                requests.Add(new SectionRequest(quarter, key, normalizedSecType));
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

        private static bool TryNormalizeSecType(string raw, out string secType)
        {
            secType = "L-USEC";
            if (string.IsNullOrWhiteSpace(raw))
            {
                return true;
            }

            var s = raw.Trim().ToUpperInvariant();
            if (s == "L-USEC" || s == "USEC")
            {
                secType = "L-USEC";
                return true;
            }

            if (s == "L-SEC" || s == "SEC")
            {
                secType = "L-SEC";
                return true;
            }

            return false;
        }
    }
}

/////////////////////////////////////////////////////////////////////
