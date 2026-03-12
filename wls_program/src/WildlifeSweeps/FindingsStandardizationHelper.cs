using System;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;

namespace WildlifeSweeps
{
    internal static class FindingsStandardizationHelper
    {
        internal static FindingsDescriptionStandardizer.PromptResult PromptForUnmappedFinding(
            FindingsDescriptionStandardizer.PromptContext context,
            FindingsDescriptionStandardizer standardizer)
        {
            while (true)
            {
                using var dialog = new UnmappedFindingDialog(context.CleanedOriginal);
                var result = dialog.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.Ignore)
                {
                    return new FindingsDescriptionStandardizer.PromptResult(
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        true,
                        true,
                        false);
                }

                if (result == System.Windows.Forms.DialogResult.OK)
                {
                    var description = dialog.ReplacementText.Trim();
                    if (string.IsNullOrWhiteSpace(description))
                    {
                        System.Windows.Forms.MessageBox.Show(
                            "Provide replacement text or choose Skip/Ignore.",
                            "Missing Information",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Warning);
                        continue;
                    }

                    return new FindingsDescriptionStandardizer.PromptResult(
                        description,
                        string.Empty,
                        string.Empty,
                        true,
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
            builder.AppendLine("OriginalText\tCleanedOriginal\tStandardDescription\tPhotoRef\tSource");
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
    }
}
