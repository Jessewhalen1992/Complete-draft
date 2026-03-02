using System;
using System.Collections.Generic;

namespace AtsBackgroundBuilder.Core
{
    internal enum AtsBuildOptionGroup
    {
        Sections,
        ShapeFiles,
        Plsr,
    }

    internal enum AtsBuildOptionKey
    {
        IncludeAtsFabric,
        IncludeLsds,
        AllowMultiQuarterDispositions,
        IncludeQuarterSectionLabels,
        IncludeDispoLinework,
        IncludeDispoLabels,
        IncludeCrownReservations,
        IncludeP3Shapes,
        IncludeCompassMapping,
        CheckPlsr,
        IncludeSurfaceImpact,
    }

    internal sealed class AtsBuildOptionDefinition
    {
        private readonly Func<Config, bool>? _defaultResolver;

        public AtsBuildOptionDefinition(
            AtsBuildOptionKey key,
            AtsBuildOptionGroup group,
            string label,
            Func<Config, bool>? defaultResolver = null)
        {
            Key = key;
            Group = group;
            Label = label ?? string.Empty;
            _defaultResolver = defaultResolver;
        }

        public AtsBuildOptionKey Key { get; }
        public AtsBuildOptionGroup Group { get; }
        public string Label { get; }

        public bool ResolveDefaultChecked(Config? config)
        {
            if (_defaultResolver == null)
            {
                return false;
            }

            return _defaultResolver(config ?? new Config());
        }
    }

    internal static class AtsBuildOptionCatalog
    {
        private static readonly IReadOnlyList<AtsBuildOptionGroup> OrderedGroups = new[]
        {
            AtsBuildOptionGroup.Sections,
            AtsBuildOptionGroup.ShapeFiles,
            AtsBuildOptionGroup.Plsr,
        };

        private static readonly IReadOnlyList<AtsBuildOptionDefinition> OrderedOptions = new[]
        {
            new AtsBuildOptionDefinition(AtsBuildOptionKey.IncludeAtsFabric, AtsBuildOptionGroup.Sections, "ATS Fabric"),
            new AtsBuildOptionDefinition(AtsBuildOptionKey.IncludeLsds, AtsBuildOptionGroup.Sections, "LSDs"),
            new AtsBuildOptionDefinition(
                AtsBuildOptionKey.AllowMultiQuarterDispositions,
                AtsBuildOptionGroup.Sections,
                "1/4 Definitions"),
            new AtsBuildOptionDefinition(AtsBuildOptionKey.IncludeQuarterSectionLabels, AtsBuildOptionGroup.Sections, "1/4 SEC. Labels"),
            new AtsBuildOptionDefinition(AtsBuildOptionKey.IncludeDispoLinework, AtsBuildOptionGroup.ShapeFiles, "Dispositions"),
            new AtsBuildOptionDefinition(AtsBuildOptionKey.IncludeDispoLabels, AtsBuildOptionGroup.ShapeFiles, "Disposition Labels"),
            new AtsBuildOptionDefinition(AtsBuildOptionKey.IncludeCrownReservations, AtsBuildOptionGroup.ShapeFiles, "CLRs"),
            new AtsBuildOptionDefinition(AtsBuildOptionKey.IncludeP3Shapes, AtsBuildOptionGroup.ShapeFiles, "P3 Water"),
            new AtsBuildOptionDefinition(AtsBuildOptionKey.IncludeCompassMapping, AtsBuildOptionGroup.ShapeFiles, "Compass Mapping"),
            new AtsBuildOptionDefinition(AtsBuildOptionKey.CheckPlsr, AtsBuildOptionGroup.Plsr, "PLSR Check"),
            new AtsBuildOptionDefinition(AtsBuildOptionKey.IncludeSurfaceImpact, AtsBuildOptionGroup.Plsr, "Surface Impact"),
        };

        public static IReadOnlyList<AtsBuildOptionGroup> Groups => OrderedGroups;
        public static IReadOnlyList<AtsBuildOptionDefinition> Options => OrderedOptions;

        public static string GetGroupTitle(AtsBuildOptionGroup group)
        {
            return group switch
            {
                AtsBuildOptionGroup.Sections => "SECTIONS",
                AtsBuildOptionGroup.ShapeFiles => "SHAPE FILES",
                AtsBuildOptionGroup.Plsr => "PLSR",
                _ => string.Empty,
            };
        }
    }
}
