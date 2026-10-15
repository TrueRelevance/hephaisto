using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;

namespace Hephaisto.Agent.Components;

/// <summary>
/// GitHub's markdown as the console shows it: an issue's description, its comments, Claude's
/// analysis, a pull request's description. Somebody else wrote every word of it - a stranger
/// who opened an issue, a model - so it is rendered with three rules and no exception.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>No HTML of its own.</b> Raw HTML and HTML entities that spell markup are text
/// (<c>DisableHtml</c>): a <c>&lt;script&gt;</c>, a <c>&lt;b&gt;</c> and an <c>onerror</c>
/// arrive as the characters they are.</item>
/// <item><b>A link is a link only to http(s) or mailto.</b> Any other scheme - <c>javascript:</c>,
/// <c>data:</c>, a relative path - leaves the link's text and drops the link. Every link opens
/// in a new tab, with <c>noopener noreferrer nofollow</c>.</item>
/// <item><b>No image is loaded.</b> An image becomes a link to it, named by its alt text: a
/// picture fetched from somewhere tells that somewhere who looked, and when.</item>
/// </list>
/// <para>GitHub's flavour beyond CommonMark: tables, task lists, strikethrough, bare URLs, and a
/// line break where the text has one, as comments are written.</para>
/// <para><b>An issue reference is a link</b>, as on GitHub: <c>#12</c> in the repository the text
/// belongs to, <c>owner/repo#12</c> anywhere, and the address of an issue or a pull request. One
/// the console knows - an issue of the planner's board - leads to its page in the console, in the
/// same tab; any other to GitHub. Inside code and inside a link nothing is turned into one.</para>
/// <para><c>&lt;sub&gt;</c> and <c>&lt;sup&gt;</c> - the small print GitHub comments end with,
/// Hephaisto's own among them - are dropped as tags and their text kept.</para>
/// </remarks>
public static partial class SafeMarkdown
{
    /// <summary>The longest text rendered. Longer is cut, and says so.</summary>
    public const int MaxLength = 60_000;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    /// <param name="repository"><c>owner/repo</c> the text belongs to: what a bare <c>#12</c> means. Null: a bare one stays text.</param>
    /// <param name="internalLink">The console's own address of an issue, or null for one it does not know.</param>
    public static MarkupString Render(string? markdown, string? repository = null, Func<string, int, string?>? internalLink = null) =>
        new(ToHtml(markdown, repository, internalLink));

    public static string ToHtml(string? markdown, string? repository = null, Func<string, int, string?>? internalLink = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var text = markdown.Length <= MaxLength ? markdown : string.Concat(markdown.AsSpan(0, MaxLength), "\n\n…");
        text = SmallPrint().Replace(HtmlComment().Replace(text, string.Empty), string.Empty);
        var document = Markdown.Parse(text, Pipeline);

        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (!Allowed(link.Url))
            {
                Unwrap(link);
                continue;
            }

            if (link.IsImage)
            {
                // The picture's address, as a link named by what it shows.
                link.IsImage = false;

                if (link.FirstChild is null)
                {
                    link.AppendChild(new LiteralInline("image"));
                }
            }

            // An issue the console knows opens in the console.
            if (!link.IsImage && IssueOf(link.Url) is { } issue && internalLink?.Invoke(issue.Repository, issue.Number) is { } inside)
            {
                link.Url = inside;
                continue;
            }

            Mark(link.GetAttributes());
        }

        foreach (var link in document.Descendants<AutolinkInline>().ToList())
        {
            if (!link.IsEmail && IssueOf(link.Url) is { } issue && internalLink?.Invoke(issue.Repository, issue.Number) is { } inside)
            {
                var replaced = new LinkInline(inside, string.Empty);
                replaced.AppendChild(new LiteralInline(link.Url));
                link.ReplaceBy(replaced);
            }
            else if (link.IsEmail || Allowed(link.Url))
            {
                Mark(link.GetAttributes());
            }
            else
            {
                link.ReplaceBy(new LiteralInline(link.Url));
            }
        }

        LinkReferences(document, repository, internalLink);

        return document.ToHtml(Pipeline);
    }

    /// <summary>
    /// <c>#12</c> and <c>owner/repo#12</c> in plain text, turned into links. Text inside a link is
    /// left alone, and code is never plain text.
    /// </summary>
    private static void LinkReferences(MarkdownDocument document, string? repository, Func<string, int, string?>? internalLink)
    {
        foreach (var literal in document.Descendants<LiteralInline>().ToList())
        {
            if (InsideLink(literal))
            {
                continue;
            }

            var content = literal.Content.ToString();
            var matches = Reference().Matches(content).Where(m => m.Groups["repo"].Success || repository is not null).ToList();

            if (matches.Count == 0)
            {
                continue;
            }

            Inline at = literal;
            var from = 0;

            foreach (var match in matches)
            {
                if (match.Index > from)
                {
                    at = Insert(at, new LiteralInline(content[from..match.Index]));
                }

                var repo = match.Groups["repo"].Success ? match.Groups["repo"].Value : repository!;
                var number = int.Parse(match.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var inside = internalLink?.Invoke(repo, number);
                var link = new LinkInline(inside ?? $"https://github.com/{repo}/issues/{number}", string.Empty);

                link.AppendChild(new LiteralInline(match.Value));

                if (inside is null)
                {
                    Mark(link.GetAttributes());
                }

                at = Insert(at, link);
                from = match.Index + match.Length;
            }

            if (from < content.Length)
            {
                Insert(at, new LiteralInline(content[from..]));
            }

            literal.Remove();
        }

        static Inline Insert(Inline after, Inline inline)
        {
            after.InsertAfter(inline);
            return inline;
        }
    }

    private static bool InsideLink(Inline inline)
    {
        for (var parent = inline.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is LinkInline)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>owner/repo</c> of a repository's address on github.com, or null for any other host.</summary>
    public static string? RepositoryOf(string? url) =>
        url is not null && RepositoryUrl().Match(url.Trim()) is { Success: true } m ? m.Groups["repo"].Value : null;

    /// <summary>The issue or pull request an address of github.com names, or null.</summary>
    public static (string Repository, int Number)? IssueOf(string? url) =>
        url is not null && IssueUrl().Match(url.Trim()) is { Success: true } m
            ? (m.Groups["repo"].Value, int.Parse(m.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture))
            : null;

    // #12 or owner/repo#12, not inside a word, not part of an address, not "C#1".
    [GeneratedRegex(@"(?<![\w/#&])(?:(?<repo>[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+))?#(?<number>\d{1,7})\b")]
    private static partial Regex Reference();

    [GeneratedRegex(@"^https://github\.com/(?<repo>[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+)/(?:issues|pull)/(?<number>\d{1,7})/?(?:[#?].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex IssueUrl();

    [GeneratedRegex(@"^https://github\.com/(?<repo>[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex RepositoryUrl();

    // An HTML comment is invisible on GitHub - Hephaisto's own markers are ones - so it is here too.
    [GeneratedRegex(@"<!--[\s\S]*?-->")]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"</?(?:sub|sup)>", RegexOptions.IgnoreCase)]
    private static partial Regex SmallPrint();

    private static bool Allowed(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeMailto);

    private static void Mark(HtmlAttributes attributes)
    {
        attributes.AddPropertyIfNotExist("target", "_blank");
        attributes.AddPropertyIfNotExist("rel", "noopener noreferrer nofollow");
    }

    /// <summary>A link that may not be one: its text stays where it was, the link goes.</summary>
    private static void Unwrap(LinkInline link)
    {
        var children = new List<Inline>();

        for (var child = link.FirstChild; child is not null; child = child.NextSibling)
        {
            children.Add(child);
        }

        Inline at = link;

        foreach (var child in children)
        {
            child.Remove();
            at.InsertAfter(child);
            at = child;
        }

        link.Remove();
    }
}
