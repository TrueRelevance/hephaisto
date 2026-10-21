using System.ComponentModel;
using System.Reflection;
using Hephaisto.Agent.Mcp;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace Hephaisto.Tests.Mcp;

/// <summary>
/// The server's tools, read out of the code, against the reviewed list (#157, F5).
/// </summary>
/// <remarks>
/// <c>scripts/e2e/mcp/tools.golden.json</c> is what was reviewed: every name, its order, who may
/// call it and the description a gateway's tool search indexes. The pager suite's P30 checks the
/// same against a running install; this checks it on every build, before anything is installed.
/// A tool added in code and not in the file, or described differently, fails here.
/// </remarks>
public sealed class McpToolSurfaceTests
{
    private static readonly Assembly Agent = typeof(McpCatalogue).Assembly;

    private static IEnumerable<(string Name, string Needs, bool ReadOnly, string Description, MethodInfo Method)> Registered() =>
        from type in Agent.GetTypes()
        where type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null
        from method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        let tool = method.GetCustomAttribute<McpServerToolAttribute>()
        where tool is not null
        let policy = method.GetCustomAttribute<AuthorizeAttribute>()?.Policy
        select (
            tool.Name!,
            policy switch
            {
                null => McpCatalogue.Reader,
                McpExtensions.WritePolicy => McpCatalogue.Write,
                McpExtensions.ApprovePolicy => McpCatalogue.Approver,
                _ => "unknown policy " + policy,
            },
            tool.ReadOnly,
            method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
            method);

    [Fact]
    public void The_registered_tools_are_the_reviewed_list()
    {
        var registered = Registered().ToDictionary(t => t.Name);
        var golden = McpSpecification.Golden();

        registered.Keys.Should().BeEquivalentTo(golden.Select(t => t.Name));

        foreach (var tool in golden)
        {
            var live = registered[tool.Name];
            live.Needs.Should().Be(tool.Needs, $"{tool.Name}: who may call it is part of the review");
            live.ReadOnly.Should().Be(tool.ReadOnly, $"{tool.Name}: the read-only hint a client trusts");
            live.Description.Should().Be(tool.Description, $"{tool.Name}: the description is what tool search indexes");
        }
    }

    [Fact]
    public void The_catalogue_order_is_the_reviewed_order()
    {
        McpCatalogue.Tools.Select(t => (t.Name, t.Needs)).Should().Equal(McpSpecification.Golden().Select(t => (t.Name, t.Needs)));
    }

    [Fact]
    public void Every_tool_names_itself_explicitly()
    {
        // A name derived from the method would change the day somebody renames a method, and the
        // gateway, the hook that allows reads by prefix and every saved prompt would lose the tool.
        Registered().Should().OnlyContain(t => !string.IsNullOrEmpty(t.Name));
    }

    [Fact]
    public void A_tool_reaches_incidents_only_through_the_two_facades()
    {
        // IncidentQueries also approves actions and re-arms the mode; CodeFixCoordinator decides
        // plans. A tool that could inject either is one line from a door that must stay shut. The
        // planner's facade (#307) holds the planner's service, which neither approves nor plans.
        Type[] allowed = [typeof(McpIncidentReader), typeof(McpIncidentActions), typeof(McpPlanner), typeof(Hephaisto.Core.Abstractions.IClock)];

        var toolTypes = Agent.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null).ToList();
        toolTypes.Should().NotBeEmpty();

        foreach (var type in toolTypes)
        {
            foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()))
            {
                allowed.Should().Contain(parameter.ParameterType, $"{type.Name} takes a {parameter.ParameterType.Name}");
            }
        }

        foreach (var method in Registered().Select(t => t.Method))
        {
            foreach (var parameter in method.GetParameters())
            {
                parameter.ParameterType.Namespace.Should().NotBe("Hephaisto.Agent.Web", $"{method.Name} binds {parameter.ParameterType.Name}");
            }
        }
    }

    [Fact]
    public void The_write_facade_has_exactly_the_six_changes_and_the_bulk_close()
    {
        typeof(McpIncidentActions)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Should().BeEquivalentTo(
                "AcknowledgeAsync", "AssignAsync", "CloseAsync", "CloseManyAsync", "AddNoteEntryAsync", "FeedbackAsync", "ReinvestigateAsync");
    }

    [Fact]
    public void The_planner_facade_reaches_nothing_but_the_planner()
    {
        // Its writes are the planner's own clicks: no incident queries, no code-fix coordinator,
        // no database - a door to any of them would be one the catalogue tests cannot see.
        var parameters = typeof(McpPlanner).GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).ToList();

        parameters.Should().Contain(typeof(Hephaisto.Agent.Planner.PlannerService));
        parameters.Should().NotContain(t => t.Namespace == "Hephaisto.Agent.Web"
            || t.Namespace == "Hephaisto.Agent.CodeFix"
            || t.Namespace == "Hephaisto.Agent.Persistence"
            || t == typeof(Hephaisto.Agent.Planner.TriageRunner));

        typeof(McpPlanner)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Should().BeEquivalentTo("ListAsync", "GetAsync", "FieldsAsync", "CreateAsync", "UpdateAsync", "CloseAsync", "CommentAsync");
    }

    [Fact]
    public void The_reader_writes_nothing()
    {
        // Its methods read; a Save anywhere in it is a write through the back door.
        var source = File.ReadAllText(Path.Combine(McpSpecification.Dir(), "..", "..", "..", "src", "Hephaisto.Agent", "Mcp", "McpIncidentReader.cs"))
            + File.ReadAllText(Path.Combine(McpSpecification.Dir(), "..", "..", "..", "src", "Hephaisto.Agent", "Mcp", "McpIncidentReader.Deep.cs"))
            + File.ReadAllText(Path.Combine(McpSpecification.Dir(), "..", "..", "..", "src", "Hephaisto.Agent", "Mcp", "McpIncidentReader.WorkItems.cs"));

        // Every part of the reader is in that text: a new partial file is read here too.
        Directory.GetFiles(Path.Combine(McpSpecification.Dir(), "..", "..", "..", "src", "Hephaisto.Agent", "Mcp"), "McpIncidentReader*.cs")
            .Should().HaveCount(3, "a fourth part of the reader has to be added to the files this test reads");

        source.Should().NotContain("SaveChanges").And.NotContain("ExecuteUpdate").And.NotContain("ExecuteDelete")
            .And.NotContain("IAuditRepository");
        System.Text.RegularExpressions.Regex.IsMatch(source, @"\bdb(\.\w+)?\.(Add|AddRange|Remove|RemoveRange|Update|Attach)\(")
            .Should().BeFalse("the reader's context is for reading");
        System.Text.RegularExpressions.Regex.IsMatch("db.Incidents.Add(x)", @"\bdb(\.\w+)?\.(Add|AddRange|Remove|RemoveRange|Update|Attach)\(")
            .Should().BeTrue("the guard itself can fail");
    }
}
