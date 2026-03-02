using System;
using System.Collections.Generic;

namespace AtsBackgroundBuilder.Core
{
    internal enum PlsrApplyDecisionActionType
    {
        None,
        UpdateOwner,
        TagExpired,
        CreateMissingLabel,
        CreateMissingLabelFromTemplate,
        CreateMissingLabelFromXml
    }

    internal sealed class PlsrApplyDecisionItem
    {
        public Guid IssueId { get; set; }
        public bool IsActionable { get; set; }
        public PlsrApplyDecisionActionType ActionType { get; set; } = PlsrApplyDecisionActionType.None;
    }

    internal sealed class PlsrApplyDecisionRoutedIssue
    {
        public Guid IssueId { get; set; }
        public PlsrApplyDecisionActionType ActionType { get; set; } = PlsrApplyDecisionActionType.None;
    }

    internal sealed class PlsrApplyDecisionResult
    {
        public int AcceptedActionable { get; set; }
        public int IgnoredActionable { get; set; }
        public List<PlsrApplyDecisionRoutedIssue> AcceptedRoutedIssues { get; } = new List<PlsrApplyDecisionRoutedIssue>();
    }

    internal static class PlsrApplyDecisionEngine
    {
        public static PlsrApplyDecisionResult Route(
            IEnumerable<PlsrApplyDecisionItem> items,
            ISet<Guid> acceptedIssueIds)
        {
            var result = new PlsrApplyDecisionResult();
            if (items == null)
            {
                return result;
            }

            foreach (var item in items)
            {
                if (item == null || !item.IsActionable)
                {
                    continue;
                }

                if (acceptedIssueIds == null || !acceptedIssueIds.Contains(item.IssueId))
                {
                    result.IgnoredActionable++;
                    continue;
                }

                result.AcceptedActionable++;
                result.AcceptedRoutedIssues.Add(new PlsrApplyDecisionRoutedIssue
                {
                    IssueId = item.IssueId,
                    ActionType = item.ActionType
                });
            }

            return result;
        }
    }
}
