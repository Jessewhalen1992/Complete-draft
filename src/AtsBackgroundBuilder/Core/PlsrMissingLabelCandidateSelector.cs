using System;
using System.Collections.Generic;

namespace AtsBackgroundBuilder.Core
{
    internal sealed class PlsrMissingLabelCandidateSelectionInput
    {
        public string? PreferredCandidateId { get; set; }
        public IEnumerable<string> IndexedCandidateIds { get; set; } = Array.Empty<string>();
    }

    internal sealed class PlsrMissingLabelCandidateSelectionResult
    {
        public List<string> OrderedCandidateIds { get; } = new List<string>();
        public bool HasCandidates => OrderedCandidateIds.Count > 0;
    }

    internal static class PlsrMissingLabelCandidateSelector
    {
        public static PlsrMissingLabelCandidateSelectionResult Select(PlsrMissingLabelCandidateSelectionInput input)
        {
            var result = new PlsrMissingLabelCandidateSelectionResult();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddCandidate(result.OrderedCandidateIds, seen, input?.PreferredCandidateId);
            if (input?.IndexedCandidateIds != null)
            {
                foreach (var candidateId in input.IndexedCandidateIds)
                {
                    AddCandidate(result.OrderedCandidateIds, seen, candidateId);
                }
            }

            return result;
        }

        private static void AddCandidate(List<string> destination, HashSet<string> seen, string? candidateId)
        {
            if (string.IsNullOrWhiteSpace(candidateId))
            {
                return;
            }

            if (seen.Add(candidateId))
            {
                destination.Add(candidateId);
            }
        }
    }
}
