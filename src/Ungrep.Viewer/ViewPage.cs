using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Ungrep.Viewer;

/// <summary>
/// The page `ug view` writes: the template, the baked data, the vendored scripts and their notices,
/// all embedded resources, in one file that asks the network for nothing (SPEC-viewer).
/// </summary>
public static partial class ViewPage
{
    // Inlined in this order: the plugins find cytoscape, layout-base and cose-base as globals.
    // Versions as in vendor/SOURCES.md and SPEC-viewer, Boundaries.
    private static readonly (string File, string Package)[] _scripts =
    [
        ("cytoscape.min.js", "cytoscape 3.34.3"),
        ("cytoscape-dagre.min.js", "cytoscape-dagre 4.0.1"),
        ("layout-base.js", "layout-base 2.0.1"),
        ("cose-base.js", "cose-base 2.2.0"),
        ("cytoscape-fcose.js", "cytoscape-fcose 2.2.0"),
    ];

    private static readonly (string File, string Package)[] _licences =
    [
        ("cytoscape.LICENSE", "cytoscape 3.34.3"),
        ("cytoscape-dagre.LICENSE", "cytoscape-dagre 4.0.1"),
        ("dagre.LICENSE", "@dagrejs/dagre 3.1.1, bundled in cytoscape-dagre"),
        ("graphlib.LICENSE", "@dagrejs/graphlib 4.0.5, bundled in cytoscape-dagre"),
        ("layout-base.LICENSE", "layout-base 2.0.1"),
        ("cose-base.LICENSE", "cose-base 2.2.0"),
        ("cytoscape-fcose.LICENSE", "cytoscape-fcose 2.2.0"),
    ];

    public static string Render(ViewGraph graph)
    {
        var vendor = new StringBuilder();
        foreach ((string file, string package) in _scripts)
        {
            vendor.Append("<script>/* ").Append(package).Append(" */\n").Append(Script(Resource("vendor/" + file), file)).Append("\n</script>\n");
        }

        var notices = new StringBuilder("<p>This page inlines the libraries below, each under the MIT licence. layout-base's singular value decomposition is adapted from JamaJS; its Apache-2.0 notice stays inline in that script.</p>\n");
        foreach ((string file, string package) in _licences)
        {
            notices.Append("<h3>").Append(WebUtility.HtmlEncode(package)).Append("</h3>\n<pre>")
                .Append(WebUtility.HtmlEncode(Resource("vendor/" + file).Trim())).Append("</pre>\n");
        }

        var parts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TITLE"] = WebUtility.HtmlEncode("ug view " + graph.Query),
            ["STYLE"] = Resource("page/view.css"),
            ["NOTICES"] = notices.ToString(),
            ["DATA"] = graph.ToJson(),
            ["VENDOR"] = vendor.ToString(),
            ["SCRIPT"] = Script(Resource("page/view.js"), "view.js"),
        };

        // One pass, so nothing inserted (a query, a path) is read as a placeholder.
        return Placeholder().Replace(Resource("page/view.html"), match => parts[match.Groups[1].Value]);
    }

    // A script inside <script>: without its source map comment (the browser would ask for the
    // map), and refused if it could end the tag early.
    private static string Script(string text, string name)
    {
        if (text.Contains("</script", StringComparison.OrdinalIgnoreCase) || text.Contains("<!--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{name} cannot be inlined: it holds </script or <!--.");
        }

        return SourceMap().Replace(text, "").TrimEnd();
    }

    private static string Resource(string name)
    {
        using Stream stream = typeof(ViewPage).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [GeneratedRegex("@@([A-Z]+)@@")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^//# sourceMappingURL=.*$", RegexOptions.Multiline)]
    private static partial Regex SourceMap();
}
