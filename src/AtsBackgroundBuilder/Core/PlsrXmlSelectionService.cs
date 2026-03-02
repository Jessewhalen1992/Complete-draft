using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AtsBackgroundBuilder.Core
{
    internal enum PlsrXmlPathValidationFailure
    {
        None,
        EmptySelection,
        NoValidFiles,
    }

    internal static class PlsrXmlSelectionService
    {
        public const string DialogFilter = "PLSR XML (*.xml)|*.xml|All files (*.*)|*.*";
        public const string DialogTitle = "Select PLSR/Surface XML file(s)";
        public const string RequiredSelectionMessage = "Check PLSR / Surface Impact requires at least one XML file.";

        public static bool RequiresXml(AtsBuildInput input)
        {
            if (input == null)
            {
                return false;
            }

            return input.CheckPlsr || input.IncludeSurfaceImpact;
        }

        public static bool TryGetValidPaths(
            IEnumerable<string>? candidatePaths,
            out List<string> validPaths,
            out PlsrXmlPathValidationFailure failure)
        {
            validPaths = new List<string>();
            failure = PlsrXmlPathValidationFailure.None;

            if (candidatePaths == null)
            {
                failure = PlsrXmlPathValidationFailure.EmptySelection;
                return false;
            }

            var rawPaths = candidatePaths.ToList();
            if (rawPaths.Count == 0)
            {
                failure = PlsrXmlPathValidationFailure.EmptySelection;
                return false;
            }

            validPaths = rawPaths
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (validPaths.Count == 0)
            {
                failure = PlsrXmlPathValidationFailure.NoValidFiles;
                return false;
            }

            return true;
        }
    }
}
