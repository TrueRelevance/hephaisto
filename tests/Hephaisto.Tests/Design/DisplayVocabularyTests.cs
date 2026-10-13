using System.Text.RegularExpressions;
using Hephaisto.Agent.Components;
using Hephaisto.Core.Domain;
using Hephaisto.Agent.Observability;

using Hephaisto.Core.Planning;

namespace Hephaisto.Tests.Design;

/// <summary>
/// The demo site can still read the glyph vocabulary out of <see cref="Display"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>demo-site/display.mjs</c> parses <c>Display.cs</c> at build time rather than carrying its
/// own copy, because the copy it used to carry had drifted: three of five states rendered with
/// another state's glyph, on every page of demo.hephaisto.dev, for as long as the site existed.
/// </para>
/// <para>
/// C# cannot run the JavaScript, so this asserts the thing that actually breaks it - the shape
/// the regexes match. A <c>switch</c> rewritten as a dictionary, or an arm moved onto two lines,
/// would leave <c>display.mjs</c> parsing nothing; it refuses loudly in that case, and this
/// test is what makes the refusal happen on a pull request instead of in a deploy.
/// </para>
/// </remarks>
public class DisplayVocabularyTests
{
    /// <summary>
    /// A work item's three states (v0.14.0): each has a glyph, a word and a class of its own,
    /// because state is never colour alone - and an unnamed state falls through to "?".
    /// </summary>
    [Fact]
    public void The_work_item_vocabulary_covers_every_state()
    {
        var states = Enum.GetValues<WorkItemState>();

        foreach (var state in states)
        {
            Display.WorkItemGlyph(state).Should().NotBe("?");
            Display.WorkItemWord(state).Should().NotBe("unknown");
            Display.WorkItemClass(state).Should().StartWith("st-");
        }

        states.Select(Display.WorkItemGlyph).Should().OnlyHaveUniqueItems();
        states.Select(Display.WorkItemWord).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// The planner's vocabulary (v0.15.0). Every reason is "a person decides" and shares one
    /// colour, so the glyph and the word are all that tells two of them apart - which is why
    /// both have to be unique, and why "nothing asks for a decision" may not look like one.
    /// </summary>
    [Fact]
    public void The_planner_vocabulary_covers_every_reason_day_and_pull_request_state()
    {
        var reasons = Enum.GetValues<PlannerReason>();

        foreach (var reason in reasons)
        {
            Display.PlannerReasonWord(reason).Should().NotBe("unknown");
            Display.PlannerReasonClass(reason).Should().StartWith("st-");
        }

        reasons.Select(Display.PlannerReasonGlyph).Should().OnlyHaveUniqueItems();
        reasons.Select(Display.PlannerReasonWord).Should().OnlyHaveUniqueItems();
        reasons.Where(r => r != PlannerReason.None).Select(Display.PlannerReasonClass).Distinct().Should().Equal("st-awaiting");
        Display.PlannerReasonClass(PlannerReason.None).Should().NotBe("st-awaiting");

        var days = Enum.GetValues<PlannerDay>();

        days.Select(Display.PlannerDayWord).Should().OnlyHaveUniqueItems().And.NotContain("unknown");
        days.Select(Display.PlannerDayGlyph).Should().OnlyHaveUniqueItems().And.NotContain("?");

        var pulls = Enum.GetValues<BoardPullState>();

        pulls.Select(Display.PullStateWord).Should().OnlyHaveUniqueItems().And.NotContain("unknown");
        pulls.Select(Display.PullStateGlyph).Should().OnlyHaveUniqueItems().And.NotContain("?");
        pulls.Select(Display.PullStateClass).Should().OnlyContain(c => c.StartsWith("st-"));
    }

    [Theory]
    [InlineData(0, "sev-critical")]
    [InlineData(1, "sev-warning")]
    [InlineData(2, "sev-info")]
    [InlineData(int.MaxValue, "sev-info")]
    public void A_priority_is_coloured_by_where_it_stands_among_its_fields_options(int rank, string expected)
    {
        Display.PlannerPriorityClass(rank).Should().Be(expected);
    }

    [Theory]
    [InlineData("2026-10-13T00:00:01Z", "today")]
    [InlineData("2026-10-12T23:59:59Z", "yesterday")]
    [InlineData("2026-10-04T09:00:00Z", "9 days ago")]
    [InlineData("2026-10-14T09:00:00Z", "today")]
    public void How_long_ago_is_counted_in_utc_days(string at, string expected)
    {
        Display.Ago(DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture), new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero))
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("https://github.com/acme/api/issues/12", "acme/api#12")]
    [InlineData("https://github.example/acme/api/issues/12", "acme/api#12")]
    [InlineData("https://github.com/acme/api/pull/12", "https://github.com/acme/api/pull/12")]
    [InlineData("not an address", "not an address")]
    public void An_issues_address_is_shortened_to_its_reference_and_anything_else_is_left_alone(string url, string expected)
    {
        Display.ShortIssue(url).Should().Be(expected);
    }

    /// <summary>Through what a plan was answered: every source a person can decide through has words.</summary>
    [Fact]
    public void Every_way_a_plan_is_answered_has_words_and_nothing_else_does()
    {
        Display.DecidedThrough(ApprovalSource.Ui).Should().Be("the console");
        Display.DecidedThrough(ApprovalSource.GitHub).Should().Be("a comment on the issue");
        Display.DecidedThrough(ApprovalSource.Api).Should().NotBeNull();
        Display.DecidedThrough(ApprovalSource.Oidc).Should().NotBeNull();
        Display.DecidedThrough(ApprovalSource.Teams).Should().NotBeNull();

        Display.DecidedThrough(ApprovalSource.NotApplicable).Should().BeNull();
        Display.DecidedThrough(ApprovalSource.Auto).Should().BeNull("a model's plan is never approved by policy");
        Display.DecidedThrough(null).Should().BeNull();
    }

    /// <summary>
    /// An issue's address becomes a link only when it is an absolute http(s) address: it is
    /// whatever the API that was asked said, and a javascript: address is an address too.
    /// </summary>
    [Theory]
    [InlineData("https://github.com/octo/shop/issues/12", true)]
    [InlineData("http://github-stand-in.hephaisto-obs:8080/octo/shop/issues/12", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,<script>alert(1)</script>", false)]
    [InlineData("/octo/shop/issues/12", false)]
    [InlineData("github.com/octo/shop", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_an_absolute_http_address_is_ever_a_link(string? url, bool linked)
    {
        (Display.HttpUrl(url) is not null).Should().Be(linked);
    }

    private static readonly Regex Arm =
        new("""^\s*[A-Za-z_][A-Za-z0-9_]*\.([A-Za-z_][A-Za-z0-9_]*)\s*=>\s*"([^"]*)"\s*,""",
            RegexOptions.Compiled | RegexOptions.Multiline);

    private static Regex Method(string name) =>
        new($$"""public static string {{name}}\([^)]*\)\s*=>\s*\w+ switch\s*\{([\s\S]*?)\n    \};""",
            RegexOptions.Multiline);

    [Fact]
    public void The_state_vocabulary_parses_and_agrees_with_the_method()
    {
        var arms = Parse("StateGlyph");

        // Every member, not most of them: a state the demo site cannot name renders as the
        // fallback "?", which looks like a deliberate unknown rather than a missing case.
        foreach (var state in Enum.GetValues<IncidentState>())
        {
            arms.Should().ContainKey(state.ToString());
            arms[state.ToString()].Should().Be(Display.StateGlyph(state));
        }
    }

    [Fact]
    public void The_state_classes_parse_and_agree_with_the_method()
    {
        var arms = Parse("StateClass");

        foreach (var state in Enum.GetValues<IncidentState>())
        {
            arms.Should().ContainKey(state.ToString());
            arms[state.ToString()].Should().Be(Display.StateClass(state));
        }
    }

    /// <summary>
    /// The connections panel's four states (#111), pinned for the reason the others are: an
    /// unnamed arm falls through to "?" and reads as a deliberate unknown rather than a gap.
    /// </summary>
    /// <remarks>
    /// The fourth state is why this matters more than it looks. <c>NotConfigured</c> must keep a
    /// muted glyph and the suppressed colour: a deliberately switched-off channel painted red
    /// makes a correct install look broken, and a panel that is wrong on a correct install is one
    /// people stop reading - which would cost the whole feature.
    /// </remarks>
    [Fact]
    public void The_connection_vocabulary_covers_every_state()
    {
        var glyphs = Parse("ConnectionGlyph");
        var classes = Parse("ConnectionClass");

        foreach (var state in Enum.GetValues<ConnectionState>())
        {
            glyphs.Should().ContainKey(state.ToString());
            glyphs[state.ToString()].Should().Be(Display.ConnectionGlyph(state));

            classes.Should().ContainKey(state.ToString());
            classes[state.ToString()].Should().Be(Display.ConnectionClass(state));
        }
    }

    /// <summary>
    /// Deny is not named in either decision switch - it is the default arm - so the parser has
    /// to read the fallback as part of the vocabulary rather than as defensive padding.
    /// </summary>
    [Fact]
    public void The_decision_vocabulary_keeps_deny_in_the_default_arm()
    {
        var glyphs = Parse("DecisionGlyph");
        var classes = Parse("DecisionClass");

        glyphs.Should().ContainKey(nameof(PolicyDecision.Allow));
        glyphs.Should().ContainKey(nameof(PolicyDecision.RequireApproval));
        glyphs.Should().NotContainKey(nameof(PolicyDecision.Deny),
            "if Deny gains a named arm, display.mjs still reads it - but this test should be the "
            + "thing that tells you the fallback is no longer load-bearing");

        classes.Should().ContainKey(nameof(PolicyDecision.Allow));
        classes.Should().ContainKey(nameof(PolicyDecision.RequireApproval));

        Display.DecisionGlyph(PolicyDecision.Deny).Should().Be("x");
        Display.DecisionClass(PolicyDecision.Deny).Should().Be("dec-deny");
    }

    private static Dictionary<string, string> Parse(string method)
    {
        var source = File.ReadAllText(DisplayFile());
        var block = Method(method).Match(source);

        block.Success.Should().BeTrue(
            $"demo-site/display.mjs finds Display.{method} with this shape; if it has been "
            + "rewritten, that build renders every value as the fallback");

        return Arm.Matches(block.Groups[1].Value)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
    }

    private static string DisplayFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);

        return Path.Combine(dir!.FullName, "src", "Hephaisto.Agent", "Components", "Display.cs");
    }
}
