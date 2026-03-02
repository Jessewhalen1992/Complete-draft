using System;
using System.Collections.Generic;

namespace AtsBackgroundBuilder.Core
{
    internal readonly struct ReviewDecisionEntry
    {
        internal ReviewDecisionEntry(Guid issueId, string decision)
        {
            IssueId = issueId;
            Decision = decision ?? string.Empty;
        }

        internal Guid IssueId { get; }
        internal string Decision { get; }
    }

    internal static class ReviewDecisionService
    {
        internal static HashSet<Guid> ResolveAcceptedIssueIds(
            bool applyRequested,
            IEnumerable<ReviewDecisionEntry> decisions)
        {
            var accepted = new HashSet<Guid>();
            if (!applyRequested || decisions == null)
            {
                return accepted;
            }

            foreach (var decision in decisions)
            {
                if (string.Equals(decision.Decision, "Accept", StringComparison.OrdinalIgnoreCase))
                {
                    accepted.Add(decision.IssueId);
                }
            }

            return accepted;
        }
    }
}
