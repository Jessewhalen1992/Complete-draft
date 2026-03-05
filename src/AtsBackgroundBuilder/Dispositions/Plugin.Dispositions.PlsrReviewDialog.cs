using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using AtsBackgroundBuilder.Core;
using Autodesk.AutoCAD.ApplicationServices;
using WinForms = System.Windows.Forms;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        private sealed class PlsrReviewRow
        {
            public Guid IssueId { get; set; }
            public string Decision { get; set; } = "Ignore";
            public string Type { get; set; } = string.Empty;
            public string Quarter { get; set; } = string.Empty;
            public string DispNum { get; set; } = string.Empty;
            public string VerDate { get; set; } = string.Empty;
            public string Current { get; set; } = string.Empty;
            public string Expected { get; set; } = string.Empty;
            public string Action { get; set; } = string.Empty;
            public string Detail { get; set; } = string.Empty;
            public bool Actionable { get; set; }
        }

        private static HashSet<Guid> ShowPlsrReviewDialog(
            List<PlsrCheckIssue> issues,
            Logger logger)
        {
            var accepted = new HashSet<Guid>();
            if (issues == null || issues.Count == 0)
            {
                return accepted;
            }

            try
            {
                var rows = new BindingList<PlsrReviewRow>(
                    issues.Select(issue => new PlsrReviewRow
                    {
                        IssueId = issue.Id,
                        Decision = "Ignore",
                        Type = issue.Type ?? string.Empty,
                        Quarter = issue.QuarterKey ?? string.Empty,
                        DispNum = issue.DispNum ?? string.Empty,
                        VerDate = issue.VersionDateStatus ?? "N/A",
                        Current = issue.CurrentValue ?? string.Empty,
                        Expected = issue.ExpectedValue ?? string.Empty,
                        Action = DescribeIssueAction(issue.ChangeType),
                        Detail = issue.Detail ?? string.Empty,
                        Actionable = issue.IsActionable
                    }).ToList());

                using (var form = new WinForms.Form())
                using (var grid = new WinForms.DataGridView())
                using (var buttonPanel = new WinForms.FlowLayoutPanel())
                using (var acceptAllButton = new WinForms.Button())
                using (var ignoreAllButton = new WinForms.Button())
                using (var applyButton = new WinForms.Button())
                using (var cancelButton = new WinForms.Button())
                using (var topLabel = new WinForms.Label())
                {
                    var applyRequested = false;
                    form.Text = "PLSR Review";
                    form.StartPosition = WinForms.FormStartPosition.CenterScreen;
                    form.Width = 1400;
                    form.Height = 760;
                    form.MinimizeBox = false;
                    form.MaximizeBox = true;

                    topLabel.AutoSize = false;
                    topLabel.Dock = WinForms.DockStyle.Top;
                    topLabel.Height = 44;
                    topLabel.Padding = new WinForms.Padding(10, 10, 10, 10);
                    topLabel.Text = "Review PLSR results. Set each row to Accept or Ignore. Pan/zoom model space if needed, then click Apply Decisions.";

                    grid.Dock = WinForms.DockStyle.Fill;
                    grid.AutoGenerateColumns = false;
                    grid.AllowUserToAddRows = false;
                    grid.AllowUserToDeleteRows = false;
                    grid.AllowUserToResizeRows = false;
                    grid.MultiSelect = false;
                    grid.SelectionMode = WinForms.DataGridViewSelectionMode.FullRowSelect;
                    grid.RowHeadersVisible = false;
                    grid.EditMode = WinForms.DataGridViewEditMode.EditOnEnter;
                    grid.DataSource = rows;

                    var decisionColumn = new WinForms.DataGridViewComboBoxColumn
                    {
                        DataPropertyName = nameof(PlsrReviewRow.Decision),
                        HeaderText = "Decision",
                        Name = "Decision",
                        Width = 90,
                        FlatStyle = WinForms.FlatStyle.Flat
                    };
                    decisionColumn.Items.Add("Accept");
                    decisionColumn.Items.Add("Ignore");

                    grid.Columns.Add(decisionColumn);
                    grid.Columns.Add(new WinForms.DataGridViewTextBoxColumn { DataPropertyName = nameof(PlsrReviewRow.Type), HeaderText = "Result", ReadOnly = true, Width = 130 });
                    grid.Columns.Add(new WinForms.DataGridViewTextBoxColumn { DataPropertyName = nameof(PlsrReviewRow.Quarter), HeaderText = "Quarter", ReadOnly = true, Width = 170 });
                    grid.Columns.Add(new WinForms.DataGridViewTextBoxColumn { DataPropertyName = nameof(PlsrReviewRow.DispNum), HeaderText = "Disp", ReadOnly = true, Width = 120 });
                    grid.Columns.Add(new WinForms.DataGridViewTextBoxColumn { DataPropertyName = nameof(PlsrReviewRow.VerDate), HeaderText = "Ver. Date.", ReadOnly = true, Width = 90 });
                    grid.Columns.Add(new WinForms.DataGridViewTextBoxColumn { DataPropertyName = nameof(PlsrReviewRow.Action), HeaderText = "Action", ReadOnly = true, Width = 160 });
                    grid.Columns.Add(new WinForms.DataGridViewTextBoxColumn { DataPropertyName = nameof(PlsrReviewRow.Current), HeaderText = "Current", ReadOnly = true, Width = 230 });
                    grid.Columns.Add(new WinForms.DataGridViewTextBoxColumn { DataPropertyName = nameof(PlsrReviewRow.Expected), HeaderText = "Expected", ReadOnly = true, Width = 230 });
                    grid.Columns.Add(new WinForms.DataGridViewTextBoxColumn { DataPropertyName = nameof(PlsrReviewRow.Detail), HeaderText = "Detail", ReadOnly = true, AutoSizeMode = WinForms.DataGridViewAutoSizeColumnMode.Fill });

                    buttonPanel.Dock = WinForms.DockStyle.Bottom;
                    buttonPanel.Height = 52;
                    buttonPanel.FlowDirection = WinForms.FlowDirection.RightToLeft;
                    buttonPanel.Padding = new WinForms.Padding(8);

                    applyButton.Text = "Apply Decisions";
                    applyButton.Width = 130;
                    applyButton.Click += (_, __) =>
                    {
                        grid.EndEdit();
                        var bindingContext = form.BindingContext;
                        if (bindingContext != null && bindingContext[rows] is WinForms.CurrencyManager manager)
                        {
                            manager.EndCurrentEdit();
                        }

                        applyRequested = true;
                        form.Close();
                    };

                    cancelButton.Text = "Cancel";
                    cancelButton.Width = 90;
                    cancelButton.Click += (_, __) =>
                    {
                        applyRequested = false;
                        form.Close();
                    };

                    acceptAllButton.Text = "Accept All";
                    acceptAllButton.Width = 100;
                    acceptAllButton.Click += (_, __) =>
                    {
                        foreach (var row in rows)
                        {
                            row.Decision = "Accept";
                        }

                        grid.Refresh();
                    };

                    ignoreAllButton.Text = "Ignore All";
                    ignoreAllButton.Width = 100;
                    ignoreAllButton.Click += (_, __) =>
                    {
                        foreach (var row in rows)
                        {
                            row.Decision = "Ignore";
                        }

                        grid.Refresh();
                    };

                    buttonPanel.Controls.Add(applyButton);
                    buttonPanel.Controls.Add(cancelButton);
                    buttonPanel.Controls.Add(ignoreAllButton);
                    buttonPanel.Controls.Add(acceptAllButton);

                    form.Controls.Add(grid);
                    form.Controls.Add(buttonPanel);
                    form.Controls.Add(topLabel);

                    Application.ShowModelessDialog(form);
                    while (form.Visible)
                    {
                        WinForms.Application.DoEvents();
                        System.Threading.Thread.Sleep(25);
                    }

                    var acceptedFromReview = ReviewDecisionService.ResolveAcceptedIssueIds(
                        applyRequested,
                        rows
                            .Where(row => row != null)
                            .Select(row => new ReviewDecisionEntry(row.IssueId, row.Decision)));
                    accepted.UnionWith(acceptedFromReview);
                }
            }
            catch (System.Exception ex)
            {
                logger.WriteLine("PLSR review window failed: " + ex.Message);
            }

            return accepted;
        }

        private static string DescribeIssueAction(PlsrIssueChangeType changeType)
        {
            return changeType switch
            {
                PlsrIssueChangeType.UpdateOwner => "Update label owner",
                PlsrIssueChangeType.TagExpired => "Add (Expired)",
                PlsrIssueChangeType.CreateMissingLabel => "Create missing label",
                PlsrIssueChangeType.CreateMissingLabelFromTemplate => "Create missing label (template)",
                PlsrIssueChangeType.CreateMissingLabelFromXml => "Create missing label (XML fallback)",
                _ => "No change"
            };
        }
    }
}
