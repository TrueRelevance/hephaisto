using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hephaisto.Tests.E2e;

/// <summary>
/// The console suite's bookkeeping (scripts/e2e/ui), held where every build sees it: its
/// known-red list (#312), and the weekly review's spec against the stand-in it runs on.
/// </summary>
/// <remarks>
/// The suite needs a live agent; these need nothing. The list is held to what the pager's and
/// the issues suite's are held to - every entry names a test that exists and reads the list,
/// every entry names the milestone that needs it green, and an entry the version floor has
/// passed fails. Playwright itself fails a listed test that passes.
/// </remarks>
public sealed partial class UiSuiteTests
{
    [Fact]
    public void Every_known_red_entry_names_its_spec_test_milestone_and_why()
    {
        foreach (var line in KnownRedLines())
        {
            EntryPattern().IsMatch(line).Should().BeTrue(
                $"'{line}' must read '<spec file> <test id> <major.minor> <stage and reason>' - a red test with no milestone is never due");
        }
    }

    /// <summary>
    /// A line for a test that does not exist, or in a spec that never calls <c>knownRed</c>, hides
    /// nothing: the test runs as if unlisted, and the line reads as a promise.
    /// </summary>
    [Fact]
    public void Every_known_red_entry_is_a_test_of_a_spec_that_reads_the_list()
    {
        foreach (var (spec, id, _) in KnownRed())
        {
            var path = Path.Combine(Tests(), spec);
            File.Exists(path).Should().BeTrue($"KNOWN_RED names {spec}, which is not in scripts/e2e/ui/tests");

            var text = File.ReadAllText(path);
            text.Should().Contain("knownRed(info)", $"{spec} is listed, so it must call knownRed in a beforeEach");
            text.Should().Contain($"test('{id} ", $"{spec} has no test whose title starts with {id}");
        }
    }

    [Fact]
    public void No_known_red_entry_is_older_than_the_version_floor()
    {
        var floor = Floor();

        KnownRed().Where(e => e.Milestone < floor).Select(e => $"{e.Spec} {e.Id}").Should().BeEmpty(
            $"the floor is {floor}, and every test of an earlier milestone had to be green for it");
    }

    [Fact]
    public void The_entry_pattern_can_fail()
    {
        // The guards above pass for the wrong reason if nothing can ever be malformed.
        EntryPattern().IsMatch("planner-review.spec.ts R01").Should().BeFalse();
        EntryPattern().IsMatch("planner-review.spec.ts 0.15 stage 3").Should().BeFalse();
        EntryPattern().IsMatch("planner-review R01 0.15 stage 3").Should().BeFalse();
        EntryPattern().IsMatch("planner-review.spec.ts R01 0.15 stage 3 (#312): no runner").Should().BeTrue();
    }

    /// <summary>
    /// The helper is what makes a line count; without it every listed test is an unexpected red.
    /// UI_STRICT is the release gate's way past the list, and the runner names what is still red.
    /// </summary>
    [Fact]
    public void The_helper_marks_a_listed_test_as_expected_to_fail_and_strict_ignores_the_list()
    {
        var helpers = File.ReadAllText(Path.Combine(Tests(), "helpers.ts"));

        helpers.Should().Contain("export function knownRed(info: TestInfo)");
        helpers.Should().Contain("process.env.UI_STRICT === '1'");
        helpers.Should().Contain("info.fail(true, `KNOWN RED");
        File.ReadAllText(Path.Combine(Ui(), "run.sh")).Should().Contain("startswith(\"KNOWN RED\")");
    }

    /// <summary>
    /// The spec and the stand-in are two files in two languages. A control the spec calls and the
    /// stand-in does not serve is a 404 that reads as "the review did nothing".
    /// </summary>
    [Theory]
    [InlineData("GitHubStandIn.cs", "control.MapGet(\"/writes\"", "/github/control/writes")]
    [InlineData("GitHubStandIn.cs", "control.MapGet(\"/requests\"", "/github/control/requests")]
    [InlineData("GitHubStandIn.cs", "control.MapGet(\"/state\"", "/github/control/state")]
    [InlineData("GitHubStandIn.cs", "control.MapGet(\"/comments\"", "/github/control/comments")]
    [InlineData("GitHubStandIn.cs", "control.MapPost(\"/repos/{owner}/{repo}/issues/{number:int}/comments\"", "/github/control/repos/acme/web/issues/118/comments")]
    [InlineData("GitHubStandIn.Project.cs", "control.MapPost(\"/reset\"", "/github/control/project/reset")]
    [InlineData("GitHubStandIn.Project.cs", "app.MapGroup(\"/github/control/project\")", "/github/control/project`")]
    [InlineData("TeamsStandIn.cs", "app.MapGet(\"/teams/messages\"", "/teams/messages")]
    public void Every_control_the_review_spec_calls_is_one_the_stand_in_serves(string file, string served, string called)
    {
        File.ReadAllText(Path.Combine(RepoRoot(), "infra", "e2e", "notification-receiver", file)).Should().Contain(served);
        File.ReadAllText(Path.Combine(Tests(), "planner-review.spec.ts")).Should().Contain(called);
    }

    /// <summary>
    /// What the review reads of GitHub, served by the stand-in in GitHub's words: the search, the
    /// sub-issues, a repository's <c>archived</c>, and the events a reference leaves on a timeline.
    /// </summary>
    [Theory]
    [InlineData("GitHubStandIn.Review.cs", "api.MapGet(\"/search/issues\"")]
    [InlineData("GitHubStandIn.Review.cs", "\"/repos/{owner}/{repo}/issues/{number:int}/sub_issues\"")]
    [InlineData("GitHubStandIn.Review.cs", "Query must include 'is:issue' or 'is:pull-request'")]
    [InlineData("GitHubStandIn.Review.cs", "[\"event\"] = \"cross-referenced\"")]
    [InlineData("GitHubStandIn.Review.cs", "[\"event\"] = \"referenced\"")]
    [InlineData("GitHubStandIn.cs", "[\"archived\"] = archived,")]
    [InlineData("GitHubStandIn.cs", "Repository was archived so is read-only.")]
    [InlineData("GitHubStandIn.cs", ".Concat(ReferenceEvents(org,")]
    public void The_stand_in_serves_what_the_review_reads(string file, string served)
    {
        File.ReadAllText(Path.Combine(RepoRoot(), "infra", "e2e", "notification-receiver", file)).Should().Contain(served);
    }

    /// <summary>
    /// The scripted review answers exactly the organisation's open seeded issues. One seeded and
    /// unanswered gets the phase's fallback, unsure, and moves every count the spec compares; one
    /// answered and not seeded is a verdict the agent must drop - and nothing would say so.
    /// </summary>
    [Fact]
    public void The_scripted_review_answers_every_open_issue_the_stand_in_seeds_and_no_other()
    {
        var standIn = Path.Combine(RepoRoot(), "infra", "e2e", "notification-receiver");
        var seeding = File.ReadAllText(Path.Combine(standIn, "GitHubStandIn.Project.cs")) + File.ReadAllText(Path.Combine(standIn, "GitHubStandIn.Review.cs"));

        var seeded = SeededPattern().Matches(seeding).Select(m => $"{m.Groups[1].Value}#{m.Groups[2].Value}").ToHashSet(StringComparer.Ordinal);
        var closed = ClosedPattern().Matches(seeding).Select(m => $"{m.Groups[1].Value}#{m.Groups[2].Value}").ToHashSet(StringComparer.Ordinal);

        seeded.Should().Contain(["acme/api#431", "acme/web#118", "acme/legacy-ttd#1"], "the patterns must find the seeding at all");
        closed.Should().BeEquivalentTo(["acme/planning#10", "acme/web#229"]);

        Verdicts().Select(v => v.GetProperty("issue").GetString()).Should().BeEquivalentTo(seeded.Except(closed));
    }

    /// <summary>
    /// The verdict's shape the owner decided (#312): a group of three, a reason to close only for
    /// close, a priority only for a bug or a feature, a confidence of three.
    /// </summary>
    [Fact]
    public void Every_scripted_verdict_has_the_shape_the_review_decided()
    {
        string[] reasons = ["done", "superseded", "obsolete", "duplicate", "answered", "stale"];

        foreach (var v in Verdicts())
        {
            var issue = v.GetProperty("issue").GetString();
            var group = v.GetProperty("group").GetString();

            group.Should().BeOneOf(["close", "bug", "feature"], issue);
            v.GetProperty("confidence").GetString().Should().BeOneOf(["high", "medium", "low"], issue);
            v.GetProperty("summary").GetString().Should().StartWith("FAKE SDK review", $"{issue}: nobody may mistake it for a model's");
            v.GetProperty("reason").GetString().Should().NotBeNullOrWhiteSpace(issue);

            if (group == "close")
            {
                v.GetProperty("close_reason").GetString().Should().BeOneOf(reasons, issue);
                v.GetProperty("priority").ValueKind.Should().Be(JsonValueKind.Null, issue);
            }
            else
            {
                v.GetProperty("close_reason").ValueKind.Should().Be(JsonValueKind.Null, issue);
                v.GetProperty("priority").GetString().Should().MatchRegex("^P[0-2]$", issue);
            }
        }
    }

    private static List<JsonElement> Verdicts()
    {
        using var script = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "coder", "fake-scripts", "default.review.json")));

        return [.. script.RootElement.GetProperty("steps").EnumerateArray()
            .Where(s => s.TryGetProperty("result", out _))
            .Single()
            .GetProperty("result").GetProperty("structured_output").GetProperty("verdicts")
            .EnumerateArray()
            .Select(v => v.Clone())];
    }

    private static Version Floor()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var floor = FloorPattern().Match(props);
        floor.Success.Should().BeTrue("Directory.Build.props sets MinVerMinimumMajorMinor");

        return Version.Parse(floor.Groups[1].Value);
    }

    private static List<string> KnownRedLines() =>
        [.. File.ReadLines(Path.Combine(Ui(), "KNOWN_RED"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))];

    private static List<(string Spec, string Id, Version Milestone)> KnownRed() =>
        [.. KnownRedLines()
            .Select(l => EntryPattern().Match(l))
            .Where(m => m.Success)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, Version.Parse(m.Groups[3].Value)))];

    private static string Ui() => Path.Combine(RepoRoot(), "scripts", "e2e", "ui");

    private static string Tests() => Path.Combine(Ui(), "tests");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [GeneratedRegex(@"<MinVerMinimumMajorMinor>\s*([0-9]+\.[0-9]+)\s*</MinVerMinimumMajorMinor>")]
    private static partial Regex FloorPattern();

    [GeneratedRegex(@"^([a-z0-9-]+\.spec\.ts)\s+([A-Z][0-9]{2})\s+([0-9]+\.[0-9]+)\s+\S.*$")]
    private static partial Regex EntryPattern();

    /// <summary>A seeded issue: <c>Add("acme/api", 431, ...</c>, on the board or not.</summary>
    [GeneratedRegex(@"\bAdd\(""(acme/[a-z0-9-]+)"", ([0-9]+),")]
    private static partial Regex SeededPattern();

    [GeneratedRegex(@"\bClose\(""(acme/[a-z0-9-]+)"", ([0-9]+),")]
    private static partial Regex ClosedPattern();
}
