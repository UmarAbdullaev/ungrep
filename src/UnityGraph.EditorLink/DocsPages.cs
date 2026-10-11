using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityGraph.EditorLink;

/// <summary>
/// A documentation page as a few lines of plain text: the page's main content without navigation,
/// scripts and markup, cut to a token budget (SPEC-editor-ops, Documentation).
/// </summary>
public static partial class DocsTrimmer
{
    private const int BytesPerToken = 4;

    private const int MaxBudget = 8000;

    /// <summary>
    /// Text of <paramref name="html"/>, at most <paramref name="budgetTokens"/> tokens; the last line says when it was cut
    /// and, given the command that asked (<paramref name="again"/>), the budget that shows more.
    /// </summary>
    public static string Trim(string html, int budgetTokens, string source, string? again = null) => Cut(Plain(html), budgetTokens, source, again);

    /// <summary>The whole text of <paramref name="html"/>: main content, headings and list items kept, markup dropped.</summary>
    public static string Plain(string html)
    {
        string body = Main(html);
        body = DroppedElements().Replace(body, " ");
        body = Comments().Replace(body, " ");
        body = Headings().Replace(body, m => "\n\n" + new string('#', m.Groups[1].Value[0] - '0') + " " + m.Groups[2].Value + "\n");
        body = ListItems().Replace(body, "\n- ");
        body = Breaks().Replace(body, "\n");
        body = Tags().Replace(body, " ");
        body = WebUtility.HtmlDecode(body).Replace(' ', ' ');

        var lines = new List<string>();
        foreach (string raw in body.Split('\n'))
        {
            string line = Spaces().Replace(raw, " ").Trim();
            if (line.Length > 0)
            {
                lines.Add(line);
            }
            else if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add("");
            }
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Concat(lines.Select(l => l + "\n"));
    }

    /// <summary>
    /// <paramref name="text"/> cut on a line to <paramref name="budgetTokens"/>: `... cut to 1200 tokens: ug docs
    /// Manual/Prefabs --budget 2400; the whole page: <paramref name="source"/>`.
    /// </summary>
    public static string Cut(string text, int budgetTokens, string source, string? again = null)
    {
        int budget = budgetTokens * BytesPerToken;
        var kept = new StringBuilder();
        foreach (string line in text.TrimEnd('\n').Split('\n'))
        {
            if (kept.Length + line.Length + 1 > budget)
            {
                string more = again != null && budgetTokens < MaxBudget ? ": " + again + " --budget " + Math.Min(budgetTokens * 2, MaxBudget).ToString(CultureInfo.InvariantCulture) : "";
                return kept.ToString().TrimEnd() + "\n... cut to " + budgetTokens.ToString(CultureInfo.InvariantCulture) + " tokens" + more + "; the whole page: " + source + "\n";
            }

            kept.Append(line).Append('\n');
        }

        return text.Length == 0 ? "" : kept.ToString();
    }

    // The page's own content: inside <main>, or the `content` container Unity's pages use, else the body.
    private static string Main(string html)
    {
        Match main = MainElement().Match(html);
        if (main.Success)
        {
            return main.Groups[1].Value;
        }

        Match content = ContentDiv().Match(html);
        if (content.Success)
        {
            return html[content.Index..];
        }

        Match body = BodyElement().Match(html);
        return body.Success ? body.Groups[1].Value : html;
    }

    [GeneratedRegex(@"<main\b[^>]*>(.*?)</main>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex MainElement();

    [GeneratedRegex("""<div\b[^>]*\bclass="[^"]*\bcontent\b[^"]*"[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex ContentDiv();

    [GeneratedRegex(@"<body\b[^>]*>(.*?)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex BodyElement();

    [GeneratedRegex(@"<(script|style|nav|header|footer|svg|noscript|form|button|select)\b.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DroppedElements();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<h([1-6])\b[^>]*>(.*?)</h\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Headings();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItems();

    [GeneratedRegex(@"<(br|/p|/div|/tr|/pre|/ul|/ol|/table|/dt|/dd|p|tr|pre)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex Spaces();
}

/// <summary>Pages fetched earlier, as trimmed text, one file per address in a cache folder outside the project.</summary>
public sealed class DocsCache(string folder)
{
    public string Folder { get; } = folder;

    /// <summary>The user-level cache: `%LOCALAPPDATA%/unitygraph/docs`, `~/Library/Caches/unitygraph/docs` or `$XDG_CACHE_HOME/unitygraph/docs`.</summary>
    public static string DefaultFolder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string root;
        if (OperatingSystem.IsWindows())
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else if (OperatingSystem.IsMacOS())
        {
            root = Path.Combine(home, "Library", "Caches");
        }
        else
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            root = string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".cache") : xdg;
        }

        return Path.Combine(root, "unitygraph", "docs");
    }

    public string? Get(string url)
    {
        try
        {
            string file = FileFor(url);
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Stores a page; a cache that cannot be written is not an error, the next call fetches again.</summary>
    public void Put(string url, string text)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FileFor(url), text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string FileFor(string url)
    {
        var name = new StringBuilder();
        foreach (char c in url.Replace("https://", "", StringComparison.Ordinal))
        {
            name.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '@' ? c : '_');
        }

        string text = name.ToString();
        return Path.Combine(Folder, (text.Length > 120 ? text[..120] : text) + ".txt");
    }
}

/// <summary>The outcome of a fetch: the page's HTML, or why there is none.</summary>
public sealed record DocsFetchResult(string? Html, string? Problem);

/// <summary>
/// The one network access of ug: a page from docs.unity3d.com over https, when the editor and the
/// local documentation cannot answer. Anything else is refused before a connection is made.
/// </summary>
public static class DocsFetcher
{
    public const string Host = "docs.unity3d.com";
    public const int MaxBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const int MaxRedirects = 4;

    /// <summary>Null when <paramref name="url"/> may be fetched, else why not.</summary>
    public static string? Refusal(string url)
    {
        bool allowed = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && uri.UserInfo.Length == 0;
        return allowed ? null : "only https://" + Host + " pages are fetched, not " + url;
    }

    /// <summary>Fetches <paramref name="url"/>; redirects are followed only within the same host.</summary>
    public static DocsFetchResult Fetch(string url)
    {
        if (Refusal(url) is { } refusal)
        {
            return new DocsFetchResult(null, refusal);
        }

        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout };
        try
        {
            string current = url;
            for (int hops = 0; hops <= MaxRedirects; hops++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using HttpResponseMessage response = http.Send(request, HttpCompletionOption.ResponseHeadersRead);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    string next = new Uri(new Uri(current), location).ToString();
                    if (Refusal(next) is { } bad)
                    {
                        return new DocsFetchResult(null, "redirected away: " + bad);
                    }

                    current = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new DocsFetchResult(null, "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " for " + current);
                }

                return Read(response);
            }

            return new DocsFetchResult(null, "too many redirects");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return new DocsFetchResult(null, e is TaskCanceledException ? "no answer within " + Timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s" : e.Message);
        }
    }

    private static DocsFetchResult Read(HttpResponseMessage response)
    {
        using Stream stream = response.Content.ReadAsStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes)
            {
                return new DocsFetchResult(null, "the page is over " + (MaxBytes / 1024 / 1024).ToString(CultureInfo.InvariantCulture) + " MB");
            }
        }

        return new DocsFetchResult(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), null);
    }
}

/// <summary>Turns the bridge's `local` / `online` answers into page text: local file, then cache, then a fetch.</summary>
public static class DocsPages
{
    /// <summary>
    /// The text to print for <paramref name="answer"/>: a page, or the bridge's error with why nothing was fetched
    /// (<paramref name="found"/> false). The cache keeps whole pages; each answer is cut to its own budget, and the
    /// cut line names <paramref name="again"/> with a bigger one.
    /// </summary>
    public static string Text(OpAnswer answer, bool online, int budgetTokens, DocsCache cache, Func<string, DocsFetchResult> fetch, out bool found, string? again = null)
    {
        found = true;
        if (answer.Local != null)
        {
            try
            {
                return Shown(DocsTrimmer.Plain(File.ReadAllText(answer.Local)), budgetTokens, answer.Local, again);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                found = false;
                return "cannot read " + answer.Local + ": " + e.Message + "\n";
            }
        }

        found = false;
        var notes = new List<string>();
        foreach (string url in answer.Online ?? [])
        {
            // Before C1 the cache held pages already cut to a budget: those are fetched again.
            if (cache.Get(url) is { } cached && !cached.Contains("\n... cut to ", StringComparison.Ordinal))
            {
                found = true;
                return Shown(cached, budgetTokens, url, again);
            }

            if (!online)
            {
                notes.Add(url);
                continue;
            }

            DocsFetchResult result = fetch(url);
            if (result.Html != null)
            {
                string whole = DocsTrimmer.Plain(result.Html);
                cache.Put(url, whole);
                found = true;
                return Shown(whole, budgetTokens, url, again);
            }

            notes.Add(url + " (" + result.Problem + ")");
        }

        var lines = new StringBuilder((answer.Error ?? "no page") + "\n");
        if (notes.Count > 0)
        {
            lines.Append(online ? "not fetched, read it at:\n" : "docs.online is off in .claude/unitygraph.json; read it at:\n");
            foreach (string note in notes)
            {
                lines.Append("  ").Append(note).Append('\n');
            }
        }

        return lines.ToString();
    }

    private static string Shown(string whole, int budgetTokens, string source, string? again)
    {
        string text = DocsTrimmer.Cut(whole, budgetTokens, source, again);
        return text.Length > 0 ? text : "the page has no text: " + source + "\n";
    }
}
