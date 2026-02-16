/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        private static SectionDrawResult TryPromptAndBuildSections(Editor editor, Database database, Config config, Logger logger)
        {
            if (config.UseSectionIndex)
            {
                var requests = PromptForSectionRequests(editor);
                if (requests.Count > 0)
                {
                    var result = DrawSectionsFromRequests(editor, database, requests, config, logger, false);
                    if (result.QuarterPolylineIds.Count == 0)
                    {
                        var searchFolders = BuildSectionIndexSearchFolders(config);
                        var zones = new HashSet<int>();
                        foreach (var request in requests)
                        {
                            zones.Add(request.Key.Zone);
                        }

                        var zoneList = string.Join(", ", zones);
                        editor.WriteMessage(
                            $"\nNo section outlines found in index. " +
                            $"Verify the section index files for zone(s) {zoneList} exist in {string.Join("; ", searchFolders)} " +
                            "(Master_Sections.index_Z<zone>.jsonl/.csv or Master_Sections.index.jsonl/.csv). " +
                            "See AtsBackgroundBuilder.log for details.");
                    }

                    return result;
                }
            }

            editor.WriteMessage("\nSection input required.");
            return new SectionDrawResult(
                new List<ObjectId>(),
                new List<QuarterLabelInfo>(),
                new List<ObjectId>(),
                new List<ObjectId>(),
                new List<ObjectId>(),
                new List<ObjectId>(),
                new List<ObjectId>(),
                new Dictionary<ObjectId, int>(),
                false);
        }

        private static List<SectionRequest> PromptForSectionRequests(Editor editor)
        {
            var requests = new List<SectionRequest>();
            var zone = PromptForInt(editor, "Enter zone", 11, 1, 60);

            var addAnother = true;
            while (addAnother)
            {
                var quarter = PromptForQuarter(editor);
                if (quarter == QuarterSelection.None)
                {
                    break;
                }

                if (!TryPromptString(editor, "Enter section", out var section) ||
                    !TryPromptString(editor, "Enter township", out var township) ||
                    !TryPromptString(editor, "Enter range", out var range) ||
                    !TryPromptString(editor, "Enter meridian", out var meridian))
                {
                    break;
                }

                requests.Add(new SectionRequest(quarter, new SectionKey(zone, section, township, range, meridian)));

                var moreOptions = new PromptKeywordOptions("Add another section?")
                {
                    AllowNone = true
                };
                moreOptions.Keywords.Add("Yes");
                moreOptions.Keywords.Add("No");
                moreOptions.Keywords.Default = "No";

                var moreResult = editor.GetKeywords(moreOptions);
                addAnother = moreResult.Status == PromptStatus.OK &&
                             string.Equals(moreResult.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
            }

            return requests;
        }

        private static string PromptForClient(Editor editor, ExcelLookup lookup)
        {
            var values = lookup.Values;
            if (values.Count > 0 && values.Count <= 20)
            {
                var options = new PromptKeywordOptions("Select current client")
                {
                    AllowNone = false
                };

                foreach (var value in values)
                {
                    options.Keywords.Add(value);
                }

                var result = editor.GetKeywords(options);
                if (result.Status == PromptStatus.OK)
                {
                    return result.StringResult;
                }
            }

            if (values.Count > 0)
            {
                editor.WriteMessage("\nAvailable clients: " + string.Join(", ", values));
            }

            var prompt = new PromptStringOptions("Enter current client name: ")
            {
                AllowSpaces = true
            };
            var input = editor.GetString(prompt);
            return input.Status == PromptStatus.OK ? input.StringResult : string.Empty;
        }

        private static double PromptForDouble(Editor editor, string message, double defaultValue, double min, double max)
        {
            var options = new PromptDoubleOptions(message + " [" + defaultValue + "]: ")
            {
                DefaultValue = defaultValue,
                AllowNone = true
            };

            var result = editor.GetDouble(options);
            if (result.Status != PromptStatus.OK)
            {
                return defaultValue;
            }

            var value = result.Value;
            if (value < min || value > max)
            {
                editor.WriteMessage($"\nValue out of range. Using nearest allowed value ({min} - {max}).");
                return Math.Min(Math.Max(value, min), max);
            }

            return value;
        }

        private static int PromptForInt(Editor editor, string message, int defaultValue, int min, int max)
        {
            var options = new PromptIntegerOptions(message + " [" + defaultValue + "]: ")
            {
                DefaultValue = defaultValue,
                AllowNone = true,
                LowerLimit = min,
                UpperLimit = max
            };

            var result = editor.GetInteger(options);
            return result.Status == PromptStatus.OK ? result.Value : defaultValue;
        }

        private static string MapValue(ExcelLookup lookup, string key, string fallback)
        {
            var entry = lookup.Lookup(key);
            return string.IsNullOrWhiteSpace(entry?.Value) ? fallback : entry.Value!;
        }

        private static string FormatDispNum(string? dispNum)
        {
            var regex = new Regex("^([A-Z]{3})(\\d+)");
            var match = regex.Match(dispNum ?? string.Empty);
            if (!match.Success)
            {
                return dispNum ?? string.Empty;
            }

            return match.Groups[1].Value + " " + match.Groups[2].Value;
        }

        private static string ResolveLookupPath(string lookupFolder, string configuredFileName, string dllFolder, string defaultFileName)
        {
            // If the configured name is an absolute path, use it directly.
            if (!string.IsNullOrWhiteSpace(configuredFileName) && Path.IsPathRooted(configuredFileName) && File.Exists(configuredFileName))
                return configuredFileName;

            var fileName = string.IsNullOrWhiteSpace(configuredFileName) ? defaultFileName : configuredFileName;

            var candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(lookupFolder))
                candidates.Add(Path.Combine(lookupFolder, fileName));

            if (!string.IsNullOrWhiteSpace(dllFolder))
                candidates.Add(Path.Combine(dllFolder, fileName));

            // Fall back to current working directory if needed
            candidates.Add(Path.Combine(Environment.CurrentDirectory, fileName));

            foreach (var p in candidates)
            {
                try
                {
                    if (File.Exists(p))
                        return p;
                }
                catch
                {
                    // ignore
                }
            }

            // If nothing exists, return the first candidate so the logger prints a useful "not found" path.
            return candidates.FirstOrDefault() ?? Path.Combine(dllFolder ?? "", fileName);
        }

        private static bool PurposeRequiresWidth(string purpose, Config config)
        {
            if (string.IsNullOrWhiteSpace(purpose))
                return false;

            var norm = NormalizePurposeCode(purpose);
            var list = config.WidthRequiredPurposeCodes ?? Array.Empty<string>();

            foreach (var item in list)
            {
                if (NormalizePurposeCode(item) == norm)
                    return true;
            }

            return false;
        }

        private static string NormalizePurposeCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            // Trim, uppercase, and collapse all whitespace to single spaces
            var s = value.Trim().ToUpperInvariant();
            var chars = new List<char>(s.Length);
            bool prevSpace = false;

            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!prevSpace)
                    {
                        chars.Add(' ');
                        prevSpace = true;
                    }
                }
                else
                {
                    chars.Add(ch);
                    prevSpace = false;
                }
            }

            return new string(chars.ToArray());
        }

        private static string ToTitleCaseWords(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            // TitleCase, then common-word cleanup ("and" stays lower-case)
            var lower = NormalizePurposeCode(value).ToLowerInvariant();
            var title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lower);

            title = title.Replace(" And ", " and ");
            title = title.Replace(" Of ", " of ");
            title = title.Replace(" The ", " the ");
            return title;
        }

        private static bool IsWellSitePurpose(string purpose)
        {
            var normalized = NormalizePurposeCode(purpose);
            return string.Equals(normalized, "WELL SITE", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(normalized, "WELLSITE", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseSectionNumber(string section)
        {
            if (string.IsNullOrWhiteSpace(section))
            {
                return 0;
            }

            var match = Regex.Match(section, "\\d+");
            if (!match.Success)
            {
                return 0;
            }

            return int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
        }
    }
}

