using System.Text.RegularExpressions;

namespace Hephaisto.Tests.Mcp;

/// <summary>
/// The rules the reviewed tool list keeps, whatever gets added to it later.
/// </summary>
/// <remarks>
/// <para>
/// <b>The doors stay shut (#157, F5).</b> No tool approves or denies an action or a code-fix
/// plan, re-arms the kill switch, sets a mode, executes, deletes or creates an incident - by its
/// name, or by what a writing tool says it does. Read descriptions may say what the server
/// cannot do ("Read only: this server cannot approve, deny or execute an action"): that is a
/// sentence a model should read.
/// </para>
/// <para>
/// <b>A name says whether it reads.</b> Every read starts with a read verb and no write does, so
/// a person's client can allow reads by prefix - the hook that auto-approves a gateway's
/// read-only calls does exactly that.
/// </para>
/// </remarks>
public sealed partial class McpToolCatalogueTests
{
    private static readonly string[] ReadVerbs = ["get_", "list_", "search_", "count_", "lookup_", "fetch_"];

    [Fact]
    public void No_tool_is_named_for_a_door_that_stays_shut()
    {
        McpSpecification.Golden().Select(t => t.Name).Where(n => ForbiddenName().IsMatch(n))
            .Should().BeEmpty();
    }

    [Fact]
    public void No_writing_tool_says_it_approves_denies_re_arms_or_sets_a_mode()
    {
        McpSpecification.Golden().Where(t => !t.ReadOnly)
            .Where(t => ForbiddenDeed().IsMatch(t.Description))
            .Select(t => t.Name)
            .Should().BeEmpty();
    }

    [Fact]
    public void The_door_guards_can_fail()
    {
        ForbiddenName().IsMatch("approve_action").Should().BeTrue();
        ForbiddenName().IsMatch("re_arm_agent").Should().BeTrue();
        ForbiddenName().IsMatch("set_mode").Should().BeTrue();
        ForbiddenName().IsMatch("create_incident").Should().BeTrue();
        ForbiddenName().IsMatch("close_incident").Should().BeFalse();

        ForbiddenDeed().IsMatch("Approve the proposed action.").Should().BeTrue();
        ForbiddenDeed().IsMatch("Re-arm the kill switch.").Should().BeTrue();
        ForbiddenDeed().IsMatch("Sets the mode of the agent.").Should().BeTrue();
        ForbiddenDeed().IsMatch("Needs the approver role.").Should().BeFalse("approver is a role, not a deed");
    }

    [Fact]
    public void Every_read_starts_with_a_read_verb_and_no_write_does()
    {
        foreach (var tool in McpSpecification.Golden())
        {
            var readVerb = ReadVerbs.Any(v => tool.Name.StartsWith(v, StringComparison.Ordinal));

            readVerb.Should().Be(tool.ReadOnly, $"{tool.Name} is {(tool.ReadOnly ? "a read" : "a write")}");
        }
    }

    [Fact]
    public void Who_may_call_each_tool_is_one_of_three_and_agrees_with_read_only()
    {
        foreach (var tool in McpSpecification.Golden())
        {
            tool.Needs.Should().BeOneOf("reader", "write", "approver");
            (tool.Needs == "reader").Should().Be(tool.ReadOnly, tool.Name);
        }
    }

    [Fact]
    public void The_writes_are_the_six_the_roadmap_names_the_bulk_close_and_the_planners_four()
    {
        // The planner's (#307) open, change, close and comment on a board's GitHub issues - the
        // clicks a person makes in the console's planner. None of them answers an issue's plan.
        McpSpecification.Golden().Where(t => !t.ReadOnly).Select(t => t.Name).Should().Equal(
            "acknowledge_incident",
            "assign_incident",
            "close_incident",
            "add_alert_note_entry",
            "submit_incident_feedback",
            "reinvestigate_incident",
            "close_incidents",
            "create_planner_issue",
            "update_planner_issue",
            "close_planner_issue",
            "comment_on_planner_issue");

        McpSpecification.Golden().Where(t => t.Needs == "approver").Select(t => t.Name)
            .Should().Equal("close_incident", "reinvestigate_incident", "close_incidents");
    }

    [Fact]
    public void Names_are_unique_and_descriptions_are_long_enough_to_be_found()
    {
        var tools = McpSpecification.Golden();

        tools.Select(t => t.Name).Should().OnlyHaveUniqueItems();
        tools.Should().OnlyContain(t => t.Description.Length >= 120,
            "a gateway's tool search indexes nothing but the name and the description");
        tools.Should().OnlyContain(t => NamePattern().IsMatch(t.Name));
    }

    [GeneratedRegex(@"approve|deny|re_?arm|mode|kill|execute|delete|create_incident|open_incident")]
    private static partial Regex ForbiddenName();

    [GeneratedRegex(@"\b(approve|approves|deny|denies|re-?arms?|executes?)\b|\bsets? (the |a )?mode\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenDeed();

    [GeneratedRegex(@"^[a-z]+(_[a-z]+)+$")]
    private static partial Regex NamePattern();
}
