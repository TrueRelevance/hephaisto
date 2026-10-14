using System.Globalization;
using System.Text;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.WorkItems;

namespace Hephaisto.Agent.Planner;

/// <summary>
/// The comment that carries Claude's analysis of an issue (v0.15.0). Everything the model wrote
/// goes through <see cref="IssueComments"/>' neutralisers, as a plan's text does: a model wrote it
/// about text a stranger wrote, and it must not mention anybody, link anything or reference
/// another issue on the agent's account.
/// </summary>
public static class TriageComment
{
    public static string Marker(Guid attemptId) => $"{PlannerService.AnalysisMarker}:{attemptId:N} -->";

    public static string Render(TriageIssue issue, TriageResult result, IReadOnlyList<string> labels, string requestedBy)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(result);

        var text = new StringBuilder();

        text.Append(Marker(result.AttemptId)).Append('\n');
        text.Append("### Claude's analysis\n\n");
        text.Append("Asked for by ").Append(IssueComments.Neutralise(requestedBy, 100).TrimEnd('.')).Append(" on Hephaisto's planner. ");
        text.Append(labels.Count > 0
            ? $"Labels put on: {string.Join(", ", labels.Select(IssueComments.Code))}."
            : "No label of the repository fitted.");
        text.Append("\n\n");

        text.Append("**Summary.** ").Append(IssueComments.NeutraliseBlock(result.Summary, 500)).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(result.Analysis))
        {
            text.Append(IssueComments.NeutraliseBlock(result.Analysis, 8000)).Append("\n\n");
        }

        if (result.Steps.Count > 0)
        {
            text.Append("**Plan**\n\n");

            for (var i = 0; i < Math.Min(result.Steps.Count, 15); i++)
            {
                var number = $"{i + 1}. ";
                text.Append(number).Append(IssueComments.ItemBlock(result.Steps[i], 1000, number.Length)).Append('\n');
            }

            text.Append('\n');
        }

        if (result.Questions.Count > 0)
        {
            text.Append("**Open questions**\n\n");

            for (var i = 0; i < Math.Min(result.Questions.Count, 5); i++)
            {
                text.Append(i + 1).Append(". ").Append(IssueComments.Neutralise(result.Questions[i], 500)).Append('\n');
            }

            text.Append('\n');
        }

        text.Append("To work on it with Claude Code: `Execute github issue ")
            .Append(issue.Repository).Append('#').Append(issue.Number.ToString(CultureInfo.InvariantCulture)).Append("`\n\n");

        text.Append("<sub>$")
            .Append(result.CostUsd.ToString("0.00", CultureInfo.InvariantCulture))
            .Append(result.Billing == "subscription" ? " (notional: a subscription paid)" : result.Billing == "fake" ? " (scripted: no model ran)" : string.Empty)
            .Append(" · attempt ").Append(result.AttemptId.ToString("N")[^12..])
            .Append("</sub>\n");

        return text.ToString();
    }

    /// <summary>The comment as a person reads it: without the marker line.</summary>
    public static string WithoutMarker(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (!body.StartsWith(PlannerService.AnalysisMarker, StringComparison.Ordinal))
        {
            return body;
        }

        var end = body.IndexOf('\n', StringComparison.Ordinal);

        return end < 0 ? string.Empty : body[(end + 1)..].TrimStart('\n');
    }
}
