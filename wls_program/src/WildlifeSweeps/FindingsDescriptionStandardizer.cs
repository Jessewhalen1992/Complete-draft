using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Xml.Linq;

namespace WildlifeSweeps
{
    internal sealed class FindingsDescriptionStandardizer
    {
        private const string OtherValue = "Other";
        private const string RabbitHareSpecies = "Rabbit / Hare";
        private const string SnowshoeHareSpecies = "Snowshoe Hare";
        private const string PreserveOriginalStandardDescription = "[Keep Original]";
        private static readonly RegexOptions RuleRegexOptions = RegexOptions.IgnoreCase | RegexOptions.Compiled;
        private static readonly Regex LeadingPhotoRegex = new Regex(@"^\s*(#?\d+)(?:\b[\s_-]*|[_-]+)", RegexOptions.Compiled);
        private static readonly Regex ParentheticalPhotoRegex = new Regex(@"\((\d+)\)", RegexOptions.Compiled);
        private static readonly Regex HashPhotoRegex = new Regex(@"#(\d+)", RegexOptions.Compiled);
        private static readonly Regex PunctuationRegex = new Regex(@"[_\W]+", RegexOptions.Compiled);
        private static readonly Regex StandalonePossessiveRegex = new Regex(@"\b['’`´]?\s*s\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MultiSpaceRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly TextInfo TitleCase = CultureInfo.CurrentCulture.TextInfo;
        private static readonly Dictionary<string, string> RabbitDescriptionAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Rabbit"] = SnowshoeHareSpecies,
            ["Rabbit Sighting"] = "Snowshoe Hare Sighting",
            ["Rabbit / Hare Sighting"] = "Snowshoe Hare Sighting",
            ["Rabbit Scat / Droppings"] = "Snowshoe Hare Scat",
            ["Snowshoe Hare Scat / Droppings"] = "Snowshoe Hare Scat",
            ["Rabbit Tracks"] = "Snowshoe Hare Tracks",
            ["Rabbit Trail"] = "Snowshoe Hare Trail"
        };

        private readonly IReadOnlyList<RecognitionRule> _regexRules;
        private readonly IReadOnlyList<RecognitionRule> _keywordRules;
        private readonly IReadOnlyList<string> _speciesOptions;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _findingTypesBySpecies;
        private readonly IReadOnlyList<string> _standardDescriptions;
        private readonly Dictionary<string, (string Species, string FindingType)> _standardDescriptionMap;
        private readonly HashSet<(string Species, string FindingType)> _validPairs;
        private readonly HashSet<string> _skipNormalizedFindings;
        private readonly string _customMappingsPath;
        private readonly string _lookupWorkbookPath;
        private readonly Action<string>? _logWarning;
        private readonly Dictionary<string, CustomMapping> _customMappings;
        private readonly string _unparsedFindingsPath;

        internal FindingsDescriptionStandardizer(string? lookupPath, Action<string>? logWarning)
        {
            var workbook = LookupWorkbook.Load(lookupPath, logWarning);
            _regexRules = workbook.RegexRules;
            _keywordRules = workbook.KeywordRules;
            _speciesOptions = workbook.SpeciesOptions;
            _findingTypesBySpecies = workbook.FindingTypesBySpecies;
            _standardDescriptions = workbook.StandardDescriptions;
            _standardDescriptionMap = workbook.StandardDescriptionMap;
            _validPairs = workbook.ValidPairs;
            _skipNormalizedFindings = workbook.SkipNormalizedFindings;
            _lookupWorkbookPath = workbook.SourcePath;
            _logWarning = logWarning;

            _customMappingsPath = CustomMapping.GetDefaultPath();
            _customMappings = CustomMapping.Load(_customMappingsPath, logWarning);

            _unparsedFindingsPath = GetUnparsedFindingsPath();
        }

        internal IReadOnlyList<string> SpeciesOptions => _speciesOptions;

        internal IReadOnlyDictionary<string, IReadOnlyList<string>> FindingTypesBySpecies => _findingTypesBySpecies;

        internal IReadOnlyList<string> StandardDescriptionOptions => _standardDescriptions;

        internal bool IsValidPair(string? species, string? findingType)
        {
            species = CanonicalizeSpecies(species);
            if (string.IsNullOrWhiteSpace(species) || string.IsNullOrWhiteSpace(findingType))
            {
                return false;
            }

            return _validPairs.Contains((species.Trim(), findingType.Trim()));
        }

        internal bool TryResolveStandardDescription(string? description, out string species, out string findingType)
        {
            species = string.Empty;
            findingType = string.Empty;
            description = CanonicalizeStandardDescription(description);

            if (string.IsNullOrWhiteSpace(description))
            {
                return false;
            }

            return _standardDescriptionMap.TryGetValue(description.Trim(), out var resolved)
                   && AssignResolved(resolved, out species, out findingType);
        }

        internal bool TryGetDefaultDescriptionForPair(string? species, string? findingType, out string description)
        {
            description = string.Empty;
            species = CanonicalizeSpecies(species);

            if (string.IsNullOrWhiteSpace(species) || string.IsNullOrWhiteSpace(findingType))
            {
                return false;
            }

            var match = _standardDescriptionMap
                .FirstOrDefault(item => item.Value.Species.Equals(species, StringComparison.OrdinalIgnoreCase)
                                        && item.Value.FindingType.Equals(findingType, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Key))
            {
                description = match.Key;
                return true;
            }

            description = CanonicalizeStandardDescription($"{TitleCase.ToTitleCase(species.Trim())} {TitleCase.ToTitleCase(findingType.Trim())}");
            return true;
        }

        internal IReadOnlyList<StandardizedFinding> Standardize(string? originalText, Func<PromptContext, PromptResult> promptForUnmapped)
        {
            var preprocess = Preprocess(originalText);
            if (string.IsNullOrWhiteSpace(preprocess.CleanedOriginal))
            {
                return new List<StandardizedFinding>
                {
                    new StandardizedFinding(preprocess, string.Empty, string.Empty, string.Empty, StandardizationSource.Skipped)
                };
            }

            if (IsSkippedNormalized(preprocess.NormalizedText))
            {
                return new List<StandardizedFinding>
                {
                    new StandardizedFinding(preprocess, string.Empty, string.Empty, preprocess.CleanedOriginal, StandardizationSource.Ignored)
                };
            }

            if (_customMappings.TryGetValue(preprocess.NormalizedText, out var custom))
            {
                custom = CanonicalizeCustomMapping(custom);
                return BuildResults(preprocess, new List<RecognitionMatch>
                {
                    new RecognitionMatch(custom.Species, custom.FindingType, custom.StandardDescription, StandardizationSource.CustomMapping)
                }, promptForUnmapped);
            }

            var regexMatches = GetMatches(_regexRules, preprocess.NormalizedText, match => match.IsRegexMatch);
            if (regexMatches.Count > 0)
            {
                return BuildResults(preprocess, regexMatches, promptForUnmapped);
            }

            var keywordMatches = GetMatches(_keywordRules, preprocess.NormalizedText, match => match.IsKeywordMatch);
            if (keywordMatches.Count > 0)
            {
                return BuildResults(preprocess, keywordMatches, promptForUnmapped);
            }

            return BuildResults(preprocess, new List<RecognitionMatch>(), promptForUnmapped);
        }

        private IReadOnlyList<StandardizedFinding> BuildResults(
            PreprocessResult preprocess,
            IReadOnlyList<RecognitionMatch> matches,
            Func<PromptContext, PromptResult> promptForUnmapped)
        {
            var results = new List<StandardizedFinding>();

            if (matches.Any(match => ShouldSkipMatch(match)))
            {
                results.Add(new StandardizedFinding(preprocess, string.Empty, string.Empty, preprocess.CleanedOriginal, StandardizationSource.Ignored));
                return results;
            }

            if (matches.Count == 0)
            {
                var promptResult = promptForUnmapped(new PromptContext(preprocess.CleanedOriginal, preprocess.NormalizedText));
                var resolved = ResolvePromptResult(preprocess, promptResult, promptForUnmapped);
                results.Add(resolved);
                return results;
            }

            foreach (var match in matches)
            {
                if (!IsUsableMatch(match))
                {
                    var promptResult = promptForUnmapped(new PromptContext(preprocess.CleanedOriginal, preprocess.NormalizedText));
                    var resolved = ResolvePromptResult(preprocess, promptResult, promptForUnmapped);
                    results.Add(resolved);
                    continue;
                }

                var canonicalMatch = CanonicalizeMatch(match);
                var standardDescription = ResolveMatchedStandardDescription(preprocess, canonicalMatch.StandardDescription);
                results.Add(new StandardizedFinding(preprocess, canonicalMatch.Species, canonicalMatch.FindingType, standardDescription, canonicalMatch.Source));
            }

            return results
                .GroupBy(
                    item => $"{item.Species}|{item.FindingType}|{item.StandardDescription}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private bool IsSkippedNormalized(string normalizedText)
        {
            return !string.IsNullOrWhiteSpace(normalizedText)
                   && _skipNormalizedFindings.Contains(normalizedText);
        }

        private bool IsUsableMatch(RecognitionMatch match)
        {
            return !string.IsNullOrWhiteSpace(match.StandardDescription)
                   || IsValidPair(match.Species, match.FindingType)
                   || FindingOtherValueHelper.IsOtherValue(match.Species, OtherValue)
                   || FindingOtherValueHelper.IsOtherValue(match.FindingType, OtherValue);
        }

        private bool ShouldSkipMatch(RecognitionMatch match)
        {
            if (IsSkippedNormalized(Preprocess(match.StandardDescription).NormalizedText))
            {
                return true;
            }

            var pairText = $"{match.Species} {match.FindingType}";
            return IsSkippedNormalized(Preprocess(pairText).NormalizedText);
        }

        private StandardizedFinding ResolvePromptResult(
            PreprocessResult preprocess,
            PromptResult promptResult,
            Func<PromptContext, PromptResult> promptForUnmapped)
        {
            if (promptResult.Ignored)
            {
                return new StandardizedFinding(preprocess, string.Empty, string.Empty, string.Empty, StandardizationSource.Ignored);
            }

            if (promptResult.Skipped)
            {
                TrackUnparsedFinding(preprocess.CleanedOriginal);
                return new StandardizedFinding(
                    preprocess,
                    OtherValue,
                    OtherValue,
                    string.IsNullOrWhiteSpace(preprocess.CleanedOriginal)
                        ? OtherValue
                        : preprocess.CleanedOriginal,
                    StandardizationSource.Skipped);
            }

            var species = FindingOtherValueHelper.NormalizeOtherValue(promptResult.Species, OtherValue);
            var findingType = FindingOtherValueHelper.NormalizeOtherValue(promptResult.FindingType, OtherValue);
            var description = FindingOtherValueHelper.NormalizeOtherValue(promptResult.StandardDescription, OtherValue);
            description = CanonicalizeStandardDescription(description);
            species = CanonicalizeSpecies(species);

            if (string.IsNullOrWhiteSpace(description))
            {
                if (!string.IsNullOrWhiteSpace(species) && !string.IsNullOrWhiteSpace(findingType))
                {
                    if (FindingOtherValueHelper.IsOtherValue(species, OtherValue) ||
                        FindingOtherValueHelper.IsOtherValue(findingType, OtherValue))
                    {
                        description = OtherValue;
                    }
                    else
                    {
                        TryGetDefaultDescriptionForPair(species, findingType, out description);
                    }
                }

                if (string.IsNullOrWhiteSpace(description))
                {
                    var retry = promptForUnmapped(new PromptContext(preprocess.CleanedOriginal, preprocess.NormalizedText));
                    return ResolvePromptResult(preprocess, retry, promptForUnmapped);
                }
            }

            if ((!string.IsNullOrWhiteSpace(species) || !string.IsNullOrWhiteSpace(findingType)) &&
                !IsValidPair(species, findingType) &&
                !FindingOtherValueHelper.IsOtherValue(species, OtherValue) &&
                !FindingOtherValueHelper.IsOtherValue(findingType, OtherValue))
            {
                var retry = promptForUnmapped(new PromptContext(preprocess.CleanedOriginal, preprocess.NormalizedText));
                return ResolvePromptResult(preprocess, retry, promptForUnmapped);
            }

            if (string.IsNullOrWhiteSpace(species) || string.IsNullOrWhiteSpace(findingType))
            {
                if (FindingOtherValueHelper.IsOtherValue(description, OtherValue))
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
                else if (TryResolveStandardDescription(description, out var resolvedSpecies, out var resolvedFindingType))
                {
                    species = resolvedSpecies;
                    findingType = resolvedFindingType;
                }
                else
                {
                    species = string.Empty;
                    findingType = string.Empty;
                }
            }

            var result = CanonicalizeStandardizedFinding(new StandardizedFinding(preprocess, species, findingType, description, StandardizationSource.Prompt));
            if (promptResult.RememberMapping)
            {
                RememberPromptMapping(preprocess, result);
            }

            return result;
        }

        private void RememberPromptMapping(PreprocessResult preprocess, StandardizedFinding result)
        {
            if (string.IsNullOrWhiteSpace(preprocess.NormalizedText) || string.IsNullOrWhiteSpace(result.StandardDescription))
            {
                return;
            }

            _customMappings[preprocess.NormalizedText] = new CustomMapping(
                preprocess.NormalizedText,
                result.Species,
                result.FindingType,
                result.StandardDescription);

            if (!string.IsNullOrWhiteSpace(_lookupWorkbookPath) &&
                LookupWorkbook.TryUpsertKeywordMapping(_lookupWorkbookPath, preprocess.CleanedOriginal, result.StandardDescription, _logWarning))
            {
                return;
            }

            _logWarning?.Invoke("Findings lookup workbook was not updated; the new mapping is available only for this run.");
        }

        private static IReadOnlyList<RecognitionMatch> GetMatches(
            IReadOnlyList<RecognitionRule> rules,
            string normalizedText,
            Func<RecognitionRule, bool> matcher)
        {
            var matches = new List<RecognitionMatch>();
            int? priority = null;

            foreach (var rule in rules)
            {
                if (!matcher(rule))
                {
                    continue;
                }

                if (!rule.IsMatch(normalizedText))
                {
                    continue;
                }

                if (priority == null)
                {
                    priority = rule.Priority;
                }
                else if (rule.Priority != priority)
                {
                    break;
                }

                matches.Add(new RecognitionMatch(rule.Species, rule.FindingType, rule.StandardDescription, rule.Source));
            }

            return matches;
        }


        private static PreprocessResult Preprocess(string? originalText)
        {
            var text = originalText ?? string.Empty;
            var refs = ExtractPhotoReferences(text);

            var withoutLeading = LeadingPhotoRegex.Replace(text, string.Empty);
            withoutLeading = ParentheticalPhotoRegex.Replace(withoutLeading, " ");
            withoutLeading = HashPhotoRegex.Replace(withoutLeading, " ");

            var cleaned = PunctuationRegex.Replace(withoutLeading, " ");
            cleaned = StandalonePossessiveRegex.Replace(cleaned, " ");
            cleaned = MultiSpaceRegex.Replace(cleaned, " ").Trim();
            var cleanedOriginal = string.IsNullOrWhiteSpace(cleaned) ? string.Empty : TitleCase.ToTitleCase(cleaned);

            var normalized = cleaned.ToLowerInvariant();

            return new PreprocessResult(cleanedOriginal, normalized, string.Join(", ", refs));
        }

        private static string GetUnparsedFindingsPath()
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WildlifeSweeps");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "unparsed_findings.txt");
        }

        private void TrackUnparsedFinding(string? cleanedOriginal)
        {
            if (string.IsNullOrWhiteSpace(cleanedOriginal))
            {
                return;
            }

            var normalized = cleanedOriginal.Trim();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(_unparsedFindingsPath))
            {
                foreach (var line in File.ReadAllLines(_unparsedFindingsPath))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        existing.Add(line.Trim());
                    }
                }
            }

            if (existing.Contains(normalized))
            {
                return;
            }

            File.AppendAllLines(_unparsedFindingsPath, new[] { normalized });
        }

        private static IEnumerable<string> ExtractPhotoReferences(string text)
        {
            var refs = new List<(int Index, string Value)>();
            var leadingMatch = LeadingPhotoRegex.Match(text);
            if (leadingMatch.Success)
            {
                refs.Add((leadingMatch.Index, leadingMatch.Groups[1].Value.TrimStart('#')));
            }

            foreach (Match match in ParentheticalPhotoRegex.Matches(text))
            {
                refs.Add((match.Index, match.Groups[1].Value));
            }

            foreach (Match match in HashPhotoRegex.Matches(text))
            {
                refs.Add((match.Index, match.Groups[1].Value));
            }

            return refs
                .OrderBy(item => item.Index)
                .Select(item => item.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool AssignResolved((string Species, string FindingType) resolved, out string species, out string findingType)
        {
            species = CanonicalizeSpecies(resolved.Species);
            findingType = resolved.FindingType;
            return !string.IsNullOrWhiteSpace(species) && !string.IsNullOrWhiteSpace(findingType);
        }

        private static string CanonicalizeSpecies(string? species)
        {
            var trimmed = species?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            return string.Equals(trimmed, RabbitHareSpecies, StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "Rabbit/Hare", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "Rabbit", StringComparison.OrdinalIgnoreCase)
                    ? SnowshoeHareSpecies
                    : trimmed;
        }

        private static string CanonicalizeStandardDescription(string? description)
        {
            var trimmed = description?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            if (RabbitDescriptionAliases.TryGetValue(trimmed, out var canonical))
            {
                return canonical;
            }

            return trimmed.StartsWith("Rabbit ", StringComparison.OrdinalIgnoreCase)
                ? SnowshoeHareSpecies + trimmed.Substring("Rabbit".Length)
                : trimmed;
        }

        private static RecognitionRule CanonicalizeRule(RecognitionRule rule)
        {
            return rule with
            {
                Species = CanonicalizeSpecies(rule.Species),
                StandardDescription = CanonicalizeStandardDescription(rule.StandardDescription)
            };
        }

        private static RecognitionMatch CanonicalizeMatch(RecognitionMatch match)
        {
            return match with
            {
                Species = CanonicalizeSpecies(match.Species),
                StandardDescription = CanonicalizeStandardDescription(match.StandardDescription)
            };
        }

        private static string ResolveMatchedStandardDescription(PreprocessResult preprocess, string standardDescription)
        {
            return string.Equals(standardDescription, PreserveOriginalStandardDescription, StringComparison.OrdinalIgnoreCase)
                ? preprocess.CleanedOriginal
                : standardDescription;
        }

        private static CustomMapping CanonicalizeCustomMapping(CustomMapping mapping)
        {
            return mapping with
            {
                Species = CanonicalizeSpecies(mapping.Species),
                StandardDescription = CanonicalizeStandardDescription(mapping.StandardDescription)
            };
        }

        private static StandardizedFinding CanonicalizeStandardizedFinding(StandardizedFinding finding)
        {
            return finding with
            {
                Species = CanonicalizeSpecies(finding.Species),
                StandardDescription = CanonicalizeStandardDescription(finding.StandardDescription)
            };
        }

        internal readonly record struct StandardizedFinding(
            PreprocessResult Preprocess,
            string Species,
            string FindingType,
            string StandardDescription,
            StandardizationSource Source)
        {
            public string CleanedOriginal => Preprocess.CleanedOriginal;
            public string NormalizedText => Preprocess.NormalizedText;
            public string PhotoRef => Preprocess.PhotoRef;
        }

        internal readonly record struct PreprocessResult(string CleanedOriginal, string NormalizedText, string PhotoRef);

        internal readonly record struct PromptContext(string CleanedOriginal, string NormalizedText);

        internal readonly record struct PromptResult(
            string StandardDescription,
            string Species,
            string FindingType,
            bool RememberMapping,
            bool Ignored,
            bool Skipped);

        internal enum StandardizationSource
        {
            RegexRule,
            KeywordRule,
            CustomMapping,
            Prompt,
            Ignored,
            Skipped
        }

        private sealed record RecognitionRule(
            int Priority,
            string Species,
            string FindingType,
            string StandardDescription,
            string Pattern,
            bool IsRegexMatch,
            bool IsKeywordMatch,
            StandardizationSource Source)
        {
            private readonly Regex _regex = new Regex(Pattern, RuleRegexOptions);

            public bool IsMatch(string text)
            {
                return IsRegexMatch
                    ? _regex.IsMatch(text)
                    : text.IndexOf(Pattern, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private sealed record RecognitionMatch(
            string Species,
            string FindingType,
            string StandardDescription,
            StandardizationSource Source);

        private sealed record CustomMapping(
            string NormalizedText,
            string Species,
            string FindingType,
            string StandardDescription)
        {
            public static string GetDefaultPath()
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WildlifeSweeps");
                Directory.CreateDirectory(directory);
                return Path.Combine(directory, "custom_mappings.json");
            }

            public static Dictionary<string, CustomMapping> Load(string path, Action<string>? logWarning)
            {
                if (!File.Exists(path))
                {
                    return new Dictionary<string, CustomMapping>(StringComparer.OrdinalIgnoreCase);
                }

                try
                {
                    var json = File.ReadAllText(path);
                    var items = JsonSerializer.Deserialize<List<CustomMapping>>(json);
                    return items?.Where(item => !string.IsNullOrWhiteSpace(item.NormalizedText))
                               .Select(CanonicalizeCustomMapping)
                               .ToDictionary(item => item.NormalizedText, StringComparer.OrdinalIgnoreCase)
                           ?? new Dictionary<string, CustomMapping>(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    logWarning?.Invoke($"Failed to load custom mappings: {ex.Message}");
                    return new Dictionary<string, CustomMapping>(StringComparer.OrdinalIgnoreCase);
                }
            }

            public static void Save(string path, IEnumerable<CustomMapping> mappings)
            {
                var json = JsonSerializer.Serialize(mappings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
        }

        private sealed class LookupWorkbook
        {
            private const string RegexSheet = "RecognitionRegex";
            private const string KeywordSheet = "RecognitionKeywords";
            private const string TypesSheet = "SpeciesFindingTypes";
            private const string SkipsSheet = "Skips";
            private const string SkippedSheet = "Skipped";
            private const string PrimaryLookupFileName = "wildlife_parsing_codex_lookup.xlsx";
            private const string MirrorLookupFileName = "wildlife_parsing_codex_lookup_backup.xlsx";
            private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            private static readonly XNamespace RelationshipsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
            private static readonly XNamespace DocumentRelationshipsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

            public IReadOnlyList<RecognitionRule> RegexRules { get; private init; } = Array.Empty<RecognitionRule>();

            public IReadOnlyList<RecognitionRule> KeywordRules { get; private init; } = Array.Empty<RecognitionRule>();

            public IReadOnlyList<string> SpeciesOptions { get; private init; } = Array.Empty<string>();

            public IReadOnlyDictionary<string, IReadOnlyList<string>> FindingTypesBySpecies { get; private init; }
                = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

            public IReadOnlyList<string> StandardDescriptions { get; private init; } = Array.Empty<string>();

            public string SourcePath { get; private init; } = string.Empty;
            public Dictionary<string, (string Species, string FindingType)> StandardDescriptionMap { get; private init; }
                = new Dictionary<string, (string Species, string FindingType)>(StringComparer.OrdinalIgnoreCase);

            public HashSet<(string Species, string FindingType)> ValidPairs { get; private init; }
                = new HashSet<(string Species, string FindingType)>();

            public HashSet<string> SkipNormalizedFindings { get; private init; }
                = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public static LookupWorkbook Load(string? path, Action<string>? logWarning)
            {
                var candidatePaths = ResolveLookupPaths(path, logWarning);
                if (candidatePaths.Count == 0)
                {
                    logWarning?.Invoke($"Findings lookup workbooks not found. Expected {PrimaryLookupFileName} and {MirrorLookupFileName} beside the plugin.");
                    return new LookupWorkbook();
                }

                foreach (var candidatePath in candidatePaths)
                {
                    try
                    {
                        var workbook = ReadWorkbook(candidatePath);
                        EnsureMirrorLookupCopies(candidatePath, logWarning);
                        return workbook;
                    }
                    catch (Exception ex)
                    {
                        logWarning?.Invoke($"Failed to load findings lookup workbook '{Path.GetFileName(candidatePath)}': {ex.Message}");
                    }
                }

                return new LookupWorkbook();
            }

            private static IReadOnlyList<string> ResolveLookupPaths(string? configuredPath, Action<string>? logWarning)
            {
                var paths = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void AddPath(string? candidate)
                {
                    if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
                    {
                        paths.Add(candidate);
                    }
                }

                if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                {
                    foreach (var candidate in GetLookupReplicaPaths(configuredPath))
                    {
                        AddPath(candidate);
                    }

                    return paths;
                }

                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    logWarning?.Invoke($"Configured findings lookup not found: {configuredPath}. Falling back to plugin folder.");
                }

                foreach (var candidate in ResolveDefaultLookupPaths())
                {
                    AddPath(candidate);
                }

                return paths;
            }

            private static IReadOnlyList<string> ResolveDefaultLookupPaths()
            {
                var candidateDirectories = new List<string>();
                var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void AddDirectory(string? directory)
                {
                    if (!string.IsNullOrWhiteSpace(directory) && seenDirectories.Add(directory))
                    {
                        candidateDirectories.Add(directory);
                    }
                }

                var assemblyLocation = typeof(FindingsDescriptionStandardizer).Assembly.Location;
                if (!string.IsNullOrWhiteSpace(assemblyLocation))
                {
                    AddDirectory(Path.GetDirectoryName(assemblyLocation));
                }

                AddDirectory(AppDomain.CurrentDomain.BaseDirectory);
                AddDirectory(Environment.CurrentDirectory);
                AddDirectory(AppContext.BaseDirectory);

                var paths = new List<string>();
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var directory in candidateDirectories)
                {
                    foreach (var fileName in new[] { PrimaryLookupFileName, MirrorLookupFileName })
                    {
                        var candidate = Path.Combine(directory, fileName);
                        if (File.Exists(candidate) && seenPaths.Add(candidate))
                        {
                            paths.Add(candidate);
                        }
                    }
                }

                return paths;
            }

            private static IReadOnlyList<string> GetLookupReplicaPaths(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return Array.Empty<string>();
                }

                var directory = Path.GetDirectoryName(path);
                var fileName = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
                {
                    return new[] { path };
                }

                if (string.Equals(fileName, PrimaryLookupFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, MirrorLookupFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return new[]
                    {
                        Path.Combine(directory, PrimaryLookupFileName),
                        Path.Combine(directory, MirrorLookupFileName)
                    };
                }

                return new[] { path };
            }

            private static void EnsureMirrorLookupCopies(string sourcePath, Action<string>? logWarning)
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    return;
                }

                foreach (var replicaPath in GetLookupReplicaPaths(sourcePath))
                {
                    if (string.Equals(replicaPath, sourcePath, StringComparison.OrdinalIgnoreCase) || File.Exists(replicaPath))
                    {
                        continue;
                    }

                    try
                    {
                        File.Copy(sourcePath, replicaPath, overwrite: false);
                    }
                    catch (Exception ex)
                    {
                        logWarning?.Invoke($"Failed to create backup findings lookup workbook '{Path.GetFileName(replicaPath)}': {ex.Message}");
                    }
                }
            }

            private static LookupWorkbook ReadWorkbook(string path)
            {
                var sheets = XlsxReader.LoadSheets(path);

                var regexRows = sheets.TryGetValue(RegexSheet, out var regexSheet)
                    ? regexSheet
                    : new List<Dictionary<string, string>>();
                var keywordRows = sheets.TryGetValue(KeywordSheet, out var keywordSheet)
                    ? keywordSheet
                    : new List<Dictionary<string, string>>();
                var typeRows = sheets.TryGetValue(TypesSheet, out var typesSheet)
                    ? typesSheet
                    : new List<Dictionary<string, string>>();
                var skipRows = sheets.TryGetValue(SkipsSheet, out var skipsSheet)
                    ? skipsSheet
                    : (sheets.TryGetValue(SkippedSheet, out var skippedSheet)
                        ? skippedSheet
                        : new List<Dictionary<string, string>>());

                var regexRules = regexRows
                    .Select(row => CreateRule(row, isRegex: true))
                    .Where(rule => rule != null)
                    .Select(rule => rule!)
                    .OrderBy(rule => rule.Priority)
                    .ToList();

                var keywordRules = keywordRows
                    .Select(row => CreateRule(row, isRegex: false))
                    .Where(rule => rule != null)
                    .Select(rule => rule!)
                    .OrderBy(rule => rule.Priority)
                    .ToList();

                var validPairs = new HashSet<(string Species, string FindingType)>();
                var speciesOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var findingTypesBySpecies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in typeRows)
                {
                    if (!row.TryGetValue("Species", out var species) || string.IsNullOrWhiteSpace(species))
                    {
                        continue;
                    }

                    if (!row.TryGetValue("FindingType", out var findingType) || string.IsNullOrWhiteSpace(findingType))
                    {
                        continue;
                    }

                    species = CanonicalizeSpecies(species);
                    findingType = findingType.Trim();
                    validPairs.Add((species, findingType));
                    speciesOptions.Add(species);

                    if (!findingTypesBySpecies.TryGetValue(species, out var list))
                    {
                        list = new List<string>();
                        findingTypesBySpecies[species] = list;
                    }

                    if (!list.Contains(findingType, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(findingType);
                    }
                }

                foreach (var list in findingTypesBySpecies.Values)
                {
                    list.Sort(StringComparer.OrdinalIgnoreCase);
                }

                var descriptionMap = new Dictionary<string, (string Species, string FindingType)>(StringComparer.OrdinalIgnoreCase);
                foreach (var rule in regexRules.Concat(keywordRules))
                {
                    if (!string.IsNullOrWhiteSpace(rule.StandardDescription)
                        && !descriptionMap.ContainsKey(rule.StandardDescription))
                    {
                        descriptionMap[rule.StandardDescription] = (rule.Species, rule.FindingType);
                    }
                }

                var standardDescriptions = descriptionMap.Keys
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var skipNormalizedFindings = ParseSkipFindings(skipRows);

                return new LookupWorkbook
                {
                    RegexRules = regexRules,
                    KeywordRules = keywordRules,
                    SpeciesOptions = speciesOptions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                    FindingTypesBySpecies = findingTypesBySpecies.ToDictionary(
                        pair => pair.Key,
                        pair => (IReadOnlyList<string>)pair.Value),
                    StandardDescriptions = standardDescriptions,
                    SourcePath = path,
                    StandardDescriptionMap = descriptionMap,
                    ValidPairs = validPairs,
                    SkipNormalizedFindings = skipNormalizedFindings
                };
            }

            private static RecognitionRule? CreateRule(Dictionary<string, string> row, bool isRegex)
            {
                if (!row.TryGetValue(isRegex ? "Regex" : "Keyword", out var pattern) || string.IsNullOrWhiteSpace(pattern))
                {
                    return null;
                }

                row.TryGetValue("Species", out var species);
                row.TryGetValue("FindingType", out var findingType);
                row.TryGetValue("StandardDescription", out var description);
                row.TryGetValue("Priority", out var priorityRaw);
                _ = int.TryParse(priorityRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority);

                species = species?.Trim() ?? string.Empty;
                findingType = findingType?.Trim() ?? string.Empty;
                description = description?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(species) &&
                    string.IsNullOrWhiteSpace(findingType) &&
                    string.IsNullOrWhiteSpace(description))
                {
                    return null;
                }

                if (!isRegex)
                {
                    pattern = NormalizeKeyword(pattern);
                }

                return CanonicalizeRule(new RecognitionRule(
                    priority,
                    species,
                    findingType,
                    description,
                    pattern.Trim(),
                    isRegex,
                    !isRegex,
                    isRegex ? StandardizationSource.RegexRule : StandardizationSource.KeywordRule));
            }

            private static string NormalizeKeyword(string keyword)
            {
                var normalized = keyword.Trim().ToLowerInvariant();
                normalized = PunctuationRegex.Replace(normalized, " ");
                normalized = MultiSpaceRegex.Replace(normalized, " ").Trim();
                return normalized;
            }

                        public static bool TryUpsertKeywordMapping(string path, string keyword, string standardDescription, Action<string>? logWarning)
            {
                if (string.IsNullOrWhiteSpace(path) ||
                    string.IsNullOrWhiteSpace(keyword) ||
                    string.IsNullOrWhiteSpace(standardDescription))
                {
                    return false;
                }

                var replicaPaths = GetLookupReplicaPaths(path)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (replicaPaths.Count == 0)
                {
                    return false;
                }

                var seedPath = replicaPaths.FirstOrDefault(File.Exists);
                if (string.IsNullOrWhiteSpace(seedPath))
                {
                    return false;
                }

                var trimmedKeyword = keyword.Trim();
                var trimmedDescription = standardDescription.Trim();
                var updatedCount = 0;
                var failures = new List<string>();

                foreach (var replicaPath in replicaPaths)
                {
                    try
                    {
                        if (!File.Exists(replicaPath))
                        {
                            File.Copy(seedPath, replicaPath, overwrite: false);
                        }

                        UpsertKeywordMapping(replicaPath, trimmedKeyword, trimmedDescription);
                        updatedCount++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{Path.GetFileName(replicaPath)}: {ex.Message}");
                    }
                }

                if (failures.Count > 0)
                {
                    logWarning?.Invoke($"Failed to sync one or more findings lookup workbook copies: {string.Join(" | ", failures)}");
                }

                return updatedCount > 0;
            }


            private static void UpsertKeywordMapping(string path, string keyword, string standardDescription)
            {
                using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
                var sharedStrings = LoadSharedStrings(archive);
                var workbook = XDocument.Load(GetEntryStream(archive, "xl/workbook.xml"));
                var rels = XDocument.Load(GetEntryStream(archive, "xl/_rels/workbook.xml.rels"));
                var relMap = rels
                    .Descendants(RelationshipsNs + "Relationship")
                    .ToDictionary(
                        rel => rel.Attribute("Id")?.Value ?? string.Empty,
                        rel => rel.Attribute("Target")?.Value?.TrimStart('/') ?? string.Empty);

                var sheet = workbook
                    .Descendants(SpreadsheetNs + "sheet")
                    .FirstOrDefault(node => string.Equals(node.Attribute("name")?.Value, KeywordSheet, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Workbook does not contain the {KeywordSheet} sheet.");

                var relId = sheet.Attribute(DocumentRelationshipsNs + "id")?.Value ?? string.Empty;
                if (!relMap.TryGetValue(relId, out var target) || string.IsNullOrWhiteSpace(target))
                {
                    throw new InvalidOperationException($"Workbook relationship for {KeywordSheet} is missing.");
                }

                if (!target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                {
                    target = "xl/" + target.TrimStart('/');
                }

                var sheetDocument = XDocument.Load(GetEntryStream(archive, target));
                var sheetData = sheetDocument.Root?.Element(SpreadsheetNs + "sheetData")
                    ?? throw new InvalidOperationException($"Workbook sheet data for {KeywordSheet} is missing.");
                var rows = sheetData.Elements(SpreadsheetNs + "row").ToList();
                if (rows.Count == 0)
                {
                    var headerRow = CreateHeaderRow();
                    sheetData.Add(headerRow);
                    rows.Add(headerRow);
                }

                var header = ParseRow(rows[0], sharedStrings);
                var priorityColumn = GetHeaderColumnIndex(header, "Priority", 0);
                var keywordColumn = GetHeaderColumnIndex(header, "Keyword", 1);
                var speciesColumn = GetHeaderColumnIndex(header, "Species", 2);
                var findingTypeColumn = GetHeaderColumnIndex(header, "FindingType", 3);
                var descriptionColumn = GetHeaderColumnIndex(header, "StandardDescription", 4);
                var normalizedKeyword = NormalizeKeyword(keyword);

                XElement? matchedRow = null;
                foreach (var row in rows.Skip(1))
                {
                    var values = ParseRow(row, sharedStrings);
                    var existingKeyword = keywordColumn < values.Count ? values[keywordColumn] : string.Empty;
                    if (string.Equals(NormalizeKeyword(existingKeyword), normalizedKeyword, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedRow = row;
                        break;
                    }
                }

                var rowNumber = matchedRow != null
                    ? ((int?)matchedRow.Attribute("r") ?? GetNextRowNumber(rows))
                    : GetNextRowNumber(rows);
                if (matchedRow == null)
                {
                    matchedRow = new XElement(SpreadsheetNs + "row", new XAttribute("r", rowNumber));
                    sheetData.Add(matchedRow);
                }
                else
                {
                    matchedRow.SetAttributeValue("r", rowNumber);
                }

                SetNumericCellValue(matchedRow, rowNumber, priorityColumn + 1, "1");
                SetInlineStringCellValue(matchedRow, rowNumber, keywordColumn + 1, keyword);
                SetInlineStringCellValue(matchedRow, rowNumber, speciesColumn + 1, string.Empty);
                SetInlineStringCellValue(matchedRow, rowNumber, findingTypeColumn + 1, string.Empty);
                SetInlineStringCellValue(matchedRow, rowNumber, descriptionColumn + 1, standardDescription);

                ReplaceEntry(archive, target, sheetDocument);
            }

            private static XElement CreateHeaderRow()
            {
                return new XElement(SpreadsheetNs + "row",
                    new XAttribute("r", 1),
                    CreateInlineStringCell("A1", "Priority"),
                    CreateInlineStringCell("B1", "Keyword"),
                    CreateInlineStringCell("C1", "Species"),
                    CreateInlineStringCell("D1", "FindingType"),
                    CreateInlineStringCell("E1", "StandardDescription"));
            }

            private static int GetHeaderColumnIndex(IReadOnlyList<string> header, string name, int fallback)
            {
                for (var index = 0; index < header.Count; index++)
                {
                    if (string.Equals(header[index], name, StringComparison.OrdinalIgnoreCase))
                    {
                        return index;
                    }
                }

                return fallback;
            }

            private static int GetNextRowNumber(IEnumerable<XElement> rows)
            {
                return rows
                    .Select(row => (int?)row.Attribute("r") ?? 0)
                    .DefaultIfEmpty(0)
                    .Max() + 1;
            }

            private static void SetNumericCellValue(XElement row, int rowNumber, int columnNumber, string value)
            {
                var cellReference = BuildCellReference(columnNumber, rowNumber);
                var cell = row.Elements(SpreadsheetNs + "c")
                    .FirstOrDefault(item => string.Equals(item.Attribute("r")?.Value, cellReference, StringComparison.OrdinalIgnoreCase));
                if (cell == null)
                {
                    cell = new XElement(SpreadsheetNs + "c", new XAttribute("r", cellReference));
                }
                else
                {
                    cell.RemoveNodes();
                    cell.RemoveAttributes();
                    cell.SetAttributeValue("r", cellReference);
                }

                cell.Add(new XElement(SpreadsheetNs + "v", value));
                UpsertCell(row, cell);
            }

            private static void SetInlineStringCellValue(XElement row, int rowNumber, int columnNumber, string value)
            {
                var cellReference = BuildCellReference(columnNumber, rowNumber);
                var existing = row.Elements(SpreadsheetNs + "c")
                    .FirstOrDefault(item => string.Equals(item.Attribute("r")?.Value, cellReference, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(value))
                {
                    existing?.Remove();
                    return;
                }

                var cell = CreateInlineStringCell(cellReference, value);
                UpsertCell(row, cell);
            }

            private static XElement CreateInlineStringCell(string cellReference, string value)
            {
                return new XElement(SpreadsheetNs + "c",
                    new XAttribute("r", cellReference),
                    new XAttribute("t", "inlineStr"),
                    new XElement(SpreadsheetNs + "is",
                        new XElement(SpreadsheetNs + "t", value)));
            }

            private static void UpsertCell(XElement row, XElement replacement)
            {
                var replacementRef = replacement.Attribute("r")?.Value ?? string.Empty;
                var cells = row.Elements(SpreadsheetNs + "c")
                    .Where(cell => !string.Equals(cell.Attribute("r")?.Value, replacementRef, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                cells.Add(replacement);
                row.ReplaceNodes(cells.OrderBy(cell => ColumnNameToIndex(cell.Attribute("r")?.Value)));
            }

            private static void ReplaceEntry(ZipArchive archive, string entryName, XDocument document)
            {
                var existing = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"Workbook entry {entryName} was not found.");
                existing.Delete();
                var replacement = archive.CreateEntry(entryName);
                using var stream = replacement.Open();
                document.Save(stream);
            }

            private static Stream GetEntryStream(ZipArchive archive, string name)
            {
                var entry = archive.GetEntry(name) ?? throw new InvalidOperationException($"Missing workbook entry: {name}");
                return entry.Open();
            }

            private static IReadOnlyList<string> LoadSharedStrings(ZipArchive archive)
            {
                var entry = archive.GetEntry("xl/sharedStrings.xml");
                if (entry == null)
                {
                    return Array.Empty<string>();
                }

                using var stream = entry.Open();
                var document = XDocument.Load(stream);
                return document
                    .Descendants(SpreadsheetNs + "t")
                    .Select(element => element.Value)
                    .ToList();
            }

            private static List<string> ParseRow(XElement row, IReadOnlyList<string> sharedStrings)
            {
                var values = new List<string>();
                foreach (var cell in row.Elements(SpreadsheetNs + "c"))
                {
                    var cellRef = cell.Attribute("r")?.Value;
                    var columnIndex = ColumnNameToIndex(cellRef);
                    while (values.Count <= columnIndex)
                    {
                        values.Add(string.Empty);
                    }

                    values[columnIndex] = ReadCell(cell, sharedStrings);
                }

                return values;
            }

            private static string ReadCell(XElement cell, IReadOnlyList<string> sharedStrings)
            {
                var cellType = cell.Attribute("t")?.Value;
                if (cellType == "inlineStr")
                {
                    return cell.Descendants(SpreadsheetNs + "t").FirstOrDefault()?.Value ?? string.Empty;
                }

                if (cellType == "s" &&
                    int.TryParse(cell.Element(SpreadsheetNs + "v")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                    index >= 0 &&
                    index < sharedStrings.Count)
                {
                    return sharedStrings[index];
                }

                return cell.Element(SpreadsheetNs + "v")?.Value ?? string.Empty;
            }

            private static int ColumnNameToIndex(string? cellReference)
            {
                if (string.IsNullOrWhiteSpace(cellReference))
                {
                    return 0;
                }

                var columnName = new string(cellReference.TakeWhile(char.IsLetter).ToArray());
                if (string.IsNullOrWhiteSpace(columnName))
                {
                    return 0;
                }

                var sum = 0;
                foreach (var ch in columnName.ToUpperInvariant())
                {
                    sum *= 26;
                    sum += ch - 'A' + 1;
                }

                return sum - 1;
            }

            private static string BuildCellReference(int columnNumber, int rowNumber)
            {
                var columnName = BuildColumnName(columnNumber);
                return $"{columnName}{rowNumber}";
            }

            private static string BuildColumnName(int columnNumber)
            {
                var index = columnNumber;
                var chars = new Stack<char>();
                while (index > 0)
                {
                    index--;
                    chars.Push((char)('A' + (index % 26)));
                    index /= 26;
                }

                return new string(chars.ToArray());
            }
            private static HashSet<string> ParseSkipFindings(IEnumerable<Dictionary<string, string>> rows)
            {
                var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows)
                {
                    var source = TryGetSkipSourceText(row);
                    if (string.IsNullOrWhiteSpace(source))
                    {
                        continue;
                    }

                    var normalized = Preprocess(source).NormalizedText;
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        values.Add(normalized);
                    }
                }

                return values;
            }

            private static string TryGetSkipSourceText(Dictionary<string, string> row)
            {
                var candidates = new[]
                {
                    "SkipText",
                    "MatchedValue",
                    "MatchValue",
                    "Match",
                    "Finding",
                    "FindingText",
                    "Text",
                    "Value",
                    "StandardDescription"
                };

                foreach (var key in candidates)
                {
                    if (row.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                return row.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
            }
        }

        private static class XlsxReader
        {
            private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            private static readonly XNamespace RelationshipsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
            private static readonly XNamespace DocumentRelationshipsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

            public static Dictionary<string, List<Dictionary<string, string>>> LoadSheets(string path)
            {
                using var archive = ZipFile.OpenRead(path);
                var sharedStrings = LoadSharedStrings(archive);
                var workbook = XDocument.Load(GetEntryStream(archive, "xl/workbook.xml"));
                var rels = XDocument.Load(GetEntryStream(archive, "xl/_rels/workbook.xml.rels"));

                var relMap = rels
                    .Descendants(RelationshipsNs + "Relationship")
                    .ToDictionary(
                        rel => rel.Attribute("Id")?.Value ?? string.Empty,
                        rel => rel.Attribute("Target")?.Value?.TrimStart('/') ?? string.Empty);

                var sheets = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
                foreach (var sheet in workbook.Descendants(SpreadsheetNs + "sheet"))
                {
                    var name = sheet.Attribute("name")?.Value ?? string.Empty;
                    var relId = sheet.Attribute(DocumentRelationshipsNs + "id")?.Value ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(relId))
                    {
                        continue;
                    }

                    if (!relMap.TryGetValue(relId, out var target) || string.IsNullOrWhiteSpace(target))
                    {
                        continue;
                    }

                    var sheetDocument = XDocument.Load(GetEntryStream(archive, target));
                    sheets[name] = ParseSheet(sheetDocument, sharedStrings);
                }

                return sheets;
            }

            private static Stream GetEntryStream(ZipArchive archive, string name)
            {
                var entry = archive.GetEntry(name) ?? throw new InvalidOperationException($"Missing workbook entry: {name}");
                return entry.Open();
            }

            private static List<Dictionary<string, string>> ParseSheet(XDocument sheetDocument, IReadOnlyList<string> sharedStrings)
            {
                var rows = sheetDocument
                    .Descendants(SpreadsheetNs + "row")
                    .Select(row => ParseRow(row, sharedStrings))
                    .ToList();

                if (rows.Count == 0)
                {
                    return new List<Dictionary<string, string>>();
                }

                var header = rows[0];
                var dataRows = new List<Dictionary<string, string>>();
                for (var i = 1; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (var col = 0; col < header.Count; col++)
                    {
                        var key = header[col];
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            continue;
                        }

                        var value = col < row.Count ? row[col] : string.Empty;
                        dict[key.Trim()] = value ?? string.Empty;
                    }

                    if (dict.Values.All(string.IsNullOrWhiteSpace))
                    {
                        continue;
                    }

                    dataRows.Add(dict);
                }

                return dataRows;
            }

            private static List<string> ParseRow(XElement row, IReadOnlyList<string> sharedStrings)
            {
                var values = new List<string>();
                foreach (var cell in row.Elements(SpreadsheetNs + "c"))
                {
                    var cellRef = cell.Attribute("r")?.Value;
                    var columnIndex = ColumnNameToIndex(cellRef);
                    while (values.Count <= columnIndex)
                    {
                        values.Add(string.Empty);
                    }

                    values[columnIndex] = ReadCell(cell, sharedStrings);
                }

                return values;
            }

            private static string ReadCell(XElement cell, IReadOnlyList<string> sharedStrings)
            {
                var cellType = cell.Attribute("t")?.Value;
                if (cellType == "inlineStr")
                {
                    return cell.Descendants(SpreadsheetNs + "t").FirstOrDefault()?.Value ?? string.Empty;
                }

                if (cellType == "s"
                    && int.TryParse(cell.Element(SpreadsheetNs + "v")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    && index >= 0
                    && index < sharedStrings.Count)
                {
                    return sharedStrings[index];
                }

                return cell.Element(SpreadsheetNs + "v")?.Value ?? string.Empty;
            }

            private static IReadOnlyList<string> LoadSharedStrings(ZipArchive archive)
            {
                var entry = archive.GetEntry("xl/sharedStrings.xml");
                if (entry == null)
                {
                    return Array.Empty<string>();
                }

                using var stream = entry.Open();
                var document = XDocument.Load(stream);
                return document
                    .Descendants(SpreadsheetNs + "t")
                    .Select(element => element.Value)
                    .ToList();
            }

            private static int ColumnNameToIndex(string? cellReference)
            {
                if (string.IsNullOrWhiteSpace(cellReference))
                {
                    return 0;
                }

                var columnName = new string(cellReference.TakeWhile(char.IsLetter).ToArray());
                if (string.IsNullOrWhiteSpace(columnName))
                {
                    return 0;
                }

                var sum = 0;
                foreach (var ch in columnName.ToUpperInvariant())
                {
                    sum *= 26;
                    sum += ch - 'A' + 1;
                }

                return sum - 1;
            }
        }
    }
}














