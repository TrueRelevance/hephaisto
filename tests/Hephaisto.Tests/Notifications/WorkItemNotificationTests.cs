using System.Text.Json.Nodes;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Notifications;
using Hephaisto.Agent.Notifications.TeamsBot;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Notifications;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// What a person is told about a code fix that is for a GitHub issue (v0.14.0, #248): the same
/// three moments an incident's attempt announces, with the issue where the incident was - and
/// with somebody else's words shown as words.
/// </summary>
public sealed class WorkItemNotificationTests
{
    private static readonly CodeFixAttempt Attempt = new()
    {
        RepositoryUrl = "https://github.com/octo/shop",
        PrUrl = "https://github.com/octo/shop/pull/14",
    };

    [Fact]
    public void A_waiting_plan_names_the_issue_and_says_where_it_is_answered()
    {
        var text = CodeFixNotifier.WorkItemText(NotificationEvent.CodeFixPlanReady, "octo/shop#12", Attempt, null, commentsAreRead: true);

        text.Should().Contain("octo/shop#12").And.Contain("/approve").And.Contain("/reject").And.Contain("in the console");
    }

    [Fact]
    public void With_no_approvers_listed_it_does_not_send_anybody_to_the_issue()
    {
        // No comment is read at all then; a notification that said "/approve on the issue" would
        // send a person to write a comment nothing reads.
        var text = CodeFixNotifier.WorkItemText(NotificationEvent.CodeFixPlanReady, "octo/shop#12", Attempt, null, commentsAreRead: false);

        text.Should().Contain("octo/shop#12").And.Contain("in the console").And.Contain("GitHub:Approvers is empty");
        text.Should().NotContain("/approve");
    }

    [Fact]
    public void A_pull_request_and_an_end_without_one_name_the_issue_too()
    {
        CodeFixNotifier.WorkItemText(NotificationEvent.CodeFixPrOpened, "octo/shop#12", Attempt, "Pull request x", true)
            .Should().Contain("octo/shop#12").And.Contain("https://github.com/octo/shop/pull/14");

        CodeFixNotifier.WorkItemText(NotificationEvent.CodeFixFailed, "octo/shop#12", Attempt, "cancelled: the issue was closed", true)
            .Should().Contain("octo/shop#12").And.Contain("ended without a pull request").And.Contain("cancelled: the issue was closed");
    }

    [Fact]
    public void The_key_is_the_work_items_own()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        CodeFixNotifier.WorkItemKey(a).Should().NotBe(CodeFixNotifier.WorkItemKey(b));
        CodeFixNotifier.WorkItemKey(a).Should().StartWith("workitem/").And.EndWith("/codefix");
    }

    [Fact]
    public void The_link_is_the_attempts_own_page()
    {
        var id = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c");

        NotificationLinks.CodeFix("https://hephaisto.example/", id).Should().Be("https://hephaisto.example/codefixes/0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c");
        NotificationLinks.CodeFix(null, id).Should().BeNull("no base URL, no link - as for an incident");
        NotificationLinks.CodeFix("https://hephaisto.example", null).Should().BeNull();
    }

    [Fact]
    public void A_work_items_message_links_its_attempt_and_an_incidents_links_its_incident()
    {
        const string console = "https://hephaisto.example";
        const string grafana = "https://grafana.example";

        // A work item: the attempt's page, and neither an incident nor a dashboard made up.
        var plan = GivenNotifications.WorkItemPlan();
        NotificationLinks.For(console, grafana, null, plan)
            .Should().Be((null, $"{console}/codefixes/{plan.CodeFixAttemptId}", null));

        // An incident's code fix: as it was - the incident, at its code-fix section.
        var incident = GivenNotifications.Escalation(@event: NotificationEvent.CodeFixPlanReady) with { CodeFixAttemptId = Guid.CreateVersion7() };
        var links = NotificationLinks.For(console, grafana, incident.IncidentId, incident);

        links.Incident.Should().Be($"{console}/incidents/{incident.IncidentId}#codefix");
        links.CodeFix.Should().BeNull();
        links.Grafana.Should().StartWith(grafana);

        // An incident's own event: no fragment.
        var escalation = GivenNotifications.Escalation();
        NotificationLinks.For(console, null, escalation.IncidentId, escalation)
            .Should().Be(($"{console}/incidents/{escalation.IncidentId}", null, null));

        // No base URL: no link of either kind, and nothing throws.
        NotificationLinks.For(null, null, null, plan).Should().Be((null, null, null));
    }

    [Fact]
    public void The_bots_card_shows_the_issues_title_and_the_plan_as_text_never_as_markdown()
    {
        var card = Card(GivenNotifications.WorkItemPlan());
        var body = card["body"]!.AsArray();

        // The headline is Hephaisto's own and names the reference.
        body[0]!["text"]!.GetValue<string>().Should().Be("Code fix planned for octo/shop#12");

        // The title and the summary are RichTextBlocks of one TextRun: Teams renders a TextRun as
        // it is, where a TextBlock would turn [the docs](https://evil.example) into a link.
        var runs = body.Where(b => b!["type"]!.GetValue<string>() == "RichTextBlock")
            .Select(b => b!["inlines"]![0]!["text"]!.GetValue<string>())
            .ToList();

        runs.Should().Contain("The order total is null for an empty cart");
        runs.Should().Contain(r => r.Contains("[the docs](https://evil.example)", StringComparison.Ordinal));

        body.Where(b => b!["type"]!.GetValue<string>() == "TextBlock")
            .Select(b => b!["text"]!.GetValue<string>())
            .Should().NotContain(t => t.Contains("evil.example", StringComparison.Ordinal) || t.Contains("The order total", StringComparison.Ordinal));
    }

    [Fact]
    public void The_bots_card_opens_pages_and_decides_nothing()
    {
        var card = Card(GivenNotifications.WorkItemPlan(NotificationEvent.CodeFixPrOpened) with { ExternalUrl = "https://github.com/octo/shop/pull/14" });

        var actions = card["actions"]!.AsArray().Select(a => (a!["type"]!.GetValue<string>(), a["title"]!.GetValue<string>(), a["url"]!.GetValue<string>())).ToList();

        actions.Should().Equal(
            ("Action.OpenUrl", "Open the pull request", "https://github.com/octo/shop/pull/14"),
            ("Action.OpenUrl", "Open in Hephaisto", "https://hephaisto.example/codefixes/0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c"),
            ("Action.OpenUrl", "Open the issue", "https://github.com/octo/shop/issues/12"));

        card.ToJsonString().Should().NotContain("Action.Execute").And.NotContain("Action.Submit");
    }

    [Fact]
    public void What_a_lock_screen_shows_is_the_event_and_the_reference_and_nobody_elses_words()
    {
        var activity = TeamsBotCards.WorkItemEvent(Message(GivenNotifications.WorkItemPlan()) with { AlsoSuppressed = 2 });

        activity["summary"]!.GetValue<string>().Should().Be("Code fix planned for octo/shop#12 (+2 suppressed)");
    }

    [Fact]
    public void A_weekly_review_s_end_is_its_counts_as_words_and_a_button_to_the_review()
    {
        var snapshot = new NotificationSnapshot
        {
            Event = NotificationEvent.PlannerReviewFinished,
            Title = "Weekly review: 5 to close, 8 bugs, 8 to build, 1 unsure",
            Reason = "Not everything went: Batch 2 of 3 answered [click](https://evil.example).",
            At = DateTimeOffset.Parse("2026-11-09T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        };

        var activity = TeamsBotCards.ReviewEvent(new NotificationMessage
        {
            DeliveryId = Guid.CreateVersion7(),
            Snapshot = snapshot,
            ReviewUrl = "https://hephaisto.example/planner/review",
        });
        var card = activity["attachments"]![0]!["content"]!.AsObject();
        var json = card.ToJsonString();

        activity["summary"]!.GetValue<string>().Should().Be(snapshot.Title);
        card["body"]![0]!["text"]!.GetValue<string>().Should().Be(snapshot.Title);
        card["actions"]![0]!["url"]!.GetValue<string>().Should().Be("https://hephaisto.example/planner/review");

        // A problem can be a Job's or GitHub's words: a text run, which Teams never reads as markdown.
        card["body"]![1]!["type"]!.GetValue<string>().Should().Be("RichTextBlock");
        json.Should().Contain("[click](https://evil.example)");
    }

    private static NotificationMessage Message(NotificationSnapshot snapshot) => new()
    {
        DeliveryId = Guid.CreateVersion7(),
        Snapshot = snapshot,
        CodeFixUrl = "https://hephaisto.example/codefixes/0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c",
    };

    private static JsonObject Card(NotificationSnapshot snapshot) =>
        TeamsBotCards.WorkItemEvent(Message(snapshot))["attachments"]![0]!["content"]!.AsObject();
}
