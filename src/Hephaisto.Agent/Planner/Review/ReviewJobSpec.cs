using k8s.Models;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.GitHub;

namespace Hephaisto.Agent.Planner.Review;

/// <summary>
/// One batch of the weekly review (#312): the coder's hardened pod, told to give each issue of the
/// batch a verdict and to write nothing. <see cref="TriageJobSpec"/>'s shape, for the same reasons:
/// <see cref="CodeFixJobSpec.Hardened"/> is what makes it safe, and the coder's app label puts it
/// under the coder's NetworkPolicy.
/// </summary>
/// <remarks>
/// Two containers. <c>prepare</c> holds <c>GITHUB_TOKEN</c> and clones dev-context and, blobless
/// and read-only, the repositories the request offers - so the token needs read on every one of
/// them. <c>coder</c> holds the model's credential and nothing else, and prints the answer. No
/// <c>publish</c>: the review pushes nothing, and the publish role refuses phase review. The
/// workspace is larger than triage's for the clones: the blobs of each repository's newest
/// commits are fetched up front, and a batch can span a handful of repositories.
/// </remarks>
public static class ReviewJobSpec
{
    public const string ReviewLabel = "hephaisto.dev/review";

    /// <summary>The run a batch belongs to. An annotation, beside the label that names the attempt.</summary>
    public const string RunAnnotation = "hephaisto.dev/review-run";

    public const string WorkspaceSize = "4Gi";

    public static readonly IReadOnlyList<string> PrepareSecretKeys = ["GITHUB_TOKEN"];

    /// <summary><c>review-&lt;id12&gt;</c>.</summary>
    public static string JobName(Guid attemptId) => $"review-{CodeFixJobSpec.Id12(attemptId)}";

    public static string ConfigMapName(Guid attemptId) => JobName(attemptId) + "-req";

    public static V1Job Job(Guid attemptId, Guid runId, GitHubPlannerReviewOptions review, CodeFixOptions o)
    {
        ArgumentNullException.ThrowIfNull(review);
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
                review.Deadline,
                WorkspaceSize,
                NugetCacheClaim: null),
            o);

        job.Metadata.Annotations ??= new Dictionary<string, string>();
        job.Metadata.Annotations[RunAnnotation] = runId.ToString();

        return job;
    }

    public static V1ConfigMap RequestConfigMap(Guid attemptId, CodeFixOptions o, string requestJson, V1Job owner) =>
        CodeFixJobSpec.RequestConfigMap(ConfigMapName(attemptId), Labels(attemptId), o.Namespace, requestJson, owner);

    public static Dictionary<string, string> Labels(Guid attemptId) => new()
    {
        ["app.kubernetes.io/name"] = CodeFixJobSpec.AppLabel,
        ["app.kubernetes.io/managed-by"] = "hephaisto",
        [CodeFixJobSpec.AttemptLabel] = attemptId.ToString(),
        [ReviewLabel] = attemptId.ToString(),
        [CodeFixJobSpec.PhaseLabel] = "review",
    };
}
