using System.Globalization;
using Hephaisto.Core.Notifications;

namespace Hephaisto.Agent.Notifications;

/// <summary>
/// The links a message carries, built at render rather than stored.
/// </summary>
/// <remarks>
/// Derived rather than frozen into the snapshot on purpose: a base URL that turns out to be
/// wrong - and it is the one setting the pod cannot check for itself - is then fixed by editing
/// a value, not by re-queuing every row already waiting behind a failed endpoint.
/// </remarks>
public static class NotificationLinks
{
    /// <summary>
    /// The links one message carries, by what it is about.
    /// </summary>
    /// <remarks>
    /// An incident's event links its incident - at the code-fix section when it is a code-fix
    /// event, where the plan and its buttons are - and Grafana around the time. A code-fix event
    /// about a work item (v0.14.0) has no incident page and no dashboard: an issue names a
    /// repository, not something that runs. It links the attempt's own page and nothing else.
    /// </remarks>
    public static (string? Incident, string? CodeFix, string? Grafana) For(
        string? baseUrl, string? grafanaUrl, Guid? incidentId, NotificationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.WorkItemId is not null)
        {
            return (null, CodeFix(baseUrl, snapshot.CodeFixAttemptId), null);
        }

        // A review run (#312) is about the backlog, not something that runs: its one link is the
        // review page, from Review(), and Grafana around the time would show nothing of it.
        if (snapshot.Event is NotificationEvent.PlannerReviewFinished)
        {
            return (null, null, null);
        }

        var incident = Incident(baseUrl, incidentId);

        return (
            incident is not null && snapshot.CodeFixAttemptId is not null ? incident + "#codefix" : incident,
            null,
            Grafana(grafanaUrl, snapshot));
    }

    /// <summary>The incident in Hephaisto's own console, which is where approval happens.</summary>
    public static string? Incident(string? baseUrl, Guid? incidentId)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || incidentId is not { } id)
        {
            return null;
        }

        return $"{baseUrl.TrimEnd('/')}/incidents/{id}";
    }

    /// <summary>
    /// One code-fix attempt's own page in the console: the plan in full and the place it is
    /// answered. What a notification about a work item's attempt links, since there is no
    /// incident page to open.
    /// </summary>
    public static string? CodeFix(string? baseUrl, Guid? attemptId)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || attemptId is not { } id)
        {
            return null;
        }

        return $"{baseUrl.TrimEnd('/')}/codefixes/{id}";
    }

    /// <summary>
    /// The weekly review's page (#312), for the one event about a review run; null for any other.
    /// </summary>
    public static string? Review(string? baseUrl, NotificationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return string.IsNullOrWhiteSpace(baseUrl) || snapshot.Event is not NotificationEvent.PlannerReviewFinished
            ? null
            : $"{baseUrl.TrimEnd('/')}/planner/review";
    }

    /// <summary>The note people keep for an alert name (#145), in the console.</summary>
    public static string? AlertNote(string? baseUrl, string? alertName)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(alertName))
        {
            return null;
        }

        return $"{baseUrl.TrimEnd('/')}/alerts/{Uri.EscapeDataString(alertName)}";
    }

    /// <summary>
    /// Grafana, scoped to the hour around the event.
    /// </summary>
    /// <remarks>
    /// Built here rather than through grafana-mcp's <c>generate_deeplink</c>: that is a tool for
    /// the model, and putting an MCP round trip on the delivery path would make a notification
    /// depend on a service that this one is quite possibly being sent because of.
    /// </remarks>
    public static string? Grafana(string? grafanaUrl, NotificationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (string.IsNullOrWhiteSpace(grafanaUrl))
        {
            return null;
        }

        var from = snapshot.At.AddMinutes(-30).ToUnixTimeMilliseconds();
        var to = snapshot.At.AddMinutes(30).ToUnixTimeMilliseconds();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{grafanaUrl.TrimEnd('/')}/d/hephaisto/hephaisto?from={from}&to={to}");
    }
}
