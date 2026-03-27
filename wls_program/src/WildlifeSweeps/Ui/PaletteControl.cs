using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Autodesk.AutoCAD.EditorInput;

namespace WildlifeSweeps
{
    public class PaletteControl : UserControl
    {
        private readonly PluginSettings _settings;

        private readonly ToolTip _toolTip = new ToolTip();
        private readonly ErrorProvider _errorProvider = new ErrorProvider();
        private readonly StatusStrip _statusStrip = new StatusStrip();
        private readonly ToolStripStatusLabel _statusLabel = new ToolStripStatusLabel();

        private readonly TextBox _photoStartNumber = new TextBox();
        private readonly CheckBox _completeFromPhotosBufferExcludeOutside = new CheckBox();
        private readonly CheckBox _completeFromPhotosBufferIncludeAll = new CheckBox();
        private readonly CheckBox _completeFromPhotosIncludeQuarterLinework = new CheckBox();
        private readonly TitleBlockControlService _titleBlockControlService = new TitleBlockControlService();
        private readonly NaturalRegionLookupService _naturalRegionLookupService = new NaturalRegionLookupService();
        private readonly Button _titleBlockControlsToggle = new Button();
        private readonly Panel _titleBlockControlsPanel = new Panel();
        private readonly ComboBox _titleBlockSweepType = new ComboBox();
        private readonly ComboBox _titleBlockPurpose = new ComboBox();
        private readonly TextBox _titleBlockLocation = new TextBox();
        private readonly Button _titleBlockAddSectionsFromBoundary = new Button();
        private readonly MonthCalendar _titleBlockSurveyCalendar = new MonthCalendar();
        private readonly Button _titleBlockAddSurveyDates = new Button();
        private readonly Button _titleBlockRemoveSurveyDates = new Button();
        private readonly ListBox _titleBlockSurveyDates = new ListBox();
        private readonly TextBox _titleBlockSubRegion = new TextBox();
        private readonly Button _titleBlockAddSubRegionFromFootprint = new Button();
        private readonly Button _titleBlockUpdateFindingStatement = new Button();
        private readonly TextBox _titleBlockMethodologySpacing = new TextBox();
        private readonly List<CheckBox> _titleBlockExistingLinearInfrastructure = new List<CheckBox>();
        private readonly List<CheckBox> _titleBlockExistingLeases = new List<CheckBox>();
        private readonly List<CheckBox> _titleBlockExistingLandOther = new List<CheckBox>();
        private TitleBlockControlInput _lastAppliedTitleBlockInput = CreateDefaultTitleBlockControlInput();

        public PaletteControl(PluginSettings settings)
        {
            _settings = settings;
            Dock = DockStyle.Fill;
            Padding = new Padding(8);
            AutoScroll = true;

            _errorProvider.ContainerControl = this;
            _statusStrip.Items.Add(_statusLabel);
            _statusStrip.Dock = DockStyle.Bottom;
            _statusLabel.Text = "Ready.";

            var photoGroup = new GroupBox
            {
                Text = "PHOTOJPG4",
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };

            var photoLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                AutoSize = true
            };
            AddRow(photoLayout, "Start #", _photoStartNumber, _settings.PhotoStartNumber);
            photoGroup.Controls.Add(photoLayout);

            var completeFromPhotosButton = new Button
            {
                Text = "Complete from Photos",
                Dock = DockStyle.Top,
                Height = 28
            };
            completeFromPhotosButton.Click += (_, __) => RunCompleteFromPhotos();
            var sortBufferPhotosButton = new Button
            {
                Text = "SORT 100m buffer Photos",
                Dock = DockStyle.Top,
                Height = 28
            };
            sortBufferPhotosButton.Click += (_, __) => RunSortBufferPhotos();
            _toolTip.SetToolTip(
                sortBufferPhotosButton,
                "Select a 100m buffer and photo folder, then copy in-buffer GPS photos into a \"within 100m\" subfolder.");

            _completeFromPhotosBufferIncludeAll.Text = "BUFFERS: PROPOSED / 100m / OUTSIDE";
            _completeFromPhotosBufferExcludeOutside.Text = "BUFFERS: PROPOSED / 100m";
            _completeFromPhotosIncludeQuarterLinework.Text = "Include L-QUATER linework";
            _completeFromPhotosBufferIncludeAll.AutoSize = true;
            _completeFromPhotosBufferExcludeOutside.AutoSize = true;
            _completeFromPhotosIncludeQuarterLinework.AutoSize = true;
            _completeFromPhotosBufferIncludeAll.CheckedChanged += (_, __) =>
            {
                if (_completeFromPhotosBufferIncludeAll.Checked)
                {
                    _completeFromPhotosBufferExcludeOutside.Checked = false;
                }
            };
            _completeFromPhotosBufferExcludeOutside.CheckedChanged += (_, __) =>
            {
                if (_completeFromPhotosBufferExcludeOutside.Checked)
                {
                    _completeFromPhotosBufferIncludeAll.Checked = false;
                }
            };

            var completeFromPhotosOptions = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };
            completeFromPhotosOptions.Controls.Add(_completeFromPhotosBufferIncludeAll);
            completeFromPhotosOptions.Controls.Add(_completeFromPhotosBufferExcludeOutside);
            completeFromPhotosOptions.Controls.Add(_completeFromPhotosIncludeQuarterLinework);

            var completeFromPhotosOptionsGroup = new GroupBox
            {
                Text = "Complete From Photos Buffer Options",
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            completeFromPhotosOptionsGroup.Controls.Add(completeFromPhotosOptions);

            var photoToTextCheckButton = new Button
            {
                Text = "PHOTO TO TEXT CHECK",
                Dock = DockStyle.Top,
                Height = 28
            };
            photoToTextCheckButton.Click += (_, __) => RunPhotoToTextCheck();

            var removePointButton = new Button
            {
                Text = "Remove Point",
                Dock = DockStyle.Top,
                Height = 28
            };
            removePointButton.Click += (_, __) => RunRemovePoint();
            _toolTip.SetToolTip(
                removePointButton,
                "Remove numbered WLS point blocks, renumber the remaining blocks, and optionally rebuild a selected summary table.");

            var exportTableWorkbookButton = new Button
            {
                Text = "Export Table Workbook",
                Dock = DockStyle.Top,
                Height = 28
            };
            exportTableWorkbookButton.Click += (_, __) => RunExportTableWorkbook();
            _toolTip.SetToolTip(
                exportTableWorkbookButton,
                "Select an existing WLS summary table and export a fresh workbook from the current table values.");

            var matchTableToPhotosButton = new Button
            {
                Text = "Match Table to Photos",
                Dock = DockStyle.Top,
                Height = 28
            };
            matchTableToPhotosButton.Click += (_, __) => RunMatchTableToPhotos();
            _toolTip.SetToolTip(
                matchTableToPhotosButton,
                "Select a WLS summary table and update matching PHOTO labels to use the table wording.");

            var enviroSweepGroup = new GroupBox
            {
                Text = "ENVIRO SWEEP",
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };

            var enviroSweepLayout = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };

            var importKmzKmlButton = new Button
            {
                Text = "IMPORT KMZ/KML",
                Height = 28,
                AutoSize = true
            };
            importKmzKmlButton.Click += (_, __) => RunImportKmzKml();
            _toolTip.SetToolTip(importKmzKmlButton, "Imports KMZ/KML and auto-maps attributes to Object Data in the current drawing projection.");

            enviroSweepLayout.Controls.Add(importKmzKmlButton);
            enviroSweepGroup.Controls.Add(enviroSweepLayout);
            var titleBlockControlsHost = BuildTitleBlockControlsHost();

            Controls.Add(enviroSweepGroup);
            Controls.Add(matchTableToPhotosButton);
            Controls.Add(exportTableWorkbookButton);
            Controls.Add(removePointButton);
            Controls.Add(photoToTextCheckButton);
            Controls.Add(completeFromPhotosOptionsGroup);
            Controls.Add(sortBufferPhotosButton);
            Controls.Add(completeFromPhotosButton);
            Controls.Add(photoGroup);
            Controls.Add(titleBlockControlsHost);
            Controls.Add(_statusStrip);

            ApplyCompleteFromPhotosBufferMode(_settings);
            ApplyTooltips();
        }

        private void AddRow(TableLayoutPanel panel, string label, Control control)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, panel.RowCount - 1);
            panel.Controls.Add(control, 1, panel.RowCount - 1);
            panel.RowCount++;
        }

        private void AddRow(TableLayoutPanel panel, string label, TextBox textBox, object value)
        {
            textBox.Text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            AddRow(panel, label, textBox);
        }

        private Control BuildTitleBlockControlsHost()
        {
            _titleBlockControlsToggle.Text = "TITLE BLOCK CONTROLs";
            _titleBlockControlsToggle.Dock = DockStyle.Top;
            _titleBlockControlsToggle.Height = 28;
            _titleBlockControlsToggle.Click += (_, __) => _titleBlockControlsPanel.Visible = !_titleBlockControlsPanel.Visible;

            _titleBlockControlsPanel.Dock = DockStyle.Top;
            _titleBlockControlsPanel.AutoSize = true;
            _titleBlockControlsPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _titleBlockControlsPanel.BorderStyle = BorderStyle.FixedSingle;
            _titleBlockControlsPanel.Padding = new Padding(8);
            _titleBlockControlsPanel.Visible = false;
            _titleBlockControlsPanel.Controls.Add(BuildTitleBlockControlsContent());

            var host = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            host.Controls.Add(_titleBlockControlsPanel);
            host.Controls.Add(_titleBlockControlsToggle);

            LoadTitleBlockInput(_lastAppliedTitleBlockInput);
            return host;
        }

        private Control BuildTitleBlockControlsContent()
        {
            ConfigureTitleBlockComboBox(
                _titleBlockSweepType,
                "PRE-CONSTRUCTION",
                "POST-CONSTRUCTION");
            ConfigureTitleBlockComboBox(
                _titleBlockPurpose,
                "PIPELINE RIGHT-OF-WAY",
                "PIPELINE RIGHT-OF-WAY  & TEMPORARY AREAS",
                "PAD SITE",
                "PAD SITE & ACCESS ROAD",
                "PAD SITE & TEMPORARY AREAS",
                "PAD SITE, ACCESS ROAD & TEMPORARY AREAS",
                "TEMPORARY AREAS");

            _titleBlockLocation.Multiline = true;
            _titleBlockLocation.Height = 54;
            _titleBlockLocation.Width = 260;
            _titleBlockAddSectionsFromBoundary.Text = "ADD SECTIONS FROM BDY";
            _titleBlockAddSectionsFromBoundary.AutoSize = true;
            _titleBlockAddSectionsFromBoundary.Click += (_, __) => RunAddTitleBlockSectionsFromBoundary();
            _titleBlockSubRegion.Multiline = true;
            _titleBlockSubRegion.Height = 40;
            _titleBlockSubRegion.Width = 260;
            _titleBlockAddSubRegionFromFootprint.Text = "GET SUB-REGION FROM FOOTPRINT";
            _titleBlockAddSubRegionFromFootprint.AutoSize = true;
            _titleBlockAddSubRegionFromFootprint.Click += (_, __) => RunAddTitleBlockSubRegionFromFootprint();
            _titleBlockUpdateFindingStatement.Text = "Upd. Finding Statement";
            _titleBlockUpdateFindingStatement.AutoSize = true;
            _titleBlockUpdateFindingStatement.Click += (_, __) => RunUpdateFindingStatement();

            _titleBlockSurveyCalendar.MaxSelectionCount = 31;
            _titleBlockAddSurveyDates.Text = "ADD SELECTED DATE(S)";
            _titleBlockAddSurveyDates.AutoSize = true;
            _titleBlockAddSurveyDates.Click += (_, __) => AddSelectedSurveyDates();
            _titleBlockRemoveSurveyDates.Text = "REMOVE SELECTED";
            _titleBlockRemoveSurveyDates.AutoSize = true;
            _titleBlockRemoveSurveyDates.Click += (_, __) => RemoveSelectedSurveyDates();
            _titleBlockSurveyDates.Height = 90;
            _titleBlockSurveyDates.Width = 260;
            _titleBlockSurveyDates.SelectionMode = System.Windows.Forms.SelectionMode.MultiExtended;

            _titleBlockMethodologySpacing.Width = 120;

            var content = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 0,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            AddStackRow(content, BuildLabeledPanel("1. TYPE OF SWEEP", _titleBlockSweepType));
            AddStackRow(content, BuildLabeledPanel("2. PURPOSE", _titleBlockPurpose));

            var locationPanel = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 0,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 8)
            };
            locationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            AddStackRow(locationPanel, new Label { Text = "3. LOCATION", AutoSize = true });
            AddStackRow(locationPanel, _titleBlockLocation);
            AddStackRow(locationPanel, _titleBlockAddSectionsFromBoundary);
            AddStackRow(content, locationPanel);

            var surveyDatesPanel = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 0,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 8)
            };
            surveyDatesPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            AddStackRow(surveyDatesPanel, new Label { Text = "4. SURVEY DATES", AutoSize = true });
            AddStackRow(surveyDatesPanel, _titleBlockSurveyCalendar);
            var surveyDateButtons = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 4, 0, 0)
            };
            surveyDateButtons.Controls.Add(_titleBlockAddSurveyDates);
            surveyDateButtons.Controls.Add(_titleBlockRemoveSurveyDates);
            AddStackRow(surveyDatesPanel, surveyDateButtons);
            AddStackRow(surveyDatesPanel, _titleBlockSurveyDates);
            AddStackRow(content, surveyDatesPanel);

            var subRegionPanel = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 0,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 8)
            };
            subRegionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            AddStackRow(subRegionPanel, new Label { Text = "5. SUB REGIONS", AutoSize = true });
            AddStackRow(subRegionPanel, _titleBlockSubRegion);
            AddStackRow(subRegionPanel, _titleBlockAddSubRegionFromFootprint);
            AddStackRow(content, subRegionPanel);
            AddStackRow(content, BuildExistingLandPanel());
            AddStackRow(content, BuildLabeledPanel("7. METHODOLOGY - SPACING (m)", _titleBlockMethodologySpacing));

            var actionsPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 0)
            };
            var applyButton = new Button
            {
                Text = "Apply",
                AutoSize = true
            };
            applyButton.Click += (_, __) => RunApplyTitleBlockControls();
            var cancelButton = new Button
            {
                Text = "Cancel",
                AutoSize = true
            };
            cancelButton.Click += (_, __) => LoadTitleBlockInput(_lastAppliedTitleBlockInput);
            actionsPanel.Controls.Add(_titleBlockUpdateFindingStatement);
            actionsPanel.Controls.Add(applyButton);
            actionsPanel.Controls.Add(cancelButton);
            AddStackRow(content, actionsPanel);

            return content;
        }

        private Control BuildExistingLandPanel()
        {
            var container = new GroupBox
            {
                Text = "6. EXISTING LAND",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 8)
            };

            var layout = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 0,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            AddStackRow(layout, new Label { Text = "Rural", AutoSize = true });
            AddStackRow(layout, BuildExistingLandGroup(
                "a. Existing Linear Infrastructure",
                _titleBlockExistingLinearInfrastructure,
                "Access Roads",
                "Pipeline Right-of-ways",
                "Buried Pipelines",
                "Vegetation Control",
                "Power Lines and R/Ws"));
            AddStackRow(layout, BuildExistingLandGroup(
                "b. Existing Leases",
                _titleBlockExistingLeases,
                "Riser Sites",
                "Pad Sites",
                "Battery Site",
                "Valve Sites",
                "Compressor Sites"));
            AddStackRow(layout, BuildExistingLandGroup(
                "c. Other",
                _titleBlockExistingLandOther,
                "Residences / Residential Acreage",
                "Cultivated Cropland"));
            container.Controls.Add(layout);
            return container;
        }

        private Control BuildExistingLandGroup(string title, ICollection<CheckBox> target, params string[] options)
        {
            var container = new GroupBox
            {
                Text = title,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 4, 0, 0)
            };

            var layout = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Dock = DockStyle.Top
            };

            foreach (var option in options)
            {
                var checkBox = new CheckBox
                {
                    Text = option,
                    AutoSize = true
                };
                target.Add(checkBox);
                layout.Controls.Add(checkBox);
            }

            container.Controls.Add(layout);
            return container;
        }

        private static Control BuildLabeledPanel(string labelText, Control control)
        {
            control.Margin = new Padding(0, 4, 0, 0);

            var panel = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 0,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 8)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            AddStackRow(panel, new Label { Text = labelText, AutoSize = true });
            AddStackRow(panel, control);
            return panel;
        }

        private static void AddStackRow(TableLayoutPanel panel, Control control)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(control, 0, panel.RowCount);
            panel.RowCount++;
        }

        private static void ConfigureTitleBlockComboBox(ComboBox comboBox, params string[] options)
        {
            comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            comboBox.Width = 260;
            comboBox.Items.Clear();
            comboBox.Items.AddRange(options);
            comboBox.SelectedIndex = -1;
        }

        private static TitleBlockControlInput CreateDefaultTitleBlockControlInput()
        {
            return new TitleBlockControlInput
            {
                SurveyDates = new List<DateTime>(),
                ExistingLinearInfrastructure = new List<string>(),
                ExistingLeases = new List<string>(),
                ExistingLandOther = new List<string>()
            };
        }

        private void LoadTitleBlockInput(TitleBlockControlInput input)
        {
            var safeInput = input ?? CreateDefaultTitleBlockControlInput();
            _titleBlockSweepType.SelectedItem = string.IsNullOrWhiteSpace(safeInput.SweepType) ? null : safeInput.SweepType;
            _titleBlockPurpose.SelectedItem = string.IsNullOrWhiteSpace(safeInput.Purpose) ? null : safeInput.Purpose;
            _titleBlockLocation.Text = safeInput.Location ?? string.Empty;
            _titleBlockSubRegion.Text = safeInput.SubRegion ?? string.Empty;
            _titleBlockMethodologySpacing.Text = safeInput.MethodologySpacingMeters ?? string.Empty;
            SetCheckedValues(_titleBlockExistingLinearInfrastructure, safeInput.ExistingLinearInfrastructure);
            SetCheckedValues(_titleBlockExistingLeases, safeInput.ExistingLeases);
            SetCheckedValues(_titleBlockExistingLandOther, safeInput.ExistingLandOther);
            RefreshSurveyDateList(safeInput.SurveyDates);
            ClearValidation();
        }

        private TitleBlockControlInput BuildTitleBlockControlInput()
        {
            return new TitleBlockControlInput
            {
                SweepType = _titleBlockSweepType.SelectedItem as string,
                Purpose = _titleBlockPurpose.SelectedItem as string,
                Location = _titleBlockLocation.Text.Trim(),
                SurveyDates = GetSurveyDatesFromList(),
                SubRegion = _titleBlockSubRegion.Text.Trim(),
                ExistingLinearInfrastructure = GetCheckedValues(_titleBlockExistingLinearInfrastructure),
                ExistingLeases = GetCheckedValues(_titleBlockExistingLeases),
                ExistingLandOther = GetCheckedValues(_titleBlockExistingLandOther),
                MethodologySpacingMeters = _titleBlockMethodologySpacing.Text.Trim()
            };
        }

        private List<DateTime> GetSurveyDatesFromList()
        {
            var dates = new List<DateTime>();
            foreach (var item in _titleBlockSurveyDates.Items)
            {
                if (item is SurveyDateListItem dateItem)
                {
                    dates.Add(dateItem.Date);
                }
            }

            return dates;
        }

        private static List<string> GetCheckedValues(IEnumerable<CheckBox> checkBoxes)
        {
            var values = new List<string>();
            foreach (var checkBox in checkBoxes)
            {
                if (checkBox.Checked)
                {
                    values.Add(checkBox.Text);
                }
            }

            return values;
        }

        private static void SetCheckedValues(IEnumerable<CheckBox> checkBoxes, IEnumerable<string>? selectedValues)
        {
            var lookup = new HashSet<string>(selectedValues ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var checkBox in checkBoxes)
            {
                checkBox.Checked = lookup.Contains(checkBox.Text);
            }
        }

        private void RefreshSurveyDateList(IEnumerable<DateTime>? dates)
        {
            _titleBlockSurveyDates.BeginUpdate();
            _titleBlockSurveyDates.Items.Clear();
            if (dates != null)
            {
                foreach (var date in new SortedSet<DateTime>(dates))
                {
                    _titleBlockSurveyDates.Items.Add(new SurveyDateListItem(date.Date));
                }
            }

            _titleBlockSurveyDates.EndUpdate();
        }

        private static TitleBlockControlInput CloneTitleBlockControlInput(TitleBlockControlInput input)
        {
            return new TitleBlockControlInput
            {
                SweepType = input.SweepType,
                Purpose = input.Purpose,
                Location = input.Location,
                SurveyDates = input.SurveyDates == null ? new List<DateTime>() : new List<DateTime>(input.SurveyDates),
                SubRegion = input.SubRegion,
                ExistingLinearInfrastructure = input.ExistingLinearInfrastructure == null ? new List<string>() : new List<string>(input.ExistingLinearInfrastructure),
                ExistingLeases = input.ExistingLeases == null ? new List<string>() : new List<string>(input.ExistingLeases),
                ExistingLandOther = input.ExistingLandOther == null ? new List<string>() : new List<string>(input.ExistingLandOther),
                MethodologySpacingMeters = input.MethodologySpacingMeters
            };
        }

        private bool TryUpdateSettings(Editor editor)
        {
            ClearValidation();
            _settings.CompleteFromPhotosIncludeBufferExcludeOutside = _completeFromPhotosBufferExcludeOutside.Checked;
            _settings.CompleteFromPhotosIncludeBufferIncludeAll = _completeFromPhotosBufferIncludeAll.Checked;
            _settings.CompleteFromPhotosIncludeQuarterLinework = _completeFromPhotosIncludeQuarterLinework.Checked;
            return TryParseInt(editor, _photoStartNumber, value => _settings.PhotoStartNumber = value);
        }

        private bool TryParseInt(Editor editor, TextBox box, Action<int> assign)
        {
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                assign(value);
                SetValidation(box, true, string.Empty);
                return true;
            }

            SetValidation(box, false, "Invalid integer");
            editor.WriteMessage($"\nInvalid integer: {box.Text}");
            return false;
        }

        private void RunCompleteFromPhotos()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            if (!TryUpdateSettings(doc.Editor))
            {
                return;
            }

            SetStatus("Running Complete From Photos...");
            var service = new CompleteFromPhotosService();
            service.Execute(doc, doc.Editor, _settings.Clone());
            SetStatus("Complete From Photos finished.");
        }

        private void RunPhotoToTextCheck()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            if (!TryUpdateSettings(doc.Editor))
            {
                return;
            }

            SetStatus("Running Photo To Text Check...");
            var service = new PhotoToTextCheckService();
            service.Execute(doc, doc.Editor, _settings.Clone());
            SetStatus("Photo To Text Check finished.");
        }

        private void RunRemovePoint()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            SetStatus("Removing WLS point(s)...");
            var service = new CompleteFromPhotosService();
            service.RemovePoints(doc, doc.Editor, _settings.Clone());
            SetStatus("Remove Point finished.");
        }

        private void RunExportTableWorkbook()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            SetStatus("Exporting workbook from table...");
            var service = new CompleteFromPhotosService();
            service.ExportWorkbookFromTable(doc, doc.Editor);
            SetStatus("Export Table Workbook finished.");
        }

        private void RunMatchTableToPhotos()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            SetStatus("Matching table wording to photo labels...");
            var service = new MatchTableToPhotosService();
            service.Execute(doc, doc.Editor);
            SetStatus("Match Table to Photos finished.");
        }

        private void RunSortBufferPhotos()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            if (!TryUpdateSettings(doc.Editor))
            {
                return;
            }

            SetStatus("Running SORT 100m buffer Photos...");
            var service = new SortBufferPhotosService();
            service.Execute(doc, doc.Editor, _settings.Clone());
            SetStatus("SORT 100m buffer Photos finished.");
        }

        private void RunImportKmzKml()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            SetStatus("Running KMZ/KML import...");
            var service = new ImportKmzKmlService();
            service.Execute(doc, doc.Editor);
            SetStatus("KMZ/KML import finished.");
        }

        private void RunAddTitleBlockSectionsFromBoundary()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            SetStatus("Collecting ATS sections from boundary...");
            var hostHandle = FindForm()?.Handle ?? Handle;
            if (_titleBlockControlService.TryCollectLocationText(
                    doc,
                    doc.Editor,
                    _settings.UtmZone,
                    hostHandle,
                    out var locationText,
                    out var message))
            {
                _titleBlockLocation.Text = locationText;
                SetStatus(message);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    doc.Editor.WriteMessage($"\n{message}");
                }

                return;
            }

            SetStatus(string.IsNullOrWhiteSpace(message) ? "Unable to collect ATS sections from the boundary." : message);
            if (!string.IsNullOrWhiteSpace(message))
            {
                doc.Editor.WriteMessage($"\n{message}");
            }
        }

        private void RunAddTitleBlockSubRegionFromFootprint()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            SetStatus("Collecting natural sub-region from footprint...");
            var hostHandle = FindForm()?.Handle ?? Handle;
            if (_naturalRegionLookupService.TryCollectSubRegionText(
                    doc,
                    doc.Editor,
                    hostHandle,
                    out var subRegionText,
                    out var keptLinework,
                    out var message))
            {
                _titleBlockSubRegion.Text = subRegionText;
                SetStatus(message);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    doc.Editor.WriteMessage($"\n{message}");
                }

                if (keptLinework)
                {
                    doc.Editor.WriteMessage("\nNatural-region proof linework was left visible for review.");
                }

                return;
            }

            SetStatus(string.IsNullOrWhiteSpace(message) ? "Unable to collect natural sub-region from the selected footprint." : message);
            if (!string.IsNullOrWhiteSpace(message))
            {
                doc.Editor.WriteMessage($"\n{message}");
            }
        }

        private void AddSelectedSurveyDates()
        {
            var dates = new SortedSet<DateTime>(GetSurveyDatesFromList());
            var start = _titleBlockSurveyCalendar.SelectionStart.Date;
            var end = _titleBlockSurveyCalendar.SelectionEnd.Date;
            for (var date = start; date <= end; date = date.AddDays(1))
            {
                dates.Add(date);
            }

            RefreshSurveyDateList(dates);
        }

        private void RemoveSelectedSurveyDates()
        {
            if (_titleBlockSurveyDates.SelectedItems.Count == 0)
            {
                return;
            }

            var dates = new SortedSet<DateTime>(GetSurveyDatesFromList());
            foreach (var selectedItem in _titleBlockSurveyDates.SelectedItems)
            {
                if (selectedItem is SurveyDateListItem dateItem)
                {
                    dates.Remove(dateItem.Date);
                }
            }

            RefreshSurveyDateList(dates);
        }

        private void RunApplyTitleBlockControls()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            ClearValidation();
            var input = BuildTitleBlockControlInput();
            SetStatus("Applying title block controls...");
            if (_titleBlockControlService.Apply(doc, doc.Editor, input, out var message))
            {
                _lastAppliedTitleBlockInput = CloneTitleBlockControlInput(input);
                SetStatus(message);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    doc.Editor.WriteMessage($"\n{message}");
                }

                return;
            }

            SetStatus(string.IsNullOrWhiteSpace(message) ? "Unable to apply title block controls." : message);
            if (!string.IsNullOrWhiteSpace(message))
            {
                doc.Editor.WriteMessage($"\n{message}");
            }
        }

        private void RunUpdateFindingStatement()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            ClearValidation();
            SetStatus("Updating findings statement...");
            if (_titleBlockControlService.UpdateFindingStatement(doc, doc.Editor, out var message))
            {
                SetStatus(message);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    doc.Editor.WriteMessage($"\n{message}");
                }

                return;
            }

            SetStatus(string.IsNullOrWhiteSpace(message) ? "Unable to update the findings statement." : message);
            if (!string.IsNullOrWhiteSpace(message))
            {
                doc.Editor.WriteMessage($"\n{message}");
            }
        }

        private void ApplyTooltips()
        {
            _toolTip.SetToolTip(_photoStartNumber, "Starting number for photo blocks and photo layout.");
            _toolTip.SetToolTip(
                _completeFromPhotosBufferIncludeAll,
                "Prompts for PROPOSED and 100m boundaries, then selects separate blocks for PROPOSED, 100m-only, and OUTSIDE findings.");
            _toolTip.SetToolTip(
                _completeFromPhotosBufferExcludeOutside,
                "Prompts for PROPOSED and 100m boundaries plus one block for each area; PROPOSED takes priority to avoid duplicates.");
            _toolTip.SetToolTip(
                _completeFromPhotosIncludeQuarterLinework,
                "Draws matched ATS quarter polygons on layer L-QUATER so quarter assignment can be visually checked.");
            _toolTip.SetToolTip(
                _titleBlockControlsToggle,
                "Show or hide the WLS title block controls.");
            _toolTip.SetToolTip(
                _titleBlockAddSectionsFromBoundary,
                "Select one or more closed boundaries and fill the location field using ATS quarter sections.");
            _toolTip.SetToolTip(
                _titleBlockAddSubRegionFromFootprint,
                "Select the proposed footprint and fill the sub-region text from the Alberta natural sub-region shapefile.");
            _toolTip.SetToolTip(
                _titleBlockUpdateFindingStatement,
                "Select a findings table, answer the key-feature prompts, and update the page-1 findings statement in blue.");
        }

        private void ApplyCompleteFromPhotosBufferMode(PluginSettings settings)
        {
            if (settings.CompleteFromPhotosIncludeBufferIncludeAll)
            {
                _completeFromPhotosBufferIncludeAll.Checked = true;
                _completeFromPhotosBufferExcludeOutside.Checked = false;
            }
            else if (settings.CompleteFromPhotosIncludeBufferExcludeOutside)
            {
                _completeFromPhotosBufferExcludeOutside.Checked = true;
                _completeFromPhotosBufferIncludeAll.Checked = false;
            }
            else
            {
                _completeFromPhotosBufferExcludeOutside.Checked = false;
                _completeFromPhotosBufferIncludeAll.Checked = false;
            }

            _completeFromPhotosIncludeQuarterLinework.Checked = settings.CompleteFromPhotosIncludeQuarterLinework;
        }

        private void SetStatus(string message)
        {
            _statusLabel.Text = message;
        }

        private void SetValidation(TextBox box, bool isValid, string message)
        {
            _errorProvider.SetError(box, message);
            box.BackColor = isValid ? Color.White : Color.MistyRose;
        }

        private void ClearValidation()
        {
            _errorProvider.Clear();
            SetValidation(_photoStartNumber, true, string.Empty);
        }

        private sealed class SurveyDateListItem
        {
            public SurveyDateListItem(DateTime date)
            {
                Date = date.Date;
            }

            public DateTime Date { get; }

            public override string ToString()
            {
                return TitleBlockControlService.FormatSurveyDates(new[] { Date });
            }
        }
    }
}
