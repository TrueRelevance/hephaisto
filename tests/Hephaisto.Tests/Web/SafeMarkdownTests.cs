using Hephaisto.Agent.Components;

namespace Hephaisto.Tests.Web;

/// <summary>
/// GitHub's markdown in the console: rendered as markdown, and still nobody else's markup - no
/// HTML of its own, a link only to http(s) or mailto, and no image fetched.
/// </summary>
public sealed class SafeMarkdownTests
{
    [Fact]
    public void Headings_lists_emphasis_code_tables_and_task_lists_are_rendered()
    {
        var html = SafeMarkdown.ToHtml("""
            ## What happens

            1. Change a **price**
            2. Reload the `cart`

            - [x] done
            - [ ] open

            | a | b |
            |---|---|
            | 1 | 2 |

            ~~gone~~
            """);

        html.Should().Contain("<h2>What happens</h2>")
            .And.Contain("<ol>").And.Contain("<strong>price</strong>").And.Contain("<code>cart</code>")
            .And.Contain("type=\"checkbox\"").And.Contain("<table>").And.Contain("<del>gone</del>");
    }

    [Fact]
    public void Raw_html_is_text_never_markup()
    {
        var html = SafeMarkdown.ToHtml("Hello <b>bold</b> <script>alert(1)</script> <img src=x onerror=alert(1)>\n\n<div onclick=\"x\">block</div>");

        html.Should().NotContain("<b>").And.NotContain("<script").And.NotContain("<img").And.NotContain("<div");
        html.Should().Contain("&lt;b&gt;bold&lt;/b&gt;").And.Contain("&lt;script&gt;");
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))", "click")]
    [InlineData("[data](data:text/html;base64,PHNjcmlwdD4=)", "data")]
    [InlineData("[relative](/settings)", "relative")]
    [InlineData("[proto](vbscript:msgbox)", "proto")]
    public void A_link_to_anything_but_http_or_mailto_keeps_its_text_and_loses_the_link(string markdown, string text)
    {
        var html = SafeMarkdown.ToHtml(markdown);

        html.Should().NotContain("<a ").And.NotContain("href");
        html.Should().Contain(text);
    }

    [Fact]
    public void An_http_link_opens_in_a_new_tab_without_an_opener_and_a_bare_url_becomes_one()
    {
        var html = SafeMarkdown.ToHtml("See [the runbook](https://example.invalid/runbook) and https://example.invalid/bare, or mail <ops@example.invalid>.");

        html.Should().Contain("href=\"https://example.invalid/runbook\"")
            .And.Contain("href=\"https://example.invalid/bare\"")
            .And.Contain("href=\"mailto:ops@example.invalid\"");
        html.Split("<a ").Skip(1).Should().OnlyContain(a => a.Contains("rel=\"noopener noreferrer nofollow\"") && a.Contains("target=\"_blank\""));
    }

    [Fact]
    public void An_image_is_a_link_to_it_and_never_loaded()
    {
        var html = SafeMarkdown.ToHtml("![the logo](https://example.invalid/logo.png) and ![](https://example.invalid/blank.png) and ![bad](javascript:x)");

        html.Should().NotContain("<img");
        html.Should().Contain("href=\"https://example.invalid/logo.png\"").And.Contain(">the logo</a>");
        html.Should().Contain(">image</a>", "an image without alt text is still named");
        html.Should().Contain("bad").And.NotContain("javascript");
    }

    [Fact]
    public void Nothing_written_is_nothing_rendered_and_a_long_text_is_cut()
    {
        SafeMarkdown.ToHtml(null).Should().BeEmpty();
        SafeMarkdown.ToHtml("   ").Should().BeEmpty();
        var cut = SafeMarkdown.ToHtml(new string('a', SafeMarkdown.MaxLength + 500));
        cut.Should().Contain("…");
        cut.Length.Should().BeLessThan(SafeMarkdown.MaxLength + 100);
    }

    [Fact]
    public void A_line_break_in_a_comment_is_a_line_break()
    {
        SafeMarkdown.ToHtml("first line\nsecond line").Should().Contain("<br />");
    }

    // --- issue references ----------------------------------------------------------------------

    private static string? Board(string repository, int number) =>
        repository == "acme/api" && number == 431 ? "planner?view=today&detail=PVTI_431" : null;

    [Fact]
    public void A_reference_to_an_issue_is_a_link_to_the_console_when_it_knows_it_and_to_github_otherwise()
    {
        var html = SafeMarkdown.ToHtml("Steps: #431, acme/web#240 and #12.", "acme/api", Board);

        html.Should().Contain("<a href=\"planner?view=today&amp;detail=PVTI_431\">#431</a>", "the console's own page, in the same tab");
        html.Should().Contain("href=\"https://github.com/acme/web/issues/240\"").And.Contain(">acme/web#240</a>");
        html.Should().Contain("href=\"https://github.com/acme/api/issues/12\"");
        html.Split("<a ").Where(a => a.Contains("github.com")).Should().OnlyContain(a => a.Contains("target=\"_blank\"") && a.Contains("noopener"));
    }

    [Fact]
    public void Without_the_texts_repository_a_bare_number_stays_text_and_code_and_links_are_left_alone()
    {
        SafeMarkdown.ToHtml("Fixed in #12.").Should().NotContain("<a ");
        SafeMarkdown.ToHtml("`acme/api#431` and [see acme/api#431](https://example.invalid/x)", "acme/api").Should()
            .Contain("<code>acme/api#431</code>")
            .And.Contain(">see acme/api#431</a>")
            .And.NotContain("github.com/acme/api/issues/431");
        SafeMarkdown.ToHtml("C#1 and https://example.invalid/page#12 and &#35;", "acme/api").Should().NotContain("github.com");
    }

    [Fact]
    public void An_address_of_an_issue_the_console_knows_leads_to_the_console()
    {
        var html = SafeMarkdown.ToHtml("See https://github.com/acme/api/issues/431 and [the PR](https://github.com/acme/api/pull/500).", "acme/api", Board);

        html.Should().Contain("<a href=\"planner?view=today&amp;detail=PVTI_431\">https://github.com/acme/api/issues/431</a>");
        html.Should().Contain("href=\"https://github.com/acme/api/pull/500\"");
        SafeMarkdown.IssueOf("https://github.com/acme/api/pull/500#discussion").Should().Be(("acme/api", 500));
        SafeMarkdown.RepositoryOf("https://github.com/acme/api.git").Should().Be("acme/api");
        SafeMarkdown.RepositoryOf("http://coder-git.hephaisto-coder.svc/acme/api.git").Should().BeNull();
    }

    [Fact]
    public void The_small_print_tags_go_and_their_text_stays()
    {
        SafeMarkdown.ToHtml("Done.\n\n<sub>$0.07 · attempt 5489</sub>").Should().Contain("$0.07 · attempt 5489").And.NotContain("sub&gt;").And.NotContain("<sub>");
    }
}
