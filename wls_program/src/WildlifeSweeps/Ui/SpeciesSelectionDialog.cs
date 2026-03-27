using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WildlifeSweeps
{
    internal sealed class WildlifeGroupSelectionDialog : Form
    {
        private readonly ComboBox _wildlifeGroupComboBox;

        public WildlifeGroupSelectionDialog(string findingDescription, IEnumerable<string> knownWildlifeGroups, string defaultWildlifeGroup)
        {
            Text = "Select Wildlife Group";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 230);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                Padding = new Padding(12),
                AutoSize = true
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var findingLabel = new Label
            {
                Text = "Finding:",
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };

            var findingText = new TextBox
            {
                Text = findingDescription,
                ReadOnly = true,
                Multiline = true,
                Dock = DockStyle.Fill,
                Height = 70
            };

            var wildlifeGroupLabel = new Label
            {
                Text = "Wildlife Group:",
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };

            _wildlifeGroupComboBox = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (var wildlifeGroup in knownWildlifeGroups
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Distinct(System.StringComparer.OrdinalIgnoreCase)
                         .OrderBy(value => value, System.StringComparer.OrdinalIgnoreCase))
            {
                _wildlifeGroupComboBox.Items.Add(wildlifeGroup);
            }

            if (!string.IsNullOrWhiteSpace(defaultWildlifeGroup))
            {
                _wildlifeGroupComboBox.SelectedItem = defaultWildlifeGroup.Trim();
            }

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

            var cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Width = 90
            };

            buttonPanel.Controls.Add(okButton);
            buttonPanel.Controls.Add(cancelButton);

            layout.Controls.Add(findingLabel, 0, 0);
            layout.SetColumnSpan(findingLabel, 2);
            layout.Controls.Add(findingText, 0, 1);
            layout.SetColumnSpan(findingText, 2);
            layout.Controls.Add(wildlifeGroupLabel, 0, 2);
            layout.Controls.Add(_wildlifeGroupComboBox, 1, 2);
            layout.Controls.Add(buttonPanel, 0, 3);
            layout.SetColumnSpan(buttonPanel, 2);

            Controls.Add(layout);

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        public string SelectedWildlifeGroup => _wildlifeGroupComboBox.Text.Trim();
    }
}
