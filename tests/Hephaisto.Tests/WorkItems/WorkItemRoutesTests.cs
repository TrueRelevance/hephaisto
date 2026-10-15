using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Web;
using Hephaisto.Agent.WorkItems;

namespace Hephaisto.Tests.WorkItems;

/// <summary>
/// The door to a work item's plan is the door to an incident's: the same verbs under the work
/// item's own path, behind the same policy. Read off the route table, because a route that was
/// mapped without its policy answers every request and fails no other test.
/// </summary>
public sealed class WorkItemRoutesTests
{
    private static List<RouteEndpoint> Routes()
    {
        var builder = WebApplication.CreateBuilder();

        // Only so that the parameters of the handlers are known to be services; nothing is resolved.
        builder.Services.AddScoped<CodeFixCoordinator>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<CodeFixQueries>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<WorkItemQueries>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<WorkItemActions>(_ => throw new NotSupportedException());

        var app = builder.Build();

        app.MapCodeFixEndpoints();
        app.MapWorkItemEndpoints();

        return [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()];
    }

    private static string Verb(RouteEndpoint e) => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single();

    private static string? Policy(RouteEndpoint e) => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).SingleOrDefault();

    [Theory]
    [InlineData("/api/workitems/{id:guid}/codefix/{attemptId:guid}/approve")]
    [InlineData("/api/workitems/{id:guid}/codefix/{attemptId:guid}/deny")]
    public void DecidingAWorkItemsPlan_IsAPost_BehindTheApproverPolicy_LikeAnIncidents(string route)
    {
        var routes = Routes();
        var workItem = routes.Should().ContainSingle(e => e.RoutePattern.RawText == route).Subject;
        var incident = routes.Should().ContainSingle(e => e.RoutePattern.RawText == route.Replace("/api/workitems/", "/api/incidents/", StringComparison.Ordinal)).Subject;

        Verb(workItem).Should().Be("POST");
        Policy(workItem).Should().Be(AuthenticationExtensions.ApprovePolicy);
        Policy(workItem).Should().Be(Policy(incident), "the same door, under another path");
        Verb(incident).Should().Be("POST");
    }

    [Fact]
    public void ReadingWorkItems_NeedsNoApprover_AndNothingElseWritesThem()
    {
        var workItems = Routes().Where(e => e.RoutePattern.RawText!.StartsWith("/api/workitems", StringComparison.Ordinal)).ToList();

        workItems.Where(e => Verb(e) == "GET").Select(e => e.RoutePattern.RawText).Should().BeEquivalentTo(["/api/workitems", "/api/workitems/{id:guid}"]);
        workItems.Where(e => Verb(e) == "GET").Should().OnlyContain(e => Policy(e) == null);
        workItems.Where(e => Verb(e) != "GET").Should().HaveCount(3).And.OnlyContain(e => Policy(e) == AuthenticationExtensions.ApprovePolicy);
        workItems.Should().ContainSingle(e => e.RoutePattern.RawText == "/api/workitems/{id:guid}/codefix/{attemptId:guid}/replan", "/replan, from the console");
    }

    [Fact]
    public void OneAttemptByItsOwnId_IsARead_WhateverItIsFor()
    {
        // GET /api/codefixes/{attemptId} (#248): the one address an attempt for an issue has in
        // the API besides its work item's. A read like the list beside it - and a guid, so it
        // can never be taken for /counts or /mode.
        var attempt = Routes().Should().ContainSingle(e => e.RoutePattern.RawText == "/api/codefixes/{attemptId:guid}").Subject;

        Verb(attempt).Should().Be("GET");
        Policy(attempt).Should().BeNull();
    }
}
