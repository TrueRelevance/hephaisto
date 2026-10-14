using k8s.Models;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.GitHub;

namespace Hephaisto.Agent.Planner;

/// <summary>
/// The triage Job (v0.15.0): the coder's hardened pod, told to label and analyse one issue.
/// </summary>
/// <remarks>
/// <para>
/// Everything that makes the pod safe is <see cref="CodeFixJobSpec.Hardened"/>, shared on purpose.
/// It carries the coder's app label, so the coder's NetworkPolicy is the one that holds for it:
/// egress through the proxy, to GitHub and to the model, and nowhere in the cluster.
/// </para>
/// <para>
/// Two containers. <c>prepare</c> holds <c>GITHUB_TOKEN</c> and clones dev-context, which is
/// private in production. <c>coder</c> holds the model's credential and nothing else, reads
/// dev-context and prints the answer. No repository is cloned: an issue on a board names a
/// repository, often none that holds code, and what Claude needs to place it is the team's notes.
/// </para>
/// </remarks>
public static class TriageJobSpec
{
    public const string TriageLabel = "hephaisto.dev/triage";

    /// <summary>The issue, <c>owner/repo#n</c>. An annotation: a label value cannot hold a slash or a hash.</summary>
    public const string IssueAnnotation = "hephaisto.dev/issue";

    public static readonly IReadOnlyList<string> PrepareSecretKeys = ["GITHUB_TOKEN"];

    /// <summary><c>triage-&lt;id12&gt;</c>.</summary>
    public static string JobName(Guid attemptId) => $"triage-{CodeFixJobSpec.Id12(attemptId)}";

    public static string ConfigMapName(Guid attemptId) => JobName(attemptId) + "-req";

    public static V1Job Job(Guid attemptId, string issue, GitHubPlannerTriageOptions triage, CodeFixOptions o)
    {
        ArgumentNullException.ThrowIfNull(triage);
        ArgumentNullException.ThrowIfNull(o);

        var env = new List<V1EnvVar>
        {
            new() { Name = "CODEFIX_REQUEST", Value = "/work/in/request.json" },
            new() { Name = "CODEFIX_SDK", Value = string.IsNullOrWhiteSpace(o.Sdk) ? "real" : o.Sdk },
        };

        env.AddRange(CodeFixJobSpec.ProxyEnv(o.EgressProxyUrl, noProxy: []));

        var job = CodeFixJobSpec.Hardened(
            new CodeFixJobSpec.JobShape(
                JobName(attemptId),
                ConfigMapName(attemptId),
                Labels(attemptId),
                env,
                [
                    new CodeFixJobSpec.ContainerShape(CodeFixJobSpec.PrepareContainerName, "prepare", PrepareSecretKeys, []),
                    new CodeFixJobSpec.ContainerShape(
                        CodeFixJobSpec.ContainerName, "coder", CodeFixJobSpec.CoderKeysFor(o.Sdk), CodeFixJobSpec.CoderEnv(o.Model)),
                ],
                triage.Deadline,
                // dev-context and nothing else: a fraction of what a coder's workspace holds
                "1Gi",
                NugetCacheClaim: null),
            o);

        job.Metadata.Annotations ??= new Dictionary<string, string>();
        job.Metadata.Annotations[IssueAnnotation] = issue;

        return job;
    }

    public static V1ConfigMap RequestConfigMap(Guid attemptId, CodeFixOptions o, string requestJson, V1Job owner) =>
        CodeFixJobSpec.RequestConfigMap(ConfigMapName(attemptId), Labels(attemptId), o.Namespace, requestJson, owner);

    public static Dictionary<string, string> Labels(Guid attemptId) => new()
    {
        ["app.kubernetes.io/name"] = CodeFixJobSpec.AppLabel,
        ["app.kubernetes.io/managed-by"] = "hephaisto",
        [CodeFixJobSpec.AttemptLabel] = attemptId.ToString(),
        [TriageLabel] = attemptId.ToString(),
        [CodeFixJobSpec.PhaseLabel] = "triage",
    };
}
