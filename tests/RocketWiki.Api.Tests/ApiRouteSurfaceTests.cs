using System.Text.RegularExpressions;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// The API's plain-HTTP surface has to be routed in three places that are edited by
/// different people at different times: the Vite dev proxy (what the SPA calls
/// same-origin), the nginx template (Docker Compose), and the Helm ingress (k3s). They had
/// drifted — nginx and the ingress each routed four prefixes while the SPA called seven,
/// so <c>/avatars</c>, <c>/avatar</c>, <c>/emojis</c> and <c>/users</c> fell into the SPA
/// catch-all in BOTH production artifacts.
///
/// <para>That failure mode is the reason this test exists rather than a comment. Nothing
/// errored: an avatar or emoji GET returned <b>HTTP 200 text/html</b> (index.html) into an
/// <c>&lt;img&gt;</c>, and the writes got a 405 from nginx's static handler. Avatars and
/// custom emojis were simply broken, admin emoji management was dead, and the §19 Gravatar
/// endpoint was unreachable regardless of its flag — with nothing failing at deploy time
/// to say so.</para>
///
/// <para><b>The dev proxy is the ground truth</b>, deliberately: it is the list a developer
/// edits the moment the SPA starts calling a new route, because otherwise their own
/// local run breaks immediately. The production artifacts have no such feedback loop —
/// which is exactly why they were the ones that fell behind. So this asserts
/// production ⊇ dev, and names the missing prefix when it fails.</para>
///
/// <para>Read as text rather than executed. A Vite config is TypeScript and a Helm
/// template is Go text/template; parsing either properly would mean running their
/// toolchains from a C# test. The regexes below are pinned to the exact one-line shapes
/// those files use, and each has a non-vacuity assertion so a reformat that breaks the
/// match fails loudly instead of silently matching nothing.</para>
/// </summary>
public sealed class ApiRouteSurfaceTests
{
    /// <summary>
    /// Prefixes the SPA calls that are deliberately NOT the API's: none today. Kept as an
    /// explicit (empty) list so that if one ever appears, excluding it is a visible
    /// decision in this file rather than an edit to the assertion.
    /// </summary>
    private static readonly string[] NotRoutedToApi = [];

    [Fact]
    public void NginxAndIngress_RouteEveryApiPrefixTheSpaCallsSameOrigin()
    {
        var devProxyPrefixes = ReadDevProxyPrefixes();
        var nginxPrefixes = ReadNginxLocations();
        var ingressPrefixes = ReadIngressPaths();

        var expected = devProxyPrefixes.Except(NotRoutedToApi, StringComparer.Ordinal).ToList();

        // Non-vacuity: if any of the three reads returns nothing, the file moved or was
        // reformatted and this test must fail rather than pass over an empty set.
        Assert.True(expected.Count >= 6, $"Read only {expected.Count} dev-proxy prefixes; the parse is broken.");
        Assert.True(nginxPrefixes.Count >= 6, $"Read only {nginxPrefixes.Count} nginx locations; the parse is broken.");
        Assert.True(ingressPrefixes.Count >= 6, $"Read only {ingressPrefixes.Count} ingress paths; the parse is broken.");

        var missingFromNginx = expected.Except(nginxPrefixes, StringComparer.Ordinal).ToList();
        Assert.True(missingFromNginx.Count == 0,
            "deploy/docker/nginx/default.conf.template routes no `location` for: " +
            string.Join(", ", missingFromNginx) +
            ". Unrouted prefixes fall into the SPA catch-all and answer 200 text/html instead of failing.");

        var missingFromIngress = expected.Except(ingressPrefixes, StringComparer.Ordinal).ToList();
        Assert.True(missingFromIngress.Count == 0,
            "deploy/helm/rocketwiki/templates/ingress.yaml routes nothing to the API for: " +
            string.Join(", ", missingFromIngress) +
            ". Unrouted prefixes fall through to the web Service's catch-all.");
    }

    /// <summary>`const proxyPaths = ['/graphql', …]` plus the separately-configured
    /// `'/hubs'` key, which carries `ws: true` and so cannot live in the shared list.</summary>
    private static List<string> ReadDevProxyPrefixes()
    {
        var text = ReadRepoFile(Path.Combine("web", "vite.config.ts"));

        var listMatch = Regex.Match(text, @"const\s+proxyPaths\s*=\s*\[(?<body>[^\]]*)\]");
        Assert.True(listMatch.Success, "Could not find `const proxyPaths = [...]` in web/vite.config.ts.");

        var prefixes = Regex.Matches(listMatch.Groups["body"].Value, @"'(?<p>/[^']+)'")
            .Select(m => m.Groups["p"].Value)
            .ToList();

        // '/hubs' is configured on its own because of `ws: true`; it is still part of the
        // surface and both production artifacts must route it.
        foreach (var extra in Regex.Matches(text, @"'(?<p>/hubs)'\s*:\s*\{").Select(m => m.Groups["p"].Value))
        {
            if (!prefixes.Contains(extra, StringComparer.Ordinal))
            {
                prefixes.Add(extra);
            }
        }

        return prefixes;
    }

    /// <summary>`location /foo {` — the proxy blocks only; `/assets/` and the bare `/`
    /// catch-all are the SPA's own and are excluded by requiring a non-empty path with no
    /// trailing slash.</summary>
    private static List<string> ReadNginxLocations() =>
        Regex.Matches(
                ReadRepoFile(Path.Combine("deploy", "docker", "nginx", "default.conf.template")),
                @"^\s*location\s+(?<p>/[A-Za-z0-9_-]+)\s*\{",
                RegexOptions.Multiline)
            .Select(m => m.Groups["p"].Value)
            .ToList();

    /// <summary>
    /// The document-level security headers, and the nginx trap that makes "we set them at
    /// server level" an unsafe thing to believe.
    ///
    /// <para><b>add_header does not merge across levels.</b> A <c>location</c> that declares
    /// even one add_header of its own REPLACES the entire inherited set — silently, with no
    /// warning at <c>nginx -t</c> and no error at runtime. So adding a Cache-Control to a
    /// block is enough to serve every response from it with no CSP, no X-Frame-Options and
    /// no nosniff, and the only way to find out is to look at a response header. Two
    /// locations already declare a Cache-Control, so this is not hypothetical.</para>
    ///
    /// <para>The invariant, encoded: <b>a location either declares no add_header at all
    /// (and inherits everything), or it declares all five.</b> That is exactly nginx's rule
    /// rather than an approximation of it, which is why it can be checked mechanically —
    /// and why "the headers are present on every location that serves responses" follows
    /// from it rather than needing a second, weaker assertion.</para>
    /// </summary>
    [Fact]
    public void NginxTemplate_SetsTheSecurityHeaders_AndNoLocationSilentlyDropsThem()
    {
        var template = ReadRepoFile(Path.Combine("deploy", "docker", "nginx", "default.conf.template"));

        string[] required =
        [
            "Content-Security-Policy",
            "X-Frame-Options",
            "X-Content-Type-Options",
            "Referrer-Policy",
            "Strict-Transport-Security",
        ];

        // The server-level set, which everything with no add_header of its own inherits.
        // Read from the region before the first `location` block so a header declared only
        // inside some location cannot satisfy this.
        var firstLocation = template.IndexOf("\n    location ", StringComparison.Ordinal);
        Assert.True(firstLocation > 0, "Could not find the first `location` block; the template was restructured.");
        var serverPreamble = template[..firstLocation];

        foreach (var header in required)
        {
            Assert.True(
                Regex.IsMatch(serverPreamble, $@"^\s*add_header\s+{Regex.Escape(header)}\s", RegexOptions.Multiline),
                $"deploy/docker/nginx/default.conf.template sets no server-level `{header}`.");
        }

        // `always` on every one: without it nginx omits the header on 4xx/5xx responses,
        // and a clickjacking frame around a 403 is still a frame.
        foreach (Match match in Regex.Matches(template, @"^\s*add_header\s+(?<h>\S+)\s+(?<rest>.*)$", RegexOptions.Multiline))
        {
            if (required.Contains(match.Groups["h"].Value, StringComparer.Ordinal))
            {
                Assert.EndsWith("always;", match.Groups["rest"].Value.Trim(), StringComparison.Ordinal);
            }
        }

        // And the trap itself: any location that declares an add_header must declare all
        // five, because declaring one drops the inherited set entirely.
        foreach (var (path, body) in ReadNginxLocationBodies(template))
        {
            if (!body.Contains("add_header", StringComparison.Ordinal))
            {
                continue; // declares none, so it inherits all of them
            }

            foreach (var header in required)
            {
                Assert.True(
                    body.Contains($"add_header {header}", StringComparison.Ordinal)
                    || Regex.IsMatch(body, $@"add_header\s+{Regex.Escape(header)}\s"),
                    $"`location {path}` in deploy/docker/nginx/default.conf.template declares an add_header of its " +
                    $"own, which REPLACES every inherited one — but does not repeat `{header}`. Every response from " +
                    "that block is therefore served without it. Repeat all five, or set none here.");
            }
        }
    }

    /// <summary>
    /// Each `location … { … }` block's path and body, matched by counting braces. A regex
    /// cannot do this (the bodies contain braces of their own, e.g. `${API_UPSTREAM}`), and
    /// getting it wrong would make the assertion above vacuous.
    /// </summary>
    private static List<(string Path, string Body)> ReadNginxLocationBodies(string template)
    {
        var blocks = new List<(string, string)>();
        foreach (Match match in Regex.Matches(template, @"^\s*location\s+(?<p>\S+)\s*\{", RegexOptions.Multiline))
        {
            var depth = 1;
            var start = match.Index + match.Length;
            var i = start;
            while (i < template.Length && depth > 0)
            {
                if (template[i] == '{')
                {
                    depth++;
                }
                else if (template[i] == '}')
                {
                    depth--;
                }

                i++;
            }

            Assert.Equal(0, depth); // unbalanced braces mean the parse is wrong, not the config
            blocks.Add((match.Groups["p"].Value, template[start..(i - 1)]));
        }

        Assert.True(blocks.Count >= 8, $"Parsed only {blocks.Count} location blocks; the parse is broken.");
        return blocks;
    }

    /// <summary>The `range $path := list "…" "…"` line that generates the API paths.</summary>
    private static List<string> ReadIngressPaths()
    {
        var text = ReadRepoFile(Path.Combine("deploy", "helm", "rocketwiki", "templates", "ingress.yaml"));

        var rangeMatch = Regex.Match(text, @"range\s+\$path\s*:=\s*list(?<body>[^}]*)}}");
        Assert.True(rangeMatch.Success, "Could not find the `range $path := list …` line in ingress.yaml.");

        return Regex.Matches(rangeMatch.Groups["body"].Value, "\"(?<p>/[^\"]+)\"")
            .Select(m => m.Groups["p"].Value)
            .ToList();
    }

    private static string ReadRepoFile(string relativePath)
    {
        var full = Path.Combine(RepoRoot.Find(), relativePath);
        Assert.True(File.Exists(full), $"Expected '{relativePath}' to exist at the repo root.");
        return File.ReadAllText(full);
    }
}
