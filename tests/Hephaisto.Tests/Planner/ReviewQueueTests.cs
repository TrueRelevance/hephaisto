using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Planner.Review;
using Hephaisto.Tests.TestData;

namespace Hephaisto.Tests.Planner;

/// <summary>
/// The review page's rules (#312), without a browser: which tab a verdict is on and the counts,
/// the headings under a tab, what a card offers, and what "Close N issues" closes.
/// </summary>
public sealed class ReviewQueueTests
{
    [Fact]
    public void Each_verdict_is_counted_on_its_tab_and_a_decision_moves_it()
    {
        var verdicts = new[]
        {
            View("acme/api#431", "close", closeReason: "done"),
            View("acme/web#118", "bug", priority: "P1"),
            View("acme/planning#30", "bug", confidence: "low"),
            View("acme/planning#31", "bug", confidence: "low", decision: Moved("bug")),
            View("acme/api#205", "feature", decision: Unsure()),
        };

        verdicts.Select(v => v.Tab).Should().Equal("close", "bug", "unsure", "bug", "unsure");
        ReviewQueue.Tabs.Select(t => ReviewQueue.Count(verdicts, t)).Should().Equal(1, 2, 0, 2);
        ReviewQueue.Decided(verdicts, "bug").Should().Be(1);
    }

    [Fact]
    public void Close_cards_are_grouped_by_their_reason_in_a_fixed_order_and_a_moved_card_by_the_persons()
    {
        var verdicts = new[]
        {
            View("acme/legacy-ttd#1", "close", closeReason: "obsolete"),
            View("acme/api#431", "close", closeReason: "done"),
            View("acme/infra#44", "close", closeReason: "done"),
            View("acme/api#205", "feature", decision: Moved("close", "stale")),
            View("acme/web#118", "bug"),
        };

        var groups = ReviewQueue.GroupsOf(verdicts, "close");

        groups.Select(g => (g.Key, g.Heading, g.Cards.Count)).Should().Equal(("done", "Already done", 2), ("obsolete", "Obsolete", 1), ("stale", "Stale", 1));
    }

    [Fact]
    public void Bugs_and_features_are_grouped_by_the_suggested_priority_the_urgent_first()
    {
        var verdicts = new[] { View("a/b#1", "bug", priority: "P2"), View("a/b#2", "bug"), View("a/b#3", "bug", priority: "P0") };

        ReviewQueue.GroupsOf(verdicts, "bug").Select(g => g.Key).Should().Equal("P0", "P2", "none");
        ReviewQueue.GroupsOf(verdicts, "feature").Should().BeEmpty();
    }

    [Fact]
    public void Close_closes_what_a_person_decided_to_close_and_counts_an_archived_repository_out()
    {
        var verdicts = new[]
        {
            View("acme/api#431", "close", closeReason: "done", decision: Agreed("close", "done")),
            View("acme/legacy-ttd#1", "close", closeReason: "obsolete", archived: true, decision: Agreed("close", "obsolete")),
            View("acme/api#205", "feature", decision: Moved("close", "stale")),
            View("acme/infra#44", "close", closeReason: "done"),
            View("acme/web#160", "close", closeReason: "done", decision: Unsure()),
        };

        ReviewQueue.Closable(verdicts).Select(v => v.Number).Should().Equal(431, 205);
        ReviewQueue.ArchivedAgreed(verdicts).Select(v => v.Number).Should().Equal(1);
    }

    [Fact]
    public void A_card_offers_agree_only_where_claudes_verdict_puts_it()
    {
        var close = View("acme/api#431", "close", closeReason: "done");
        ReviewQueue.ActionsFor(close, "close").Should().BeEquivalentTo(new ReviewCardActions(true, ["bug", "feature"], true));

        // A low confidence is under unsure: agree with Claude there, or move it anywhere.
        var low = View("acme/planning#30", "bug", confidence: "low");
        ReviewQueue.ActionsFor(low, "unsure").Should().BeEquivalentTo(new ReviewCardActions(true, ["close", "bug", "feature"], false));

        // Moved away by a person: agreeing with Claude here would say the opposite of the card.
        var moved = View("acme/api#205", "feature", decision: Moved("close", "stale"));
        ReviewQueue.ActionsFor(moved, "close").Agree.Should().BeFalse();

        // The runner's own unsure has no verdict to agree with.
        var none = View("acme/api#9", "unsure", confidence: "low");
        ReviewQueue.ActionsFor(none, "unsure").Agree.Should().BeFalse();
    }

    [Fact]
    public void A_decided_card_says_who_and_what()
    {
        ReviewQueue.DecidedWords(Agreed("close", "done")).Should().Be("agreed by flo");
        ReviewQueue.DecidedWords(Moved("close", "stale")).Should().Be("moved to close as stale by flo");
        ReviewQueue.DecidedWords(Moved("bug")).Should().Be("moved to a real bug by flo");
        ReviewQueue.DecidedWords(Unsure()).Should().Be("marked unsure by flo");
    }

    [Fact]
    public void Every_close_reason_and_tab_has_words()
    {
        foreach (var reason in ReviewVerdicts.CloseReasons)
        {
            ReviewQueue.CloseReasonOrder.Should().Contain(reason);
            ReviewQueue.CloseReasonWord(reason).Should().NotBe("closable");
            ReviewQueue.CloseReasonHeading(reason).Should().NotBe("Other");
        }

        ReviewQueue.Tabs.Select(ReviewQueue.TabWord).Should().Equal("Can be closed", "Real bugs", "Still to build", "Unsure");
    }

    private static ReviewDecisionView Agreed(string group, string? reason = null) => new("flo", Given.Now, group, reason, true, false);

    private static ReviewDecisionView Moved(string group, string? reason = null) => new("flo", Given.Now, group, reason, false, false);

    private static ReviewDecisionView Unsure() => new("flo", Given.Now, "bug", null, false, true);

    /// <summary>A verdict as the page gets it, its tab worked out by the same rule as the API's.</summary>
    private static ReviewVerdictView View(
        string reference, string group, string? closeReason = null, string confidence = "high", string? priority = null, bool archived = false, ReviewDecisionView? decision = null)
    {
        var repository = reference[..reference.IndexOf('#', StringComparison.Ordinal)];
        var number = int.Parse(reference[(reference.IndexOf('#', StringComparison.Ordinal) + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        var row = ReviewActionsTests.Verdict(repository, number, group, closeReason, confidence, priority, archived: archived);
        ReviewVerdicts.Decide(row, decision is null ? null : new ReviewDecision(decision.By, decision.At, decision.Group, decision.CloseReason, decision.Agreed, decision.Unsure));

        return ReviewReader.View(row);
    }
}
