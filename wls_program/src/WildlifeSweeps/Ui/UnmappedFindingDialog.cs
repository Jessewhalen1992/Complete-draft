using System;
using System.Drawing;
using System.Windows.Forms;

namespace WildlifeSweeps
{
    internal sealed class UnmappedFindingDialog : Form
    {
        private readonly TextBox _replacementText;

        public UnmappedFindingDialog(string cleanedText)
        {
            Text = "Unmapped Finding";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 220);

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

            var foundLabel = new Label
            {
                Text = "Found text:",
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };

            var foundText = new TextBox
            {
                Text = cleanedText,
                ReadOnly = true,
                Multiline = true,
                Dock = DockStyle.Fill,
                Height = 70
            };

            var replacementLabel = new Label
            {
                Text = "Use this text:",
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };

            _replacementText = new TextBox
            {
                Text = cleanedText,
                Dock = DockStyle.Fill
            };
            _replacementText.SelectAll();

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

            layout.Controls.Add(foundLabel, 0, 0);
            layout.SetColumnSpan(foundLabel, 2);
            layout.Controls.Add(foundText, 0, 1);
            layout.SetColumnSpan(foundText, 2);
            layout.Controls.Add(replacementLabel, 0, 2);
            layout.Controls.Add(_replacementText, 1, 2);
            layout.Controls.Add(buttonPanel, 0, 3);
            layout.SetColumnSpan(buttonPanel, 2);

            Controls.Add(layout);

            AcceptButton = okButton;
            CancelButton = skipButton;
        }

        public string ReplacementText => _replacementText.Text.Trim();
    }
}
