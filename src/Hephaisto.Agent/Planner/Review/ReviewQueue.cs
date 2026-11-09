namespace Hephaisto.Agent.Planner.Review;

/// <summary>A group of cards under one heading of a tab: the close cards by their reason, the others by priority.</summary>
/// <param name="Key">The reason (<c>done</c>, ...) or the priority (<c>P1</c>, or <c>none</c>).</param>
public sealed record ReviewCardGroup(string Key, string Heading, IReadOnlyList<ReviewVerdictView> Cards);

/// <summary>The clicks a card offers on the tab it is shown on.</summary>
/// <param name="Agree">Agree with Claude's verdict: only where the verdict's own group is a group, and the card is not elsewhere by a person's move.</param>
/// <param name="MoveTo">The groups it can be moved to.</param>
/// <param name="Unsure">Mark it unsure: everywhere but the unsure tab.</param>
public sealed record ReviewCardActions(bool Agree, IReadOnlyList<string> MoveTo, bool Unsure);

/// <summary>
/// The review page's rules (#312), pure, so they are tested without a browser: which tab holds
/// what, the counts, the groups under a tab, what a card offers, and what "Close N issues" closes.
/// </summary>
public static class ReviewQueue
{
    public const string UnsureTab = ReviewVerdicts.Unsure;

    /// <summary>The tabs, in the order they are shown.</summary>
    public static readonly IReadOnlyList<string> Tabs = [ReviewVerdicts.Close, ReviewVerdicts.Bug, ReviewVerdicts.Feature, ReviewVerdicts.Unsure];

    /// <summary>The groups a person can put an issue in. Unsure is not one: it is a mark.</summary>
    public static readonly IReadOnlySet<string> Groups = new HashSet<string>(StringComparer.Ordinal) { ReviewVerdicts.Close, ReviewVerdicts.Bug, ReviewVerdicts.Feature };

    public static readonly IReadOnlyList<string> CloseReasonOrder = ["done", "superseded", "obsolete", "duplicate", "answered", "stale"];

    private static readonly IReadOnlyList<string> PriorityOrder = ["P0", "P1", "P2"];

    public static string TabWord(string tab) => tab switch
    {
        ReviewVerdicts.Close => "Can be closed",
        ReviewVerdicts.Bug => "Real bugs",
        ReviewVerdicts.Feature => "Still to build",
        ReviewVerdicts.Unsure => "Unsure",
        _ => tab,
    };

    /// <summary>A group in a sentence: "agreed: a bug".</summary>
    public static string GroupWord(string group) => group switch
    {
        ReviewVerdicts.Close => "can be closed",
        ReviewVerdicts.Bug => "a real bug",
        ReviewVerdicts.Feature => "still to build",
        _ => "unsure",
    };

    /// <summary>The short word on a move button.</summary>
    public static string MoveWord(string group) => group switch
    {
        ReviewVerdicts.Close => "Close",
        ReviewVerdicts.Bug => "Bug",
        ReviewVerdicts.Feature => "Build",
        _ => group,
    };

    public static string CloseReasonWord(string? reason) => reason switch
    {
        "done" => "already done",
        "superseded" => "superseded",
        "obsolete" => "obsolete",
        "duplicate" => "a duplicate",
        "answered" => "answered",
        "stale" => "stale",
        _ => "closable",
    };

    public static string CloseReasonHeading(string reason) => reason switch
    {
        "done" => "Already done",
        "superseded" => "Superseded",
        "obsolete" => "Obsolete",
        "duplicate" => "Duplicate",
        "answered" => "Answered",
        "stale" => "Stale",
        _ => "Other",
    };

    public static string PriorityHeading(string? priority) => priority switch
    {
        "P0" => "P0 · cannot wait",
        "P1" => "P1 · important",
        "P2" => "P2 · nice to have",
        _ => "No priority suggested",
    };

    public static int Count(IEnumerable<ReviewVerdictView> verdicts, string tab) => verdicts.Count(v => v.Tab == tab);

    /// <summary>Decided, on a tab: what a person already said of the issues there.</summary>
    public static int Decided(IEnumerable<ReviewVerdictView> verdicts, string tab) => verdicts.Count(v => v.Tab == tab && v.Decision is not null);

    /// <summary>A person decided to close it: agreed with a close, or moved it to close - and did not mark it unsure.</summary>
    public static bool DecidedToClose(bool decided, string? group, bool unsure) => decided && !unsure && group == ReviewVerdicts.Close;

    public static bool DecidedToClose(ReviewVerdictView v)
    {
        ArgumentNullException.ThrowIfNull(v);

        return DecidedToClose(v.Decision is not null, v.Decision?.Group, v.Decision?.Unsure ?? false);
    }

    /// <summary>What "Close N issues" closes: decided to close, and not in an archived repository.</summary>
    public static IReadOnlyList<ReviewVerdictView> Closable(IEnumerable<ReviewVerdictView> verdicts) =>
        [.. verdicts.Where(v => DecidedToClose(v) && !v.Archived)];

    /// <summary>Decided to close and in an archived repository: counted out of the bar, and the bar says so.</summary>
    public static IReadOnlyList<ReviewVerdictView> ArchivedAgreed(IEnumerable<ReviewVerdictView> verdicts) =>
        [.. verdicts.Where(v => DecidedToClose(v) && v.Archived)];

    /// <summary>The reason a close card is grouped under: the person's, when they moved it here, else Claude's.</summary>
    public static string CloseReasonOf(ReviewVerdictView v)
    {
        ArgumentNullException.ThrowIfNull(v);

        return (v.Decision is { Group: ReviewVerdicts.Close, CloseReason: { } chosen } ? chosen : v.CloseReason) ?? "other";
    }

    /// <summary>The cards of a tab under their headings: the close cards by reason, the others by suggested priority, unsure as one.</summary>
    public static IReadOnlyList<ReviewCardGroup> GroupsOf(IEnumerable<ReviewVerdictView> verdicts, string tab)
    {
        var cards = verdicts.Where(v => v.Tab == tab).ToList();

        if (cards.Count == 0)
        {
            return [];
        }

        return tab switch
        {
            ReviewVerdicts.Close => [.. cards
                .GroupBy(CloseReasonOf, StringComparer.Ordinal)
                .OrderBy(g => Rank(CloseReasonOrder, g.Key))
                .Select(g => new ReviewCardGroup(g.Key, CloseReasonHeading(g.Key), [.. g]))],
            ReviewVerdicts.Bug or ReviewVerdicts.Feature => [.. cards
                .GroupBy(v => v.Priority ?? "none", StringComparer.Ordinal)
                .OrderBy(g => Rank(PriorityOrder, g.Key))
                .Select(g => new ReviewCardGroup(g.Key, PriorityHeading(g.Key == "none" ? null : g.Key), [.. g]))],
            _ => [new ReviewCardGroup(UnsureTab, "Needs a person", cards)],
        };
    }

    /// <summary>
    /// What a card offers where it is shown. Agree is Claude's verdict as it stands, so it is offered
    /// only where that verdict puts the card - on its group's tab, or under unsure for a low
    /// confidence - and not on a card a person moved away from it. A move is to any other group;
    /// under unsure, to any group, since that is where a person decides.
    /// </summary>
    public static ReviewCardActions ActionsFor(ReviewVerdictView v, string tab)
    {
        ArgumentNullException.ThrowIfNull(v);

        var ownGroup = Groups.Contains(v.Group);
        var movedAway = v.Decision is { Agreed: false, Unsure: false } d && d.Group != v.Group;
        var agree = ownGroup && !movedAway && (tab == v.Group || tab == UnsureTab);

        return new ReviewCardActions(
            agree,
            [.. Tabs.Where(g => Groups.Contains(g) && (tab == UnsureTab || g != tab))],
            tab != UnsureTab);
    }

    /// <summary>What a decided card says of who decided.</summary>
    public static string DecidedWords(ReviewDecisionView d)
    {
        ArgumentNullException.ThrowIfNull(d);

        var what = d switch
        {
            { Unsure: true } => "marked unsure",
            { Agreed: true } => "agreed",
            { Group: ReviewVerdicts.Close } => $"moved to close as {CloseReasonWord(d.CloseReason)}",
            _ => $"moved to {GroupWord(d.Group)}",
        };

        return $"{what} by {d.By}";
    }

    private static int Rank(IReadOnlyList<string> order, string key)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == key)
            {
                return i;
            }
        }

        return order.Count;
    }
}
