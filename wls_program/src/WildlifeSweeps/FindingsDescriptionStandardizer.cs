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
        private static readonly RegexOptions RuleRegexOptions = RegexOptions.IgnoreCase | RegexOptions.Compiled;
        private static readonly Regex LeadingPhotoRegex = new Regex(@"^\s*(#?\d+)\b", RegexOptions.Compiled);
        private static readonly Regex ParentheticalPhotoRegex = new Regex(@"\((\d+)\)", RegexOptions.Compiled);
        private static readonly Regex HashPhotoRegex = new Regex(@"#(\d+)", RegexOptions.Compiled);
        private static readonly Regex PunctuationRegex = new Regex(@"[_\W]+", RegexOptions.Compiled);
        private static readonly Regex StandalonePossessiveRegex = new Regex(@"\b['’`´]?\s*s\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MultiSpaceRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly TextInfo TitleCase = CultureInfo.CurrentCulture.TextInfo;

        private readonly IReadOnlyList<RecognitionRule> _regexRules;
        private readonly IReadOnlyList<RecognitionRule> _keywordRules;
        private readonly IReadOnlyList<string> _speciesOptions;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _findingTypesBySpecies;
        private readonly IReadOnlyList<string> _standardDescriptions;
        private readonly Dictionary<string, (string Species, string FindingType)> _standardDescriptionMap;
        private readonly HashSet<(string Species, string FindingType)> _validPairs;
        private readonly HashSet<string> _skipNormalizedFindings;
        private readonly string _customMappingsPath;
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

            _customMappingsPath = CustomMapping.GetDefaultPath();
            _customMappings = CustomMapping.Load(_customMappingsPath, logWarning);

            _unparsedFindingsPath = GetUnparsedFindingsPath();
        }

        internal IReadOnlyList<string> SpeciesOptions => _speciesOptions;

        internal IReadOnlyDictionary<string, IReadOnlyList<string>> FindingTypesBySpecies => _findingTypesBySpecies;

        internal IReadOnlyList<string> StandardDescriptionOptions => _standardDescriptions;

        internal bool IsValidPair(string? species, string? findingType)
        {
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

            description = $"{TitleCase.ToTitleCase(species.Trim())} {TitleCase.ToTitleCase(findingType.Trim())}";
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
                if (!IsValidPair(match.Species, match.FindingType))
                {
                    var promptResult = promptForUnmapped(new PromptContext(preprocess.CleanedOriginal, preprocess.NormalizedText));
                    var resolved = ResolvePromptResult(preprocess, promptResult, promptForUnmapped);
                    results.Add(resolved);
                    continue;
                }

                results.Add(new StandardizedFinding(preprocess, match.Species, match.FindingType, match.StandardDescription, match.Source));
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

            var species = NormalizeOtherValue(promptResult.Species);
            var findingType = NormalizeOtherValue(promptResult.FindingType);
            var description = NormalizeOtherValue(promptResult.StandardDescription);

            if (string.IsNullOrWhiteSpace(species) || string.IsNullOrWhiteSpace(findingType))
            {
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
                else if (!TryResolveStandardDescription(description, out species, out findingType))
                {
                    var retry = promptForUnmapped(new PromptContext(preprocess.CleanedOriginal, preprocess.NormalizedText));
                    return ResolvePromptResult(preprocess, retry, promptForUnmapped);
                }
            }

            if (!IsValidPair(species, findingType) && !IsOtherValue(species) && !IsOtherValue(findingType))
            {
                var retry = promptForUnmapped(new PromptContext(preprocess.CleanedOriginal, preprocess.NormalizedText));
                return ResolvePromptResult(preprocess, retry, promptForUnmapped);
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                if (IsOtherValue(species) || IsOtherValue(findingType))
                {
                    description = OtherValue;
                }
                else
                {
                    TryGetDefaultDescriptionForPair(species, findingType, out description);
                }
            }

            var result = new StandardizedFinding(preprocess, species, findingType, description, StandardizationSource.Prompt);
            if (promptResult.RememberMapping)
            {
                SaveCustomMapping(preprocess.NormalizedText, result);
            }

            return result;
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

        private void SaveCustomMapping(string normalizedText, StandardizedFinding result)
        {
            if (string.IsNullOrWhiteSpace(normalizedText)
                || string.IsNullOrWhiteSpace(result.Species)
                || string.IsNullOrWhiteSpace(result.FindingType)
                || string.IsNullOrWhiteSpace(result.StandardDescription))
            {
                return;
            }

            _customMappings[normalizedText] = new CustomMapping(normalizedText, result.Species, result.FindingType, result.StandardDescription);
            CustomMapping.Save(_customMappingsPath, _customMappings.Values);
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
            species = resolved.Species;
            findingType = resolved.FindingType;
            return !string.IsNullOrWhiteSpace(species) && !string.IsNullOrWhiteSpace(findingType);
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

            public IReadOnlyList<RecognitionRule> RegexRules { get; private init; } = Array.Empty<RecognitionRule>();

            public IReadOnlyList<RecognitionRule> KeywordRules { get; private init; } = Array.Empty<RecognitionRule>();

            public IReadOnlyList<string> SpeciesOptions { get; private init; } = Array.Empty<string>();

            public IReadOnlyDictionary<string, IReadOnlyList<string>> FindingTypesBySpecies { get; private init; }
                = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

            public IReadOnlyList<string> StandardDescriptions { get; private init; } = Array.Empty<string>();

            public Dictionary<string, (string Species, string FindingType)> StandardDescriptionMap { get; private init; }
                = new Dictionary<string, (string Species, string FindingType)>(StringComparer.OrdinalIgnoreCase);

            public HashSet<(string Species, string FindingType)> ValidPairs { get; private init; }
                = new HashSet<(string Species, string FindingType)>();

            public HashSet<string> SkipNormalizedFindings { get; private init; }
                = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public static LookupWorkbook Load(string? path, Action<string>? logWarning)
            {
                var resolvedPath = ResolveLookupPath(path, logWarning);
                if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                {
                    logWarning?.Invoke("Findings lookup workbook not found. Provide a valid path in settings.");
                    return new LookupWorkbook();
                }

                try
                {
                    var workbook = ReadWorkbook(resolvedPath);
                    return workbook;
                }
                catch (Exception ex)
                {
                    logWarning?.Invoke($"Failed to load findings lookup workbook: {ex.Message}");
                    return new LookupWorkbook();
                }
            }

            private static string ResolveLookupPath(string? configuredPath, Action<string>? logWarning)
            {
                if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                {
                    return configuredPath;
                }

                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    logWarning?.Invoke($"Configured findings lookup not found: {configuredPath}. Falling back to plugin folder.");
                }

                var defaultPath = ResolveDefaultLookupPath();
                if (!string.IsNullOrWhiteSpace(defaultPath))
                {
                    return defaultPath;
                }

                return string.Empty;
            }

            private static string ResolveDefaultLookupPath()
            {
                const string lookupFileName = "wildlife_parsing_codex_lookup.xlsx";

                // Prefer the folder containing WildlifeSweeps.dll first.
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

                foreach (var directory in candidateDirectories)
                {
                    var candidate = Path.Combine(directory, lookupFileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                return string.Empty;
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

                    species = species.Trim();
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
                    StandardDescriptionMap = descriptionMap,
                    ValidPairs = validPairs,
                    SkipNormalizedFindings = skipNormalizedFindings
                };
            }

            private static RecognitionRule? CreateRule(Dictionary<string, string> row, bool isRegex)
            {
                if (!row.TryGetValue("Species", out var species) || string.IsNullOrWhiteSpace(species))
                {
                    return null;
                }

                if (!row.TryGetValue("FindingType", out var findingType) || string.IsNullOrWhiteSpace(findingType))
                {
                    return null;
                }

                if (!row.TryGetValue(isRegex ? "Regex" : "Keyword", out var pattern) || string.IsNullOrWhiteSpace(pattern))
                {
                    return null;
                }

                row.TryGetValue("StandardDescription", out var description);
                row.TryGetValue("Priority", out var priorityRaw);
                _ = int.TryParse(priorityRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority);

                species = species.Trim();
                findingType = findingType.Trim();
                description = description?.Trim() ?? string.Empty;

                if (!isRegex)
                {
                    pattern = NormalizeKeyword(pattern);
                }

                return new RecognitionRule(
                    priority,
                    species,
                    findingType,
                    description,
                    pattern.Trim(),
                    isRegex,
                    !isRegex,
                    isRegex ? StandardizationSource.RegexRule : StandardizationSource.KeywordRule);
            }

            private static string NormalizeKeyword(string keyword)
            {
                var normalized = keyword.Trim().ToLowerInvariant();
                normalized = PunctuationRegex.Replace(normalized, " ");
                normalized = MultiSpaceRegex.Replace(normalized, " ").Trim();
                return normalized;
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
