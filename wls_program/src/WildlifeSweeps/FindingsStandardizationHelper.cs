using System;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;

namespace WildlifeSweeps
{
    internal static class FindingsStandardizationHelper
    {
        private const string OtherValue = "Other";

        internal static FindingsDescriptionStandardizer.PromptResult PromptForUnmappedFinding(
            FindingsDescriptionStandardizer.PromptContext context,
            FindingsDescriptionStandardizer standardizer)
        {
            while (true)
            {
                using var dialog = new UnmappedFindingDialog(
                    context.CleanedOriginal,
                    standardizer.SpeciesOptions,
                    standardizer.FindingTypesBySpecies,
                    standardizer.StandardDescriptionOptions);
                var result = dialog.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.Ignore)
                {
                    return new FindingsDescriptionStandardizer.PromptResult(
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        false,
                        true,
                        false);
                }

                if (result == System.Windows.Forms.DialogResult.OK)
                {
                    var description = NormalizeOtherValue(dialog.StandardizedDescription);
                    var species = NormalizeOtherValue(dialog.SelectedSpecies);
                    var findingType = NormalizeOtherValue(dialog.SelectedFindingType);

                    if (IsOtherValue(description))
                    {
                        if (string.IsNullOrWhiteSpace(species))
                        {
                            species = OtherValue;
                        }

                        if (string.IsNullOrWhiteSpace(findingType))
                        {
                            findingType = OtherValue;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(description)
                        && (string.IsNullOrWhiteSpace(species) || string.IsNullOrWhiteSpace(findingType)))
                    {
                        System.Windows.Forms.MessageBox.Show(
                            "Provide a species/finding type or a recognized standard description.",
                            "Missing Information",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Warning);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(species) || string.IsNullOrWhiteSpace(findingType))
                    {
                        if (!standardizer.TryResolveStandardDescription(description, out species, out findingType))
                        {
                            System.Windows.Forms.MessageBox.Show(
                                "That description is not recognized. Please choose a species and finding type.",
                                "Unknown Description",
                                System.Windows.Forms.MessageBoxButtons.OK,
                                System.Windows.Forms.MessageBoxIcon.Warning);
                            continue;
                        }
                    }

                    if (!standardizer.IsValidPair(species, findingType))
                    {
                        if (IsOtherValue(species) || IsOtherValue(findingType))
                        {
                            if (string.IsNullOrWhiteSpace(description))
                            {
                                description = OtherValue;
                            }

                            return new FindingsDescriptionStandardizer.PromptResult(
                                description,
                                species,
                                findingType,
                                dialog.RememberMapping,
                                false,
                                false);
                        }

                        var isKnownSpecies = standardizer.SpeciesOptions.Any(option =>
                            option.Equals(species, StringComparison.OrdinalIgnoreCase));
                        var isKnownFindingType = standardizer.FindingTypesBySpecies.Values.Any(list =>
                            list.Any(option => option.Equals(findingType, StringComparison.OrdinalIgnoreCase)));

                        if (isKnownSpecies && isKnownFindingType)
                        {
                            System.Windows.Forms.MessageBox.Show(
                                "That species/finding type combination is not valid.",
                                "Invalid Combination",
                                System.Windows.Forms.MessageBoxButtons.OK,
                                System.Windows.Forms.MessageBoxIcon.Warning);
                            continue;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(description))
                    {
                        if (IsOtherValue(species) || IsOtherValue(findingType))
                        {
                            description = OtherValue;
                        }
                        else
                        {
                            standardizer.TryGetDefaultDescriptionForPair(species, findingType, out description);
                        }
                    }

                    return new FindingsDescriptionStandardizer.PromptResult(
                        description,
                        species,
                        findingType,
                        dialog.RememberMapping,
                        false,
                        false);
                }

                return new FindingsDescriptionStandardizer.PromptResult(
                    context.CleanedOriginal,
                    string.Empty,
                    string.Empty,
                    false,
                    false,
                    true);
            }
        }

        internal static StringBuilder BuildLogHeader(Document doc)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Wildlife Sweeps Findings Standardization Log");
            builder.AppendLine($"Drawing: {doc.Name}");
            builder.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine("OriginalText\tCleanedOriginal\tSpecies\tFindingType\tStandardDescription\tPhotoRef\tSource");
            return builder;
        }

        internal static bool ShouldIncludeInLog(FindingsDescriptionStandardizer.StandardizationSource source)
        {
            return source != FindingsDescriptionStandardizer.StandardizationSource.RegexRule
                   && source != FindingsDescriptionStandardizer.StandardizationSource.KeywordRule
                   && source != FindingsDescriptionStandardizer.StandardizationSource.CustomMapping;
        }

        internal static bool TryAppendNonAutomaticLogEntry(
            StringBuilder logBuilder,
            string? originalText,
            FindingsDescriptionStandardizer.StandardizedFinding standardization)
        {
            if (!ShouldIncludeInLog(standardization.Source))
            {
                return false;
            }

            logBuilder.AppendLine(string.Join("\t",
                originalText ?? string.Empty,
                standardization.CleanedOriginal,
                standardization.Species,
                standardization.FindingType,
                standardization.StandardDescription,
                standardization.PhotoRef,
                standardization.Source));
            return true;
        }

        internal static string? WriteLogFile(Document doc, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            var drawingPath = doc.Database.Filename;
            var directory = !string.IsNullOrWhiteSpace(drawingPath)
                ? Path.GetDirectoryName(drawingPath)
                : null;
            var baseName = !string.IsNullOrWhiteSpace(drawingPath)
                ? Path.GetFileNameWithoutExtension(drawingPath)
                : "unsaved_drawing";
            var targetDirectory = string.IsNullOrWhiteSpace(directory)
                ? Environment.CurrentDirectory
                : directory;

            var logPath = Path.Combine(targetDirectory, $"{baseName}_finding_log.txt");
            File.WriteAllText(logPath, content);
            return logPath;
        }

        private static bool IsOtherValue(string? value)
        {
            return string.Equals(value, OtherValue, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeOtherValue(string? value)
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            var normalized = trimmed.TrimEnd('.');
            return string.Equals(normalized, OtherValue, StringComparison.OrdinalIgnoreCase)
                ? OtherValue
                : trimmed;
        }
    }
}
