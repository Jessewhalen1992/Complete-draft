using System;
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

            Controls.Add(enviroSweepGroup);
            Controls.Add(exportTableWorkbookButton);
            Controls.Add(removePointButton);
            Controls.Add(photoToTextCheckButton);
            Controls.Add(completeFromPhotosOptionsGroup);
            Controls.Add(sortBufferPhotosButton);
            Controls.Add(completeFromPhotosButton);
            Controls.Add(photoGroup);
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
    }
}
