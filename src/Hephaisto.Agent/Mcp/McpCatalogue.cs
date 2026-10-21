using ModelContextProtocol.Protocol;

namespace Hephaisto.Agent.Mcp;

/// <summary>
/// The tools, in the order a caller sees them, and who may call each.
/// </summary>
/// <remarks>
/// <para>
/// The order is not cosmetic. A gateway's tool search scores tools by the words of a question and
/// breaks ties by registry order, and a write-intent question ("acknowledge the incident") scores
/// the same on the write tool as on half the reads. So the writes sit right after the three tools
/// every conversation starts with, where a tie still reaches the top five. The planner's tools
/// (#307) sit together after the work items: their questions - an issue, today, the backlog -
/// share few words with an incident's.
/// </para>
/// <para>
/// <c>scripts/e2e/mcp/tools.golden.json</c> is the reviewed copy of this list; a unit test and the
/// pager suite's P30 hold the server to it. A tool added here and not there fails both.
/// </para>
/// </remarks>
public static class McpCatalogue
{
    public const string Reader = "reader";
    public const string Write = "write";
    public const string Approver = "approver";

    /// <summary>Every tool, in order, with who may call it.</summary>
    public static readonly IReadOnlyList<(string Name, string Needs)> Tools =
    [
        ("search_incidents", Reader),
        ("get_incident", Reader),
        ("count_incidents", Reader),
        ("acknowledge_incident", Write),
        ("assign_incident", Write),
        ("close_incident", Approver),
        ("add_alert_note_entry", Write),
        ("submit_incident_feedback", Write),
        ("reinvestigate_incident", Approver),
        ("close_incidents", Approver),
        ("get_incident_history", Reader),
        ("get_incident_findings", Reader),
        ("get_investigation", Reader),
        ("fetch_evidence_blob", Reader),
        ("get_incident_signals", Reader),
        ("get_incident_actions", Reader),
        ("get_incident_timeline", Reader),
        ("list_incident_notifications", Reader),
        ("get_alert_note", Reader),
        ("list_code_fixes", Reader),
        ("get_code_fix", Reader),
        ("list_work_items", Reader),
        ("get_work_item", Reader),
        ("list_planner_issues", Reader),
        ("get_planner_issue", Reader),
        ("lookup_planner_fields", Reader),
        ("create_planner_issue", Write),
        ("update_planner_issue", Write),
        ("close_planner_issue", Write),
        ("comment_on_planner_issue", Write),
        ("get_status", Reader),
        ("lookup_incident_filters", Reader),
        ("get_caller_identity", Reader),
    ];

    /// <summary>Whether <paramref name="caller"/> may call a tool that needs <paramref name="needs"/>.</summary>
    public static bool Allows(McpCaller caller, string needs)
    {
        ArgumentNullException.ThrowIfNull(caller);

        return needs switch
        {
            Reader => true,
            Write => caller.MayWrite,
            Approver => caller.MayWrite && caller.IsApprover,
            _ => false,
        };
    }

    /// <summary>The tools <paramref name="caller"/> may call, in order.</summary>
    public static IReadOnlyList<string> For(McpCaller caller) =>
        [.. Tools.Where(t => Allows(caller, t.Needs)).Select(t => t.Name)];

    /// <summary>Puts a tools/list answer in catalogue order; a tool not in the catalogue goes last.</summary>
    internal static void Sort(ListToolsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sorted = result.Tools
            .Select((tool, i) => (tool, i))
            .OrderBy(x => Position(x.tool.Name))
            .ThenBy(x => x.i)
            .Select(x => x.tool)
            .ToList();

        result.Tools.Clear();

        foreach (var tool in sorted)
        {
            result.Tools.Add(tool);
        }
    }

    private static int Position(string name)
    {
        for (var i = 0; i < Tools.Count; i++)
        {
            if (Tools[i].Name == name)
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
