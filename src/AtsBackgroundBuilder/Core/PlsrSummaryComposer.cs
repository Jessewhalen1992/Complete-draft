using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AtsBackgroundBuilder.Core
{
    internal sealed class PlsrSummaryComposeInput
    {
        public IEnumerable<string> IssueSummaryLines { get; set; } = Array.Empty<string>();
        public IEnumerable<string> NotIncludedPrefixes { get; set; } = Array.Empty<string>();
        public int MissingLabels { get; set; }
        public int OwnerMismatches { get; set; }
        public int ExtraLabels { get; set; }
        public int ExpiredCandidates { get; set; }
        public int MissingCreated { get; set; }
        public int SkippedTextOnlyFallbackLabels { get; set; }
        public int OwnerUpdated { get; set; }
        public int ExpiredTagged { get; set; }
        public int AcceptedActionable { get; set; }
        public int IgnoredActionable { get; set; }
        public int ApplyErrors { get; set; }
        public bool AllowTextOnlyFallbackLabels { get; set; }
        public IEnumerable<string> SkippedTextOnlyFallbackExamples { get; set; } = Array.Empty<string>();
    }

    internal sealed class PlsrSummaryComposeResult
    {
        public string SummaryText { get; set; } = string.Empty;
        public string WarningText { get; set; } = string.Empty;
        public bool ShouldShowWarning => !string.IsNullOrWhiteSpace(WarningText);
    }

    internal static class PlsrSummaryComposer
    {
        public static PlsrSummaryComposeResult Compose(PlsrSummaryComposeInput input)
        {
            var result = new PlsrSummaryComposeResult();
            var summary = new StringBuilder();
            summary.AppendLine("PLSR Check Summary");
            summary.AppendLine("-------------------");

            if (input.IssueSummaryLines != null)
            {
                foreach (var line in input.IssueSummaryLines)
                {
                    summary.AppendLine(line ?? string.Empty);
                }
            }

            var prefixes = (input.NotIncludedPrefixes ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (prefixes.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Not Included in check: " + string.Join(", ", prefixes));
            }

            summary.AppendLine();
            summary.AppendLine($"Missing labels: {input.MissingLabels}");
            summary.AppendLine($"Owner mismatches: {input.OwnerMismatches}");
            summary.AppendLine($"Extra labels not in PLSR: {input.ExtraLabels}");
            summary.AppendLine($"Expired candidates: {input.ExpiredCandidates}");
            summary.AppendLine($"Missing labels created: {input.MissingCreated}");
            summary.AppendLine($"Skipped text-only fallback labels: {input.SkippedTextOnlyFallbackLabels}");
            summary.AppendLine($"Owner updates applied: {input.OwnerUpdated}");
            summary.AppendLine($"Expired tags added: {input.ExpiredTagged}");
            summary.AppendLine($"Actionable results accepted: {input.AcceptedActionable}");
            summary.AppendLine($"Actionable results ignored: {input.IgnoredActionable}");
            summary.AppendLine($"Apply errors: {input.ApplyErrors}");
            result.SummaryText = summary.ToString().TrimEnd();

            if (input.SkippedTextOnlyFallbackLabels > 0 && !input.AllowTextOnlyFallbackLabels)
            {
                var warning = new StringBuilder();
                warning.AppendLine(
                    $"Skipped {input.SkippedTextOnlyFallbackLabels} label(s) where only text-only fallback was available (no source disposition geometry).");
                warning.AppendLine("These were intentionally not created to avoid floating MText labels.");

                var examples = (input.SkippedTextOnlyFallbackExamples ?? Array.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (examples.Count > 0)
                {
                    warning.AppendLine();
                    warning.AppendLine("Skipped labels:");
                    foreach (var example in examples)
                    {
                        warning.AppendLine(" - " + example);
                    }
                }

                result.WarningText = warning.ToString().TrimEnd();
            }

            return result;
        }
    }
}
