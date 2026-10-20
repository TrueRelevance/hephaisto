using System.Globalization;
using System.Text.RegularExpressions;

namespace Hephaisto.Tests.Design;

/// <summary>
/// Reads the stylesheets this repo actually ships and holds them to the two rules that make the
/// token set canonical rather than advisory.
/// </summary>
/// <remarks>
/// <para>
/// Written the same way as <see cref="ShippedAlertRulesTests"/>, and for the same reason: the
/// failure it catches already happened. Two colours were written straight into
/// <c>app.css</c> - <c>#10131a</c>, twice, as the text colour on a <c>var(--red)</c> ground.
/// That is correct in dark mode, where <c>--red</c> is a light pink, and wrong in light mode,
/// where <c>--red</c> is a dark crimson and the error banner rendered near-black on it. It sat
/// there for three releases because nothing looked.
/// </para>
/// <para>
/// A convention lasts exactly as long as the person who remembers it. These are the tests that
/// mean "the tokens are the only place a colour is written" is a property of the repository
/// rather than a sentence in a document.
/// </para>
/// </remarks>
public class DesignTokenTests
{
    /// <summary>Any hex, rgb() or hsl() literal. Deliberately greedy - false positives here are cheap.</summary>
    private static readonly Regex Colour =
        new(@"#[0-9a-fA-F]{3,8}\b|\brgba?\s*\(|\bhsla?\s*\(", RegexOptions.Compiled);

    private static readonly Regex Declaration =
        new(@"^\s*(--[a-z0-9-]+)\s*:\s*([^;]+);", RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public void NoColourIsWrittenOutsideTheTokenFile()
    {
        var offenders = new List<string>();

        foreach (var file in ConsumingStylesheets())
        {
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                // Comments are prose about colours, not colours.
                if (StrippedOfComments(lines[i]) is var code && !Colour.IsMatch(code))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        offenders.Should().BeEmpty(
            "every colour belongs in tokens.css, which is the one place both themes are defined "
            + "together and the one place contrast is checked. A literal here is a value that is "
            + "correct in whichever theme its author happened to be looking at");
    }

    [Fact]
    public void EveryColourTokenIsDefinedInBothThemes()
    {
        var (dark, light) = Themes();

        var missing = dark
            .Where(t => LooksLikeColour(t.Value))
            .Where(t => !light.ContainsKey(t.Key))
            .Select(t => $"{t.Key}: {t.Value}")
            .ToList();

        missing.Should().BeEmpty(
            "a colour defined only in the dark block keeps its dark value in light mode, which is "
            + "how a light-mode bug ships looking like a deliberate choice");
    }

    [Fact]
    public void BothThemesDefineTheSameTokenNames()
    {
        var (dark, light) = Themes();

        light.Keys.Except(dark.Keys).Should().BeEmpty(
            "the light block overrides the dark one; a token that appears only in light is "
            + "undefined everywhere else and silently resolves to nothing");
    }

    /// <summary>
    /// Body text and interactive colour clear WCAG AA (4.5:1) against their own ground, in
    /// BOTH themes.
    /// </summary>
    /// <remarks>
    /// Light stopped being "a courtesy, not the design target" in v0.4.0. This test is what
    /// that sentence cost: both themes are held to the same bar, and neither can be improved
    /// by making the other worse.
    /// </remarks>
    [Theory]
    [InlineData("--fg", "--bg")]
    [InlineData("--fg", "--bg-raised")]
    [InlineData("--fg-dim", "--bg")]
    [InlineData("--accent", "--bg")]
    [InlineData("--on-alert", "--red")]
    public void ReadableTextClearsAa(string foreground, string background)
    {
        foreach (var (name, tokens) in AllThemes())
        {
            var ratio = Contrast(tokens[foreground], tokens[background]);

            ratio.Should().BeGreaterThanOrEqualTo(4.5,
                $"{foreground} on {background} is read as body text in the {name} theme "
                + $"(it is {ratio:F2}:1)");
        }
    }

    /// <summary>
    /// The quieter roles clear AA-large (3:1) - the bar for text at 18.66px bold or larger, and
    /// for the non-text parts of a control.
    /// </summary>
    /// <remarks>
    /// These are deliberately held to the lower bar rather than exempted. `--fg-faint` is used
    /// for column labels and evidence sources, and the semantic hues are the fill of a meter or
    /// the glyph beside a word - never the only carrier of meaning, because state is never
    /// colour alone, but still something a person has to be able to see.
    /// </remarks>
    [Theory]
    [InlineData("--fg-faint", "--bg")]
    [InlineData("--red", "--bg")]
    [InlineData("--orange", "--bg")]
    [InlineData("--yellow", "--bg")]
    [InlineData("--green", "--bg")]
    [InlineData("--blue", "--bg")]
    [InlineData("--purple", "--bg")]
    [InlineData("--cyan", "--bg")]
    public void SupportingColoursClearAaLarge(string foreground, string background)
    {
        foreach (var (name, tokens) in AllThemes())
        {
            var ratio = Contrast(tokens[foreground], tokens[background]);

            ratio.Should().BeGreaterThanOrEqualTo(3.0,
                $"{foreground} on {background} has to be visible in the {name} theme "
                + $"(it is {ratio:F2}:1)");
        }
    }

    /// <summary>
    /// The borders are visible, and are deliberately NOT held to 3:1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured, both themes: <c>--border-strong</c> is 1.86:1 on the dark ground and 1.79:1 on
    /// the light one. WCAG 1.4.11 asks 3:1 of "visual information required to identify user
    /// interface components and states", and these borders are neither. They divide a table
    /// header from its rows and a panel from the page; no border in this console ever carries
    /// state, because state is never colour alone - it always has a glyph and a word.
    /// </para>
    /// <para>
    /// Raising them to 3:1 would draw every hairline in the console about as loudly as the text
    /// it separates, on a page whose whole argument is density. That is a design change, and it
    /// is not one this milestone is making by accident in order to turn a test green.
    /// </para>
    /// <para>
    /// So the bar is stated rather than dropped. A border must still be visible as a division:
    /// this fails if one is ever flattened into its background, which is the regression that
    /// would actually cost a reader something.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("--border", "--bg")]
    [InlineData("--border-strong", "--bg")]
    [InlineData("--border-strong", "--bg-raised")]
    public void BordersAreVisibleWithoutBeingLoud(string border, string ground)
    {
        foreach (var (name, tokens) in AllThemes())
        {
            var ratio = Contrast(tokens[border], tokens[ground]);

            ratio.Should().BeGreaterThan(1.2,
                $"{border} has to read as a division from {ground} in the {name} theme "
                + $"(it is {ratio:F2}:1)");

            ratio.Should().BeLessThan(3.0,
                $"{border} at {ratio:F2}:1 on {ground} in the {name} theme is loud enough to "
                + "compete with the content; if that is intended, this bound is the place to "
                + "say so rather than the place to find out");
        }
    }

    /// <summary>
    /// The interactive accent is distinguishable from every severity colour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test exists because of the direction this project chose. The accent is an ember
    /// orange and the severity ramp is red, orange and yellow, so the accent sits in the middle
    /// of the range that already means "something is wrong". That was named as the cost of the
    /// direction before it was picked, and this is what stops it being paid by accident.
    /// </para>
    /// <para>
    /// The first palette drafted for it put <c>--accent</c> and <c>--orange</c> 1.24:1 apart,
    /// which is two colours a reader cannot reliably tell from one another - so a link and a
    /// warning would have looked the same. Deepening the orange took it to 2.07:1 dark and
    /// 1.72:1 light.
    /// </para>
    /// <para>
    /// 1.5:1 is a low bar and deliberately so: this is not a legibility threshold, it is a
    /// "these are visibly two different colours" threshold. What it forbids is the specific
    /// mistake of drifting the accent back into the severity ramp while adjusting something
    /// else.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("--red")]
    [InlineData("--orange")]
    [InlineData("--yellow")]
    public void TheInteractiveAccentIsNotMistakableForASeverity(string severity)
    {
        foreach (var (name, tokens) in AllThemes())
        {
            var ratio = Contrast(tokens["--accent"], tokens[severity]);

            ratio.Should().BeGreaterThan(1.5,
                $"--accent and {severity} are {ratio:F2}:1 apart in the {name} theme, which is "
                + "close enough that a link and a warning read as the same colour");
        }
    }

    /// <summary>Every SVG this repo ships is well-formed XML.</summary>
    /// <remarks>
    /// <para>
    /// A broken SVG does not throw and does not log. The browser renders a broken-image
    /// placeholder and everything else on the page carries on looking correct, so the failure
    /// surfaces as "the favicon is missing" weeks later, if at all.
    /// </para>
    /// <para>
    /// This test exists because it happened while the mark was being drawn. The comment in
    /// favicon.svg referred to the token it uses by name, and <c>--bg-raised</c> contains a
    /// double hyphen, which is <b>illegal inside an XML comment</b>. The file was invalid and
    /// rendered as nothing. The visual baseline caught it that time; this catches it without
    /// needing a picture.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryShippedSvgIsWellFormed()
    {
        var broken = new List<string>();

        foreach (var svg in Directory.EnumerateFiles(RepoRoot(), "*.svg", SearchOption.AllDirectories))
        {
            // node_modules and playwright's own report assets are not ours.
            if (svg.Contains("node_modules", StringComparison.Ordinal)
                || svg.Contains("playwright-report", StringComparison.Ordinal)
                || svg.Contains("test-results", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                System.Xml.Linq.XDocument.Load(svg);
            }
            catch (System.Xml.XmlException ex)
            {
                broken.Add($"{Path.GetRelativePath(RepoRoot(), svg)}: {ex.Message}");
            }
        }

        broken.Should().BeEmpty("a malformed SVG renders as a broken image and reports nothing");
    }

    /// <summary>
    /// Every surface consumes the SAME token file, byte for byte.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the test that makes "one token source, more than one consumer" a fact rather
    /// than an aspiration. Until the landing page existed there was exactly one consumer, so
    /// the claim could not be checked and would have decayed the first time somebody adjusted
    /// a colour on one side.
    /// </para>
    /// <para>
    /// A copy rather than a symlink or a build step, because each site has to be deployable on
    /// its own and this repo deliberately has no build step for CSS. The copies are safe only
    /// because this test exists - and it was generalised from one hardcoded path the moment a
    /// third and fourth copy appeared, because a test naming only <c>website/</c> would have gone
    /// on passing while the docs site drifted.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Surfaces))]
    public void EverySurfaceConsumesTheSameTokenFile(string surface)
    {
        var copy = Path.Combine(RepoRoot(), surface, "tokens.css");

        File.Exists(copy).Should().BeTrue($"{surface} is listed as a surface, so it must ship the tokens");

        File.ReadAllText(copy).Should().Be(
            File.ReadAllText(TokenFile()),
            $"{surface}/tokens.css is a copy of the canonical set; regenerate it with "
            + $"`cp src/Hephaisto.Agent/wwwroot/tokens.css {surface.Replace('\\', '/')}/tokens.css` "
            + "rather than editing it, or the surfaces drift within one release");
    }

    /// <summary>Every surface ships the same font binaries, so they all set type identically.</summary>
    /// <remarks>
    /// The <c>@font-face</c> src in tokens.css is <c>url("fonts/...")</c>, resolved relative to the
    /// stylesheet - so a surface that copies the token file without the fonts beside it renders in
    /// a fallback stack and looks deliberate to anybody who has not seen the real thing.
    /// </remarks>
    [Theory]
    [MemberData(nameof(SurfaceFonts))]
    public void EverySurfaceShipsTheSameFonts(string surface, string file)
    {
        var app = Path.Combine(RepoRoot(), "src", "Hephaisto.Agent", "wwwroot", "fonts", file);
        var copy = Path.Combine(RepoRoot(), surface, "fonts", file);

        File.Exists(copy).Should().BeTrue($"{surface} must ship {file} beside its tokens.css");

        File.ReadAllBytes(copy).Should().Equal(File.ReadAllBytes(app),
            $"{file} differs between the console and {surface}, so one of them is "
            + "setting type in a face the other does not have");
    }

    /// <summary>
    /// The theme-color meta tags agree with the background tokens, on both surfaces.
    /// </summary>
    /// <remarks>
    /// theme-color paints the browser's own chrome and is read before any CSS, so it is the one
    /// place a colour genuinely cannot be a var(). That makes it the one place a colour can go
    /// stale silently: change --bg and the page still renders correctly while the bar above it
    /// keeps the old value. This is the only reason those two literals are allowed to exist.
    /// </remarks>
    [Theory]
    [InlineData("src/Hephaisto.Agent/Components/App.razor")]
    [InlineData("website/index.html")]
    // The demo site is rendered rather than authored, so its <meta> lives in the renderer - but
    // it is the same literal with the same way of going stale, so it is read the same way.
    [InlineData("demo-site/build.mjs")]
    public void ThemeColourAgreesWithTheBackgroundToken(string relative)
    {
        var (dark, light) = Themes();
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        var declared = Regex.Matches(markup, """<meta name="theme-color" content="(#[0-9a-fA-F]{6})" media="\(prefers-color-scheme: (dark|light)\)" />""")
            .Cast<Match>()
            .ToDictionary(m => m.Groups[2].Value, m => m.Groups[1].Value, StringComparer.Ordinal);

        // The array overload, not params: ContainKeys(a, b, "because...") reads the message as a
        // third key and fails saying it could not find a colour named after the explanation.
        declared.Should().ContainKeys(
            new[] { "dark", "light" },
            "{0} should declare a theme colour for each theme", relative);

        declared["dark"].Should().Be(dark["--bg"],
            $"{relative}'s dark theme-color has drifted from --bg");

        var effectiveLightBg = light.TryGetValue("--bg", out var lbg) ? lbg : dark["--bg"];
        declared["light"].Should().Be(effectiveLightBg,
            $"{relative}'s light theme-color has drifted from --bg");
    }

    // -- the files -------------------------------------------------------------------------

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string TokenFile() =>
        Path.Combine(RepoRoot(), "src", "Hephaisto.Agent", "wwwroot", "tokens.css");

    /// <summary>
    /// Every surface that ships its own copy of the token file and the fonts beside it.
    /// </summary>
    /// <remarks>
    /// A surface is a directory containing <c>tokens.css</c> and <c>fonts/</c>. The console is not
    /// on this list because it is the source, not a copy. Adding a deployable surface without
    /// adding it here is the failure this list exists to prevent: the copy is then never compared
    /// to anything and drifts silently.
    /// </remarks>
    /// <summary>
    /// The demo site ships the console's stylesheet, byte for byte.
    /// </summary>
    /// <remarks>
    /// It is the only surface that copies app.css rather than writing its own, and that is the
    /// point of it: the pages are rendered with the console's own hp-* class vocabulary so they
    /// are the product's appearance rather than a reconstruction of it. A drifted copy would make
    /// that claim quietly false - the demo would keep rendering, in last release's console.
    /// </remarks>
    [Fact]
    public void TheDemoSiteShipsTheSameConsoleStylesheet()
    {
        var console = Path.Combine(RepoRoot(), "src", "Hephaisto.Agent", "wwwroot", "app.css");
        var copy = Path.Combine(RepoRoot(), "demo-site", "app.css");

        File.ReadAllText(copy).Should().Be(
            File.ReadAllText(console),
            "demo-site/app.css is a copy of the console's stylesheet; regenerate it with "
            + "`cp src/Hephaisto.Agent/wwwroot/app.css demo-site/app.css` rather than editing it");
    }

    /// <summary>
    /// The docs site declares the same theme colours, in the shape VitePress wants them.
    /// </summary>
    /// <remarks>
    /// The other two surfaces write <c>&lt;meta&gt;</c> tags directly and are covered by
    /// <see cref="ThemeColourAgreesWithTheBackgroundToken"/>. VitePress builds its head from a
    /// config array instead, so the same property needs a different reader - and it needs one at
    /// all for the same reason: theme-color is read before any CSS, so it cannot be a var(), which
    /// makes it the one value that can go stale without anything looking wrong.
    /// </remarks>
    [Fact]
    public void TheDocsSiteThemeColourAgreesWithTheBackgroundToken()
    {
        var (dark, light) = Themes();
        var config = File.ReadAllText(
            Path.Combine(RepoRoot(), "docs-site", ".vitepress", "config.ts"));

        var declared = Regex.Matches(
                config,
                """content:\s*'(#[0-9a-fA-F]{6})'\s*,\s*media:\s*'\(prefers-color-scheme:\s*(dark|light)\)'""")
            .Cast<Match>()
            .ToDictionary(m => m.Groups[2].Value, m => m.Groups[1].Value, StringComparer.Ordinal);

        declared.Should().ContainKeys(
            new[] { "dark", "light" },
            "the docs site should declare a theme colour for each theme");

        declared["dark"].Should().BeEquivalentTo(dark["--bg"],
            "the docs site's dark theme-color must equal --bg in the dark theme");
        declared["light"].Should().BeEquivalentTo(light["--bg"],
            "the docs site's light theme-color must equal --bg in the light theme");
    }

    private static readonly string[] SurfaceDirectories =
    [
        "website",
        Path.Combine("docs-site", ".vitepress", "theme"),
        "demo-site",
    ];

    /// <summary>The two faces every surface ships, self-hosted, with no CDN anywhere.</summary>
    private static readonly string[] ShippedFaces =
    [
        "gabarito-latin.woff2",
        "jetbrains-mono-latin.woff2",
    ];

    public static TheoryData<string> Surfaces()
    {
        var data = new TheoryData<string>();

        foreach (var surface in SurfaceDirectories)
        {
            data.Add(surface);
        }

        return data;
    }

    /// <summary>The cross product of <see cref="SurfaceDirectories"/> and the shipped faces.</summary>
    public static TheoryData<string, string> SurfaceFonts()
    {
        var data = new TheoryData<string, string>();

        foreach (var surface in SurfaceDirectories)
        {
            foreach (var face in ShippedFaces)
            {
                data.Add(surface, face);
            }
        }

        return data;
    }

    /// <summary>Every stylesheet that CONSUMES the tokens, which is every one but the token file.</summary>
    /// <remarks>
    /// A stylesheet missing from this list is silently exempt from
    /// <see cref="NoColourIsWrittenOutsideTheTokenFile"/> - it is not skipped loudly, it is simply
    /// never looked at. That makes this list the thing to update in the same commit that adds a
    /// surface, and the reason each entry names a real file rather than globbing: a glob would
    /// quietly start covering build output.
    /// </remarks>
    private static IEnumerable<string> ConsumingStylesheets()
    {
        yield return Path.Combine(RepoRoot(), "src", "Hephaisto.Agent", "wwwroot", "app.css");
        yield return Path.Combine(RepoRoot(), "website", "site.css");
        yield return Path.Combine(RepoRoot(), "docs-site", ".vitepress", "theme", "custom.css");
        yield return Path.Combine(RepoRoot(), "demo-site", "demo.css");
    }

    private static string StrippedOfComments(string line)
    {
        var start = line.IndexOf("/*", StringComparison.Ordinal);
        if (start >= 0)
        {
            line = line[..start];
        }

        // A line inside a block comment starts with the continuation asterisk.
        return line.TrimStart().StartsWith('*') ? string.Empty : line;
    }

    private static bool LooksLikeColour(string value) =>
        value.TrimStart().StartsWith('#')
        || value.TrimStart().StartsWith("rgb", StringComparison.OrdinalIgnoreCase)
        || value.TrimStart().StartsWith("hsl", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The dark block is <c>:root</c>. Light is declared twice - once for an operating system
    /// that asked for it, once for a reader who did - and the two must be identical.
    /// </summary>
    /// <remarks>
    /// This used to split the file at the first <c>prefers-color-scheme: light</c> and parse
    /// everything after it as one block. That stopped working the moment light gained a second
    /// declaration, and it stopped working by <i>throwing on a duplicate key</i> rather than by
    /// quietly averaging the two, which is the failure mode worth having. The duplicate is
    /// unavoidable in CSS - one of the declarations lives inside a media query and the other
    /// cannot - so the guarantee is moved here instead: they are equal by assertion.
    /// </remarks>
    private static (Dictionary<string, string> Dark, Dictionary<string, string> Light) Themes()
    {
        var css = File.ReadAllText(TokenFile());

        var dark = Parse(Block(css, "\n:root {"));
        var systemLight = Parse(Block(css, ":root:not([data-theme=\"dark\"])"));
        var explicitLight = Parse(Block(css, ":root[data-theme=\"light\"]"));

        // The whole reason a duplicate is tolerable. Without this the two drift, and the reader
        // who picked light explicitly gets last release's palette.
        explicitLight.Should().BeEquivalentTo(
            systemLight,
            "the two light declarations in tokens.css must stay identical - one is for an OS "
            + "that asked for light, the other for a reader who did, and they are the same theme");

        return (dark, systemLight);
    }

    /// <summary>
    /// The declaration body following <paramref name="marker"/>, from its <c>{</c> to the
    /// matching <c>}</c>. Brace-matched rather than regexed, because a media query nests.
    /// </summary>
    private static string Block(string css, string marker)
    {
        var at = css.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"tokens.css no longer contains a `{marker.Trim()}` rule");

        // From `at`, not past the marker: one of the markers ends in its own `{`, and skipping
        // over it finds the NEXT rule's brace instead - which brace-matches cleanly and returns
        // the wrong theme, so the mistake shows up as a palette that is merely wrong rather
        // than as a crash.
        var open = css.IndexOf('{', at);
        Assert.True(open > 0, $"`{marker.Trim()}` in tokens.css opens no block");

        var depth = 0;

        for (var i = open; i < css.Length; i++)
        {
            if (css[i] == '{')
            {
                depth++;
            }
            else if (css[i] == '}' && --depth == 0)
            {
                return css[(open + 1)..i];
            }
        }

        Assert.Fail($"`{marker.Trim()}` in tokens.css is not closed");

        return string.Empty;
    }

    /// <summary>Light inherits from dark, so the effective light theme is dark overridden by it.</summary>
    private static IEnumerable<(string Name, Dictionary<string, string> Tokens)> AllThemes()
    {
        var (dark, light) = Themes();

        var effectiveLight = new Dictionary<string, string>(dark, StringComparer.Ordinal);
        foreach (var (k, v) in light)
        {
            effectiveLight[k] = v;
        }

        yield return ("dark", dark);
        yield return ("light", effectiveLight);
    }

    private static Dictionary<string, string> Parse(string css) =>
        Declaration.Matches(css)
            .Cast<Match>()
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim(), StringComparer.Ordinal);

    // -- WCAG ------------------------------------------------------------------------------

    private static double Contrast(string a, string b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);

        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>WCAG 2.1 relative luminance. Hex only - the tokens compared here are all hex.</summary>
    private static double Luminance(string hex)
    {
        hex = hex.Trim().TrimStart('#');

        Assert.True(hex.Length is 3 or 6, $"expected a hex colour, got '{hex}'");

        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        }

        double Channel(int offset)
        {
            var v = int.Parse(hex.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(2)) + (0.0722 * Channel(4));
    }
}
