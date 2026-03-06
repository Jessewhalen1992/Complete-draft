using System;
using System.Collections.Generic;

namespace AtsBackgroundBuilder.Core
{
    internal static class PlsrMissingLabelSuppressionPolicy
    {
        public static string BuildNonStandardLayerCandidateKey(string quarterKey, string normalizedDispNum)
        {
            if (string.IsNullOrWhiteSpace(quarterKey) || string.IsNullOrWhiteSpace(normalizedDispNum))
            {
                return string.Empty;
            }

            return quarterKey.Trim().ToUpperInvariant() + "|" + normalizedDispNum.Trim().ToUpperInvariant();
        }

        public static bool ShouldSuppressMissingLabel(
            ISet<string>? nonStandardLayerDispCandidates,
            string quarterKey,
            string normalizedDispNum)
        {
            if (nonStandardLayerDispCandidates == null || nonStandardLayerDispCandidates.Count == 0)
            {
                return false;
            }

            var candidateKey = BuildNonStandardLayerCandidateKey(quarterKey, normalizedDispNum);
            return candidateKey.Length > 0 && nonStandardLayerDispCandidates.Contains(candidateKey);
        }
    }
}