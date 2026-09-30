namespace UnityGraph.EditorLink.Tests;

/// <summary>
/// Documentation pages: trimming HTML to text, the cache, the host check and the order local file,
/// cache, fetch. Nothing here touches the network; the fetch is a fake.
/// </summary>
public sealed class DocsPagesTests : IDisposable
{
    private const string Page = """
        <!DOCTYPE html><html><head><title>Unity - Manual: Prefabs</title><style>body { color: red }</style>
        <script>var track = "secret-analytics";</script></head>
        <body><header><a href="/">Unity Manual</a></header>
        <nav><ul><li>Navigation entry</li></ul></nav>
        <div id="wrap"><div class="content"><h1>Prefabs</h1>
        <p>A <b>Prefab</b> is a reusable asset &amp; template.</p>
        <!-- a note for editors -->
        <h2>Overrides</h2>
        <ul><li>Property overrides</li><li>Added components</li></ul>
        <pre>var go = Instantiate(prefab);</pre>
        </div></div>
        <footer>Copyright Unity Technologies</footer></body></html>
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ug-docs-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Trimming_keeps_the_pages_content_and_drops_navigation_scripts_and_markup()
    {
        string text = DocsTrimmer.Trim(Page, 1000, "https://docs.unity3d.com/6000.3/Documentation/Manual/Prefabs.html");

        Assert.Equal("""
            # Prefabs

            A Prefab is a reusable asset & template.

            ## Overrides

            - Property overrides
            - Added components

            var go = Instantiate(prefab);

            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void A_page_without_a_content_container_uses_its_body()
    {
        Assert.Equal("Hello\n", DocsTrimmer.Trim("<html><body><p>Hello</p><script>x()</script></body></html>", 100, "src"));
        Assert.Equal("Bare text\n", DocsTrimmer.Trim("Bare text", 100, "src"));
    }

    [Fact]
    public void A_long_page_is_cut_to_the_budget_and_names_its_source()
    {
        string html = "<body>" + string.Concat(Enumerable.Range(1, 200).Select(i => "<p>Line number " + i + " of the page.</p>")) + "</body>";

        string text = DocsTrimmer.Trim(html, 50, "https://docs.unity3d.com/x.html");

        Assert.True(text.Length <= (50 * 4) + 100, text.Length.ToString());
        Assert.StartsWith("Line number 1 of the page.\n", text, StringComparison.Ordinal);
        Assert.EndsWith("... cut to 50 tokens; the whole page: https://docs.unity3d.com/x.html\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Line number 200", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cache_stores_pages_by_address_and_survives_odd_characters()
    {
        var cache = new DocsCache(_folder);
        const string Url = "https://docs.unity3d.com/Packages/com.unity.cinemachine@3.1/manual/index.html?a=b&c=d";

        Assert.Null(cache.Get(Url));
        cache.Put(Url, "cached text\n");

        Assert.Equal("cached text\n", cache.Get(Url));
        Assert.Null(cache.Get(Url + "x"));
        Assert.Single(Directory.GetFiles(_folder));
        Assert.StartsWith(_folder, Directory.GetFiles(_folder)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_cache_that_cannot_be_written_is_not_an_error()
    {
        string blocked = Path.Combine(_folder, "file");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(blocked, "not a folder");
        var cache = new DocsCache(Path.Combine(blocked, "docs"));

        cache.Put("https://docs.unity3d.com/a.html", "x");

        Assert.Null(cache.Get("https://docs.unity3d.com/a.html"));
    }

    [Fact]
    public void The_default_cache_is_outside_any_project()
    {
        string folder = DocsCache.DefaultFolder();

        Assert.EndsWith(Path.Combine("unitygraph", "docs"), folder, StringComparison.Ordinal);
        Assert.True(Path.IsPathRooted(folder));
    }

    [Theory]
    [InlineData("https://docs.unity3d.com/6000.3/Documentation/Manual/Prefabs.html", true)]
    [InlineData("https://DOCS.unity3d.com/Packages/com.unity.cinemachine@3.1/manual/index.html", true)]
    [InlineData("http://docs.unity3d.com/6000.3/Documentation/Manual/Prefabs.html", false)]
    [InlineData("https://unity3d.com/", false)]
    [InlineData("https://docs.unity3d.com.evil.example/x.html", false)]
    [InlineData("https://docs.unity3d.com@evil.example/x.html", false)]
    [InlineData("https://user:pw@docs.unity3d.com/x.html", false)]
    [InlineData("https://docs.unity3d.com:8443/x.html", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("docs.unity3d.com/x.html", false)]
    public void Only_https_pages_on_the_docs_host_may_be_fetched(string url, bool allowed)
    {
        Assert.Equal(allowed, DocsFetcher.Refusal(url) == null);
        if (!allowed)
        {
            DocsFetchResult result = DocsFetcher.Fetch(url);
            Assert.Null(result.Html);
            Assert.StartsWith("only https://docs.unity3d.com pages are fetched", result.Problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_local_page_is_read_from_disk_and_never_fetched()
    {
        Directory.CreateDirectory(_folder);
        string file = Path.Combine(_folder, "Prefabs.html");
        File.WriteAllText(file, Page);
        var fetches = new List<string>();

        string text = DocsPages.Text(new OpAnswer(null, null, null, Local: file), online: true, 1000, new DocsCache(_folder), Fetch(fetches), out bool found);

        Assert.True(found);
        Assert.StartsWith("# Prefabs\n", text, StringComparison.Ordinal);
        Assert.Empty(fetches);
    }

    [Fact]
    public void A_missing_local_file_is_reported()
    {
        string text = DocsPages.Text(new OpAnswer(null, null, null, Local: Path.Combine(_folder, "gone.html")), online: true, 1000, new DocsCache(_folder), Fetch([]), out bool found);

        Assert.False(found);
        Assert.StartsWith("cannot read ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_is_fetched_once_trimmed_and_then_served_from_the_cache()
    {
        const string Url = "https://docs.unity3d.com/6000.3/Documentation/Manual/Prefabs.html";
        var answer = new OpAnswer(null, "Manual/Prefabs is not in the local documentation", null, Online: [Url]);
        var cache = new DocsCache(_folder);
        var fetches = new List<string>();

        string first = DocsPages.Text(answer, online: true, 1000, cache, Fetch(fetches, Page), out bool found);
        string second = DocsPages.Text(answer, online: true, 1000, cache, Fetch(fetches, Page), out bool foundAgain);

        Assert.True(found);
        Assert.True(foundAgain);
        Assert.StartsWith("# Prefabs\n", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
        Assert.Equal([Url], fetches);
    }

    [Fact]
    public void The_cache_answers_even_with_docs_online_off()
    {
        const string Url = "https://docs.unity3d.com/6000.3/Documentation/Manual/Prefabs.html";
        var cache = new DocsCache(_folder);
        cache.Put(Url, "from the cache\n");
        var fetches = new List<string>();

        string text = DocsPages.Text(new OpAnswer(null, "not local", null, Online: [Url]), online: false, 1000, cache, Fetch(fetches), out bool found);

        Assert.True(found);
        Assert.Equal("from the cache\n", text);
        Assert.Empty(fetches);
    }

    [Fact]
    public void With_docs_online_off_nothing_is_fetched_and_the_addresses_are_named()
    {
        var fetches = new List<string>();

        string text = DocsPages.Text(
            new OpAnswer(null, "no type Foo in the loaded assemblies", null, Online: ["https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Foo.html"]),
            online: false, 1000, new DocsCache(_folder), Fetch(fetches), out bool found);

        Assert.False(found);
        Assert.Empty(fetches);
        Assert.Equal("""
            no type Foo in the loaded assemblies
            docs.online is off in .claude/unitygraph.json; read it at:
              https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Foo.html

            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void A_failed_fetch_fails_open_with_the_reason_and_the_address_and_caches_nothing()
    {
        const string Url = "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Foo.html";
        var cache = new DocsCache(_folder);

        string text = DocsPages.Text(new OpAnswer(null, "no type Foo", null, Online: [Url]), online: true, 1000, cache,
            _ => new DocsFetchResult(null, "no answer within 15 s"), out bool found);

        Assert.False(found);
        Assert.Equal("no type Foo\nnot fetched, read it at:\n  " + Url + " (no answer within 15 s)\n", text);
        Assert.Null(cache.Get(Url));
    }

    [Fact]
    public void The_next_address_is_tried_when_the_first_page_does_not_exist()
    {
        const string Method = "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Foo.Go.html";
        const string Dashed = "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Foo-Go.html";
        var fetches = new List<string>();

        string text = DocsPages.Text(new OpAnswer(null, "no type Foo", null, Online: [Method, Dashed]), online: true, 1000, new DocsCache(_folder),
            url =>
            {
                fetches.Add(url);
                return url == Dashed ? new DocsFetchResult("<body><p>Go does it.</p></body>", null) : new DocsFetchResult(null, "HTTP 404 for " + url);
            }, out bool found);

        Assert.True(found);
        Assert.Equal("Go does it.\n", text);
        Assert.Equal([Method, Dashed], fetches);
    }

    private static Func<string, DocsFetchResult> Fetch(List<string> calls, string? html = null) => url =>
    {
        calls.Add(url);
        return html == null ? new DocsFetchResult(null, "offline") : new DocsFetchResult(html, null);
    };
}
