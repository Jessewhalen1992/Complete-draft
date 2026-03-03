using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WildlifeSweeps
{
    internal sealed class UnmappedFindingDialog : Form
    {
        private const string OtherOption = "Other...";
        private const string OtherValue = "Other";
        private readonly ComboBox _species;
        private readonly ComboBox _findingType;
        private readonly ComboBox _standardDescription;
        private readonly CheckBox _rememberMapping;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _findingTypesBySpecies;

        public UnmappedFindingDialog(
            string cleanedText,
            IReadOnlyList<string> speciesOptions,
            IReadOnlyDictionary<string, IReadOnlyList<string>> findingTypesBySpecies,
            IReadOnlyList<string> standardDescriptions)
        {
            _findingTypesBySpecies = findingTypesBySpecies;

            Text = "Unmapped Finding";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 300);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 8,
                Padding = new Padding(12),
                AutoSize = true
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var unmappedLabel = new Label
            {
                Text = "Unmapped text:",
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };

            var unmappedText = new TextBox
            {
                Text = cleanedText,
                ReadOnly = true,
                Multiline = true,
                Dock = DockStyle.Fill,
                Height = 60
            };

            var speciesLabel = new Label
            {
                Text = "Species:",
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };

            _species = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDown
            };
            _species.Items.Add(string.Empty);
            _species.Items.AddRange(speciesOptions.Cast<object>().ToArray());
            _species.Items.Add(OtherOption);
            _species.SelectedIndexChanged += (_, __) => HandleSpeciesSelection();

            var findingTypeLabel = new Label
            {
                Text = "Finding type:",
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };

            _findingType = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDown
            };
            _findingType.Items.Add(string.Empty);
            _findingType.Items.Add(OtherOption);
            _findingType.SelectedIndexChanged += (_, __) => HandleFindingTypeSelection();

            var descriptionLabel = new Label
            {
                Text = "Standard description:",
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };

            _standardDescription = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDown
            };
            _standardDescription.Items.Add(OtherValue);
            _standardDescription.Items.AddRange(standardDescriptions.Cast<object>().ToArray());
            _standardDescription.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _standardDescription.AutoCompleteSource = AutoCompleteSource.CustomSource;
            _standardDescription.AutoCompleteCustomSource = new AutoCompleteStringCollection();
            var autocompleteItems = standardDescriptions.Prepend(OtherValue).ToArray();
            _standardDescription.AutoCompleteCustomSource.AddRange(autocompleteItems);

            _rememberMapping = new CheckBox
            {
                Text = "Remember this mapping",
                Checked = true,
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };

            var okButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Width = 90
            };

            var skipButton = new Button
            {
                Text = "Skip",
                DialogResult = DialogResult.Cancel,
                Width = 90
            };

            var ignoreButton = new Button
            {
                Text = "Ignore",
                DialogResult = DialogResult.Ignore,
                Width = 90
            };

            buttonPanel.Controls.Add(okButton);
            buttonPanel.Controls.Add(ignoreButton);
            buttonPanel.Controls.Add(skipButton);

            layout.Controls.Add(unmappedLabel, 0, 0);
            layout.SetColumnSpan(unmappedLabel, 2);
            layout.Controls.Add(unmappedText, 0, 1);
            layout.SetColumnSpan(unmappedText, 2);
            layout.Controls.Add(speciesLabel, 0, 2);
            layout.Controls.Add(_species, 1, 2);
            layout.Controls.Add(findingTypeLabel, 0, 3);
            layout.Controls.Add(_findingType, 1, 3);
            layout.Controls.Add(descriptionLabel, 0, 4);
            layout.Controls.Add(_standardDescription, 1, 4);
            layout.Controls.Add(_rememberMapping, 1, 5);
            layout.Controls.Add(buttonPanel, 0, 6);
            layout.SetColumnSpan(buttonPanel, 2);

            Controls.Add(layout);

            AcceptButton = okButton;
            CancelButton = skipButton;
        }

        public string StandardizedDescription => _standardDescription.Text;

        public string SelectedSpecies => _species.Text;

        public string SelectedFindingType => _findingType.Text;

        public bool RememberMapping => _rememberMapping.Checked;

        private void UpdateFindingTypes()
        {
            var species = SelectedSpecies;
            _findingType.Items.Clear();
            _findingType.Items.Add(string.Empty);
            if (!string.IsNullOrWhiteSpace(species)
                && _findingTypesBySpecies.TryGetValue(species, out var types))
            {
                _findingType.Items.AddRange(types.Cast<object>().ToArray());
            }

            _findingType.Items.Add(OtherOption);
            _findingType.SelectedIndex = 0;
        }

        private void HandleSpeciesSelection()
        {
            if (string.Equals(SelectedSpecies, OtherOption, StringComparison.OrdinalIgnoreCase))
            {
                _species.Text = OtherValue;
            }

            UpdateFindingTypes();
        }

        private void HandleFindingTypeSelection()
        {
            if (string.Equals(SelectedFindingType, OtherOption, StringComparison.OrdinalIgnoreCase))
            {
                _findingType.Text = OtherValue;
            }
        }
    }
}
