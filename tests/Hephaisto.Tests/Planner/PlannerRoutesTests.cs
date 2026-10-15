using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Hephaisto.Agent.Planner;
using Hephaisto.Agent.Web;

namespace Hephaisto.Tests.Planner;

/// <summary>
/// Reading the planner needs no approver; every write is a POST behind the approver policy. Read
/// off the route table, because a route mapped without its policy answers every request and
/// fails no other test.
/// </summary>
public sealed class PlannerRoutesTests
{
    private static List<RouteEndpoint> Routes()
    {
        var builder = WebApplication.CreateBuilder();

        // Only so that the handlers' parameter is known to be a service; nothing is resolved.
        builder.Services.AddSingleton<PlannerService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<TriageRunner>(_ => throw new NotSupportedException());

        var app = builder.Build();

        app.MapPlannerEndpoints();

        return [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()];
    }

    private static string Verb(RouteEndpoint e) => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single();

    private static string? Policy(RouteEndpoint e) => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).SingleOrDefault();

    [Fact]
    public void Reading_the_planner_is_four_gets_and_needs_no_approver()
    {
        var gets = Routes().Where(e => Verb(e) == "GET").ToList();

        gets.Select(e => e.RoutePattern.RawText).Should().BeEquivalentTo("/api/planner", "/api/planner/search", "/api/planner/comments", "/api/planner/analysis");
        gets.Should().OnlyContain(e => Policy(e) == null, "the console's read policy is the group's, as for every other read");
    }

    [Fact]
    public void Every_write_is_a_post_behind_the_approver_policy()
    {
        var writes = Routes().Where(e => Verb(e) != "GET").ToList();

        writes.Select(e => e.RoutePattern.RawText).Should().BeEquivalentTo(
        [
            "/api/planner/items/{itemId}/field",
            "/api/planner/items/{itemId}/close",
            "/api/planner/items/{itemId}/day",
            "/api/planner/items/{itemId}/assignee",
            "/api/planner/items/{itemId}/schedule",
            "/api/planner/comments",
            "/api/planner/issues",
            "/api/planner/triage",
        ]);

        writes.Should().OnlyContain(e => Verb(e) == "POST" && Policy(e) == AuthenticationExtensions.ApprovePolicy);
    }
}
