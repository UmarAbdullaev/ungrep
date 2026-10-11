using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UnityGraph.Core.Graph;
using UnityGraph.Query.Tests;

namespace UnityGraph.Viewer.Tests;

/// <summary>The page `ug view` writes, checked without a browser (SPEC-viewer, Testing).</summary>
public sealed class PageTests(QueryFixture fixture) : IClassFixture<QueryFixture>
{
    private string Page(string query, int depth = 2)
    {
        using GraphStore store = fixture.Open();
        return ViewPage.Render(NeighbourhoodTests.Walk(store, query, depth));
    }

    private static ViewGraph Empty(string query) => new()
    {
        Query = query,
        Depth = 2,
        Generated = "2026-10-11 14:03",
        Revision = "000000000000",
        Kinds = [],
        Nodes = [],
        Edges = [],
        More = [],
        Unresolved = 0,
        Cut = false,
        AllCommand = "",
    };

    [Fact]
    public void Page_is_one_file_that_asks_for_nothing()
    {
        string page = Page("Turntable.Rotate(float)");

        Assert.DoesNotMatch(new Regex("<script[^>]*\\ssrc", RegexOptions.IgnoreCase), page);
        Assert.DoesNotContain("<link", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex(@"\burl\((?!\s*['""]?data:)", RegexOptions.IgnoreCase), page);
        Assert.DoesNotContain("sourceMappingURL", page, StringComparison.Ordinal);
        Assert.Contains("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline';", page, StringComparison.Ordinal);
        Assert.DoesNotMatch("@@[A-Z]+@@", page);

        // The data, five vendored scripts, the page's own.
        Assert.Equal(7, Regex.Matches(page, "<script[ >]").Count);
        Assert.Equal(7, Regex.Matches(page, "</script>").Count);
        Assert.InRange(Encoding.UTF8.GetByteCount(page), 1, 2 * 1024 * 1024 - 1);
    }

    [Fact]
    public void Every_vendored_notice_is_in_the_page()
    {
        string page = Page("LabStage", depth: 1);
        string[] licences = Directory.GetFiles(Path.Combine(RepoRoot(), "src", "UnityGraph.Viewer", "vendor"), "*.LICENSE");
        Assert.Equal(7, licences.Length);
        foreach (string licence in licences)
        {
            Assert.Contains(WebUtility.HtmlEncode(File.ReadAllText(licence).Trim()), page, StringComparison.Ordinal);
        }

        foreach (string package in new[] { "cytoscape 3.34.3", "cytoscape-dagre 4.0.1", "@dagrejs/dagre 3.1.1", "@dagrejs/graphlib 4.0.5", "layout-base 2.0.1", "cose-base 2.2.0", "cytoscape-fcose 2.2.0" })
        {
            Assert.Contains("<h3>" + package, page, StringComparison.Ordinal);
        }

        Assert.Contains("JamaJS", page, StringComparison.Ordinal);
        Assert.Contains("Licensed under the Apache License, Version 2.0", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Title_is_encoded_and_nothing_inserted_is_read_as_a_placeholder()
    {
        string page = ViewPage.Render(Empty("A<b>&'\"@@SCRIPT@@"));
        Assert.Contains("<title>ug view A&lt;b&gt;&amp;&#39;&quot;@@SCRIPT@@</title>", page, StringComparison.Ordinal);
        Assert.Contains("\"query\":\"A\\u003Cb\\u003E\\u0026\\u0027\\u0022@@SCRIPT@@\"", page, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(page, Regex.Escape("The `ug view` page (SPEC-viewer, Page)")));
    }

    [Fact]
    public void Depth_and_layout_reach_the_page()
    {
        string near = Page("LabStage", depth: 1);
        Assert.Contains("\"depth\":1,\"baked\":2,\"layout\":\"dagre\"", near, StringComparison.Ordinal);

        string far = Page("LabStage", depth: 4);
        Assert.Contains("\"depth\":4,\"baked\":5,\"layout\":\"fcose\"", far, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "unitygraph.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("unitygraph.sln not found above " + AppContext.BaseDirectory);
    }
}
