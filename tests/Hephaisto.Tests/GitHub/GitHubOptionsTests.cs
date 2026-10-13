using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.Observability;
using Hephaisto.Agent.Web;
using Hephaisto.Agent.WorkItems;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.GitHub;

/// <summary>
/// <see cref="GitHubOptions"/> binds from the key shape the chart emits, and an enabled section
/// that cannot work is refused at startup with a sentence that says what to set.
/// </summary>
/// <remarks>
/// Both halves are here for the reason <c>NotificationOptionsBindingTests</c> exists: options
/// that are never bound resolve to their defaults without a word, and the default of this
/// feature is to do nothing - which is also what a broken configuration of it would look like.
/// </remarks>
public sealed class GitHubOptionsTests
{
    private static ServiceProvider Services(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddHephaistoGitHub(configuration);

        return services.BuildServiceProvider();
    }

    private static readonly (string Key, string Value)[] Working =
    [
        ("GitHub:Enabled", "true"),
        ("GitHub:Token", "not-a-real-token"),
        ("GitHub:Repositories:0", "Flou21/hephaisto-fixture-dotnet"),
    ];

    private static (string Key, string Value)[] With(params (string Key, string Value)[] more) =>
        [.. Working.Where(w => more.All(m => m.Key != w.Key)), .. more.Where(m => m.Value != "<unset>")];

    [Fact]
    public void The_charts_key_shape_binds()
    {
        // Exactly what templates/deployment.yaml renders for the github block.
        var options = Services(
            ("GitHub:Enabled", "true"),
            ("GitHub:ApiBaseUrl", "http://github-stand-in.hephaisto-obs:8080/github/api"),
            ("GitHub:Token", "stand-in-github-token"),
            ("GitHub:BotLogin", "hephaisto-bot"),
            ("GitHub:Repositories:0", "Flou21/hephaisto-fixture-dotnet"),
            ("GitHub:Repositories:1", "TrueRelevance/dev-context"),
            ("GitHub:PollInterval", "00:00:05"),
            ("GitHub:ProxyUrl", "http://hephaisto-coder-egress.hephaisto-coder.svc:3128"),
            ("GitHub:Approvers:0", "1001"),
            ("GitHub:Approvers:1", "2002")).GetRequiredService<IOptions<GitHubOptions>>().Value;

        options.Enabled.Should().BeTrue();
        options.ApiBase().Should().Be(new Uri("http://github-stand-in.hephaisto-obs:8080/github/api/"));
        options.BotLogin.Should().Be("hephaisto-bot");
        options.Repositories.Should().Equal("Flou21/hephaisto-fixture-dotnet", "TrueRelevance/dev-context");
        options.PollInterval.Should().Be(TimeSpan.FromSeconds(5));
        options.ProxyUrl.Should().Be("http://hephaisto-coder-egress.hephaisto-coder.svc:3128");
        options.ApproverIds().Should().BeEquivalentTo([1001L, 2002L]);
        options.Problems().Should().BeEmpty();
    }

    [Fact]
    public void Unset_it_is_off_points_at_github_and_asks_once_a_minute()
    {
        var options = Services().GetRequiredService<IOptions<GitHubOptions>>().Value;

        options.Enabled.Should().BeFalse();
        options.ApiBaseUrl.Should().Be("https://api.github.com");
        options.PollInterval.Should().Be(TimeSpan.FromMinutes(1));
        options.Repositories.Should().BeEmpty();
        options.Approvers.Should().BeEmpty();
    }

    [Fact]
    public void Off_nothing_about_it_is_checked_and_nothing_of_it_runs()
    {
        // A token-less, repository-less section is every install that never heard of this.
        using var services = Services(("GitHub:PollInterval", "00:00:01"), ("GitHub:Approvers:0", "somebody"));

        services.GetRequiredService<IOptions<GitHubOptions>>().Value.Problems().Should().BeEmpty();
        services.GetService<IGitHubClient>().Should().BeNull("an install that did not enable this holds no GitHub client");
        Registered(Array.Empty<(string, string)>()).Should().NotContain(d => d.ImplementationType == typeof(GitHubIssuePoller));
    }

    [Fact]
    public void On_it_has_a_client_and_the_poller()
    {
        using var services = Services(Working);
        using var scope = services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IGitHubClient>().Should().BeOfType<GitHubClient>();

        // By descriptor: the poller itself needs the kill switch and the database, which the
        // composition root registers and this test does not.
        Registered(Working).Should().ContainSingle(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(GitHubIssuePoller));
    }

    private static ServiceCollection Registered((string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddHephaistoGitHub(configuration);

        return services;
    }

    [Theory]
    [InlineData("GitHub:Token", "<unset>", "GitHub:Enabled needs GitHub:Token")]
    [InlineData("GitHub:Token", "   ", "GitHub:Enabled needs GitHub:Token")]
    [InlineData("GitHub:Repositories:0", "<unset>", "needs at least one entry in GitHub:Repositories")]
    [InlineData("GitHub:Repositories:0", "https://github.com/TrueRelevance/hephaisto", "is not owner/repo")]
    [InlineData("GitHub:Repositories:0", "hephaisto", "is not owner/repo")]
    [InlineData("GitHub:Approvers:0", "maintainer", "is not an account number")]
    [InlineData("GitHub:Approvers:0", "-5", "is not an account number")]
    [InlineData("GitHub:PollInterval", "00:00:01", "GitHub:PollInterval must be at least 5 seconds")]
    [InlineData("GitHub:ApiBaseUrl", "api.github.com", "is not an absolute http(s) URL")]
    [InlineData("GitHub:ProxyUrl", "coder-egress:3128", "is not an absolute http(s) URL")]
    public void Enabled_and_unable_to_work_is_refused_at_startup_with_one_sentence(string key, string value, string sentence)
    {
        using var services = Services(With((key, value)));

        var act = () => services.GetRequiredService<IOptions<GitHubOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle()
            .Which.Should().Contain(sentence);
    }

    [Fact]
    public void A_refusal_never_repeats_the_token()
    {
        using var services = Services(With(("GitHub:Token", "ghp_thisMustNotBeEchoedAnywhere12345"), ("GitHub:Approvers:0", "x")));

        var act = () => services.GetRequiredService<IOptions<GitHubOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().Which.Message.Should().NotContain("ghp_");
    }

    // --- the planner (v0.15.0) ---------------------------------------------------------------

    [Fact]
    public void The_planner_binds_from_the_keys_the_chart_emits_and_is_off_until_asked_for()
    {
        using var off = Services(Working);
        off.GetRequiredService<IOptions<GitHubOptions>>().Value.Planner.Enabled.Should().BeFalse();

        using var services = Services(With(
            ("GitHub:Planner:Enabled", "true"),
            ("GitHub:Planner:Owner", "example"),
            ("GitHub:Planner:Number", "3"),
            ("GitHub:Planner:StaleAfterDays", "7"),
            ("GitHub:Planner:RequiredFields:0", "Priority"),
            ("GitHub:Planner:RequiredFields:1", "Area"),
            ("GitHub:Planner:Assignees:0", "octocat")));

        var planner = services.GetRequiredService<IOptions<GitHubOptions>>().Value.Planner;

        planner.Enabled.Should().BeTrue();
        planner.Owner.Should().Be("example");
        planner.Number.Should().Be(3);
        planner.StaleAfterDays.Should().Be(7);
        planner.RequiredFields.Should().Equal("Priority", "Area");
        planner.Assignees.Should().Equal("octocat");
    }

    [Theory]
    [InlineData("GitHub:Planner:Owner", "https://github.com/example", "is not an organisation's login")]
    [InlineData("GitHub:Planner:Owner", "", "is not an organisation's login")]
    [InlineData("GitHub:Planner:Number", "0", "must be the project's number")]
    [InlineData("GitHub:Planner:StaleAfterDays", "0", "at least 1")]
    [InlineData("GitHub:Planner:Assignees:0", "some one", "is not a GitHub login")]
    public void A_planner_that_cannot_work_is_refused_with_what_to_set(string key, string value, string says)
    {
        (string Key, string Value)[] planner =
            [("GitHub:Planner:Enabled", "true"), ("GitHub:Planner:Owner", "example"), ("GitHub:Planner:Number", "3")];

        using var services = Services(With([.. planner.Where(p => p.Key != key), (key, value)]));

        var act = () => services.GetRequiredService<IOptions<GitHubOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain(says);
    }

    [Fact]
    public void A_planner_without_github_is_the_one_fault_an_install_with_github_off_can_have()
    {
        using var services = Services(
            ("GitHub:Planner:Enabled", "true"), ("GitHub:Planner:Owner", "example"), ("GitHub:Planner:Number", "3"));

        var act = () => services.GetRequiredService<IOptions<GitHubOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("needs GitHub:Enabled");
    }

    [Fact]
    public void A_planner_that_is_off_is_never_a_fault_whatever_else_it_says()
    {
        using var services = Services(With(("GitHub:Planner:Owner", "not a login"), ("GitHub:Planner:Number", "-1")));

        var act = () => services.GetRequiredService<IOptions<GitHubOptions>>().Value;

        act.Should().NotThrow();
    }

    /// <summary>
    /// The first connection probe is registered with a TryAdd on the service type, so a probe
    /// registered before the web layer runs would silently take Postgres's place in the panel.
    /// </summary>
    [Fact]
    public void GitHub_joins_the_connection_probes_without_displacing_one()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        // The composition root's order.
        services.AddHephaistoGitHub(configuration);
        services.AddHephaistoWeb();

        var probes = services.Where(d => d.ServiceType == typeof(IConnectionProbe)).Select(d => d.ImplementationType).ToArray();

        probes.Should().Contain(typeof(PostgresProbe)).And.Contain(typeof(GitHubProbe));
        services.Should().Contain(d => d.ServiceType == typeof(WorkItemQueries), "the API answers an empty list where this was never enabled");
    }
}
