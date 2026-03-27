using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WildlifeSweeps
{
    public enum WildlifeFindingZone
    {
        Proposed,
        Buffer100,
        Outside100
    }

    public sealed record FindingRow(
        WildlifeFindingZone Zone,
        string Species,
        string FindingType,
        string StandardDescription);

    public sealed record WildlifeFeatureFlags(
        bool OccupiedNest,
        bool OccupiedDen,
        bool Hibernacula,
        bool MineralLick);

    public static class WildlifeFindingsStatementBuilder
    {
        private static readonly HashSet<string> IncidentalTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "audible",
            "call",
            "heard",
            "observation",
            "observations",
            "sighting",
            "visual"
        };

        private static readonly HashSet<string> SignTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "antlers",
            "bed",
            "beds",
            "browse",
            "browse / feeding sign",
            "browse/feeding sign",
            "burrow",
            "burrows",
            "cavity",
            "cavities",
            "den",
            "dens",
            "dig",
            "digs",
            "droppings",
            "feeding",
            "feeding sign",
            "feather",
            "feathers",
            "fur",
            "hair",
            "hair / fur",
            "hair/fur",
            "lodge",
            "lodges",
            "nest",
            "nests",
            "pellets",
            "scat",
            "scat / droppings",
            "scat/droppings",
            "sign",
            "track",
            "tracks",
            "trail",
            "trails",
            "wallow",
            "wallows"
        };

        public static string Build(
            IReadOnlyCollection<FindingRow> rows,
            WildlifeFeatureFlags flags,
            int findingsPageNumber)
        {
            if (findingsPageNumber <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(findingsPageNumber), "Findings page number must be positive.");
            }

            var safeRows = (rows ?? Array.Empty<FindingRow>())
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.StandardDescription))
                .ToList();

            var includeIncidentalObservations = safeRows.Any(IsIncidentalRow);
            var sentences = new List<string>
            {
                BuildFeatureSentence(flags)
            };

            var insideRows = safeRows
                .Where(row => row.Zone == WildlifeFindingZone.Proposed || row.Zone == WildlifeFindingZone.Buffer100)
                .ToList();
            var outsideRows = safeRows
                .Where(row => row.Zone == WildlifeFindingZone.Outside100)
                .ToList();

            if (insideRows.Count > 0)
            {
                sentences.Add(BuildInsideSentence(insideRows, includeIncidentalObservations));
            }
            else if (outsideRows.Count > 0)
            {
                sentences.Add(BuildNoInsideSentence(includeIncidentalObservations));
            }
            else
            {
                sentences.Add(BuildNoWildlifeSentence(includeIncidentalObservations));
            }

            var outsideSentence = BuildOutsideSentence(insideRows, outsideRows, includeIncidentalObservations);
            if (!string.IsNullOrWhiteSpace(outsideSentence))
            {
                sentences.Add(outsideSentence);
            }

            sentences.Add($"The findings list can be found on page {findingsPageNumber.ToString(CultureInfo.InvariantCulture)}.");
            return string.Join(" ", sentences.Where(sentence => !string.IsNullOrWhiteSpace(sentence)));
        }

        private static string BuildFeatureSentence(WildlifeFeatureFlags flags)
        {
            var present = new List<string>();
            if (flags.OccupiedNest)
            {
                present.Add("occupied nests");
            }

            if (flags.OccupiedDen)
            {
                present.Add("occupied dens");
            }

            if (flags.Hibernacula)
            {
                present.Add("hibernacula");
            }

            if (flags.MineralLick)
            {
                present.Add("natural mineral licks");
            }

            if (present.Count == 0)
            {
                return "No occupied nests, occupied dens, hibernacula, natural mineral licks, or other confirmed key wildlife features requiring a 100 m setback were identified during the sweep.";
            }

            var subject = JoinWithOxfordComma(present);
            return $"{CapitalizeFirst(subject)} were identified during the sweep and require a 100 m setback.";
        }

        private static string BuildInsideSentence(
            IReadOnlyList<FindingRow> insideRows,
            bool includeIncidentalObservations)
        {
            var descriptor = BuildDescriptor(includeIncidentalObservations);
            var groups = BuildNarrativeGroups(insideRows);
            if (groups.Count == 0)
            {
                return BuildNoInsideSentence(includeIncidentalObservations);
            }

            var lead = $"{CapitalizeFirst(descriptor)} documented within the proposed footprint and 100 m buffer";
            if (!ShouldUsePrimarily(groups, insideRows.Count))
            {
                return $"{lead} included {JoinWithOxfordComma(groups.Select(group => group.Label))}.";
            }

            if (groups.Count == 1)
            {
                return $"{lead} consisted primarily of {groups[0].Label}.";
            }

            return $"{lead} consisted primarily of {groups[0].Label}, with additional {JoinWithOxfordComma(groups.Skip(1).Select(group => group.Label))}.";
        }

        private static string? BuildOutsideSentence(
            IReadOnlyList<FindingRow> insideRows,
            IReadOnlyList<FindingRow> outsideRows,
            bool includeIncidentalObservations)
        {
            if (outsideRows.Count == 0)
            {
                return null;
            }

            var descriptor = BuildDescriptor(includeIncidentalObservations);
            var documentedVerb = includeIncidentalObservations ? "were documented" : "was documented";
            var insideKeys = BuildComparisonKeySet(insideRows);
            var novelOutsideRows = outsideRows
                .Where(row => !insideKeys.Contains(BuildComparisonKey(row)))
                .ToList();
            var novelLabels = BuildNarrativeGroups(novelOutsideRows)
                .Select(group => group.Label)
                .ToList();

            if (novelLabels.Count == 0)
            {
                return $"Additional {descriptor} of similar types {documentedVerb} outside the 100 m buffer.";
            }

            var including = JoinWithOxfordComma(novelLabels);
            if (insideKeys.Count == 0)
            {
                return $"Additional {descriptor} {documentedVerb} outside the 100 m buffer, including {including}.";
            }

            var similarCount = outsideRows.Count - novelOutsideRows.Count;
            if (similarCount > 0)
            {
                return $"Additional {descriptor} of similar types {documentedVerb} outside the 100 m buffer, including {including}.";
            }

            return $"Additional {descriptor} {documentedVerb} outside the 100 m buffer, including {including}.";
        }

        private static string BuildNoInsideSentence(bool includeIncidentalObservations)
        {
            return includeIncidentalObservations
                ? "No wildlife sign or incidental observations were documented within the proposed footprint and 100 m buffer."
                : "No wildlife sign was documented within the proposed footprint and 100 m buffer.";
        }

        private static string BuildNoWildlifeSentence(bool includeIncidentalObservations)
        {
            return includeIncidentalObservations
                ? "No wildlife sign or incidental observations were documented during the sweep."
                : "No wildlife sign was documented during the sweep.";
        }

        private static bool ShouldUsePrimarily(IReadOnlyList<NarrativeGroup> groups, int insideRowCount)
        {
            if (groups.Count == 0 || insideRowCount <= 0)
            {
                return false;
            }

            if (groups.Count == 1)
            {
                return true;
            }

            var topCount = groups[0].Count;
            var secondCount = groups[1].Count;
            return topCount >= Math.Ceiling(insideRowCount * 0.4) || topCount >= secondCount * 2;
        }

        private static List<NarrativeGroup> BuildNarrativeGroups(IReadOnlyList<FindingRow> rows)
        {
            var groups = new List<NarrativeGroup>();
            var speciesGroups = rows
                .GroupBy(row => NormalizeToken(row.Species))
                .OrderBy(group => string.IsNullOrEmpty(group.Key) ? 1 : 0)
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList();

            foreach (var speciesGroup in speciesGroups)
            {
                if (string.IsNullOrEmpty(speciesGroup.Key))
                {
                    groups.AddRange(BuildExactPhraseGroups(speciesGroup));
                    continue;
                }

                var speciesRows = speciesGroup.ToList();
                var distinctTypes = speciesRows
                    .Select(row => CanonicalizeFindingType(row.FindingType, row.StandardDescription))
                    .Where(type => !string.IsNullOrEmpty(type))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (distinctTypes.Count <= 1)
                {
                    groups.Add(BuildExactPhraseGroup(speciesRows));
                    continue;
                }

                if (TryBuildCollapsedNarrativeLabel(speciesRows, out var collapsedLabel))
                {
                    groups.Add(new NarrativeGroup(
                        collapsedLabel,
                        speciesRows.Count,
                        speciesRows.Select(BuildComparisonKey).ToHashSet(StringComparer.Ordinal)));
                    continue;
                }

                groups.AddRange(BuildExactPhraseGroups(speciesRows));
            }

            return groups
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.Label, StringComparer.Ordinal)
                .ToList();
        }

        private static IEnumerable<NarrativeGroup> BuildExactPhraseGroups(IEnumerable<FindingRow> rows)
        {
            return rows
                .GroupBy(row => NormalizeToken(row.StandardDescription))
                .Where(group => !string.IsNullOrEmpty(group.Key))
                .Select(group => BuildExactPhraseGroup(group.ToList()));
        }

        private static NarrativeGroup BuildExactPhraseGroup(IReadOnlyList<FindingRow> rows)
        {
            var preferred = rows
                .Select(row => row.StandardDescription?.Trim())
                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
                ?? string.Empty;
            return new NarrativeGroup(
                ToNarrativeLabel(preferred),
                rows.Count,
                rows.Select(BuildComparisonKey).ToHashSet(StringComparer.Ordinal));
        }

        private static bool TryBuildCollapsedNarrativeLabel(
            IReadOnlyList<FindingRow> rows,
            out string label)
        {
            label = string.Empty;
            if (rows.Count == 0)
            {
                return false;
            }

            var species = NormalizeNarrativeSpecies(rows[0].Species);
            if (string.IsNullOrEmpty(species))
            {
                return false;
            }

            var types = rows
                .Select(row => CanonicalizeFindingType(row.FindingType, row.StandardDescription))
                .Where(type => !string.IsNullOrEmpty(type))
                .ToList();
            if (types.Count <= 1)
            {
                return false;
            }

            if (types.All(IsSignType))
            {
                label = $"signs of {species}";
                return true;
            }

            if (types.All(IsIncidentalType))
            {
                label = $"incidental observations of {species}";
                return true;
            }

            if (types.All(type => IsSignType(type) || IsIncidentalType(type)))
            {
                label = $"signs and incidental observations of {species}";
                return true;
            }

            return false;
        }

        private static HashSet<string> BuildComparisonKeySet(IEnumerable<FindingRow> rows)
        {
            return rows
                .Select(BuildComparisonKey)
                .Where(key => !string.IsNullOrEmpty(key))
                .ToHashSet(StringComparer.Ordinal);
        }

        private static string BuildComparisonKey(FindingRow row)
        {
            var species = NormalizeToken(row.Species);
            var findingType = CanonicalizeFindingType(row.FindingType, row.StandardDescription);
            if (!string.IsNullOrEmpty(species) && !string.IsNullOrEmpty(findingType))
            {
                return species + "|" + findingType;
            }

            return NormalizeToken(row.StandardDescription);
        }

        private static string BuildDescriptor(bool includeIncidentalObservations)
        {
            return includeIncidentalObservations
                ? "wildlife sign and incidental observations"
                : "wildlife sign";
        }

        private static bool IsIncidentalRow(FindingRow row)
        {
            var findingType = CanonicalizeFindingType(row.FindingType, row.StandardDescription);
            return IsIncidentalType(findingType);
        }

        private static bool IsIncidentalType(string? findingType)
        {
            return !string.IsNullOrWhiteSpace(findingType) && IncidentalTypes.Contains(findingType);
        }

        private static bool IsSignType(string? findingType)
        {
            return !string.IsNullOrWhiteSpace(findingType) && SignTypes.Contains(findingType);
        }

        private static string CanonicalizeFindingType(string? findingType, string? fallbackDescription = null)
        {
            var normalized = NormalizeToken(findingType);
            if (!string.IsNullOrEmpty(normalized))
            {
                normalized = normalized.Replace("observed", "observation", StringComparison.Ordinal)
                    .Replace("heard call", "call", StringComparison.Ordinal)
                    .Replace("visual observation", "visual", StringComparison.Ordinal);
            }

            if (string.IsNullOrEmpty(normalized) && !string.IsNullOrWhiteSpace(fallbackDescription))
            {
                normalized = InferFindingTypeFromDescription(fallbackDescription!);
            }

            return normalized;
        }

        private static string InferFindingTypeFromDescription(string description)
        {
            var normalizedDescription = NormalizeToken(description);
            if (string.IsNullOrEmpty(normalizedDescription))
            {
                return string.Empty;
            }

            var orderedSuffixes = new[]
            {
                "abandoned / inactive nest",
                "abandoned/inactive nest",
                "occupied nest",
                "occupied den",
                "natural mineral lick",
                "mineral lick",
                "browse / feeding sign",
                "browse/feeding sign",
                "feeding sign",
                "scat / droppings",
                "scat/droppings",
                "hair / fur",
                "hair/fur",
                "audible",
                "sighting",
                "visual",
                "tracks",
                "track",
                "scat",
                "droppings",
                "browse",
                "trail",
                "trails",
                "burrow",
                "burrows",
                "cavity",
                "cavities",
                "den",
                "dens",
                "nest",
                "nests",
                "lodge",
                "lodges",
                "wallow",
                "wallows",
                "sign"
            };

            foreach (var suffix in orderedSuffixes)
            {
                if (normalizedDescription.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return suffix;
                }
            }

            return string.Empty;
        }

        public static string JoinWithOxfordComma(IEnumerable<string> values)
        {
            var items = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList();

            return items.Count switch
            {
                0 => string.Empty,
                1 => items[0],
                2 => items[0] + " and " + items[1],
                _ => string.Join(", ", items.Take(items.Count - 1)) + ", and " + items[^1]
            };
        }

        private static string CapitalizeFirst(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var trimmed = text.Trim();
            return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        }

        private static string NormalizeNarrativeSpecies(string? species)
        {
            return NormalizeToken(species);
        }

        private static string ToNarrativeLabel(string? value)
        {
            return NormalizeToken(value);
        }

        private static string NormalizeToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new List<char>(value.Length);
            var previousWasWhitespace = false;
            foreach (var character in value.Trim())
            {
                var current = char.IsWhiteSpace(character) ? ' ' : char.ToLowerInvariant(character);
                if (current == ' ')
                {
                    if (previousWasWhitespace)
                    {
                        continue;
                    }

                    previousWasWhitespace = true;
                    builder.Add(current);
                    continue;
                }

                previousWasWhitespace = false;
                builder.Add(current);
            }

            return new string(builder.ToArray()).Trim();
        }

        private sealed record NarrativeGroup(
            string Label,
            int Count,
            HashSet<string> ComparisonKeys);
    }
}
