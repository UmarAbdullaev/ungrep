using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnityGraph.Bench;

/// <summary>
/// How a step runs besides its call: why it is a redo, whether it counts, the poll (repeat until
/// the answer matches <see cref="Until"/>, every call counted), and values captured from its
/// answer (group 1 of each pattern) for `${name}` in later steps of the same leg.
/// </summary>
public sealed record StepControl(string? Redo, bool Counted, Regex? Until, TimeSpan Every, int MaxPolls, IReadOnlyDictionary<string, Regex> Capture)
{
    public static readonly StepControl Default = new(null, true, null, TimeSpan.FromSeconds(1), 90, new Dictionary<string, Regex>());
}

/// <summary>
/// One call of an arm: a shell command, a file read like the Read tool, or an MCP tool call.
/// <see cref="StepControl.Redo"/> says why the call repeats work an earlier answer should have done.
/// </summary>
public abstract record Step(StepControl Control)
{
    public string? Redo => Control.Redo;

    /// <summary>The step as the suite writes it, with project-relative paths: what results name it by.</summary>
    public abstract string Label { get; }
}

/// <summary>A shell command run with `bash -c` in the project folder.</summary>
public sealed record RunStep(string Command, TimeSpan Timeout, StepControl Control) : Step(Control)
{
    /// <summary>The Bash tool's own limit.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    public override string Label => Command;
}

/// <summary>A project file read the way the Read tool answers: numbered lines, at most 2,000.</summary>
public sealed record ReadStep(string Path, int? Offset, int? Limit, StepControl Control) : Step(Control)
{
    public override string Label => "read " + Path + (Offset is null ? "" : $":{Offset}") + (Limit is null ? "" : $"+{Limit}");
}

/// <summary>A tool call on one of the suite's MCP servers.</summary>
public sealed record McpStep(string Server, string Tool, string ArgumentsJson, StepControl Control) : Step(Control)
{
    public override string Label => $"mcp {Server} {Tool} {ArgumentsJson}";
}

/// <summary>How an MCP server is started: a command and its arguments, run through the same shell as steps.</summary>
public sealed record McpServer(string Command, IReadOnlyList<string> Args);

/// <summary>
/// What a question's answer must hold: regular expressions matched against the arm's answers (an
/// agent's final answer), against the check steps' output (a chore's effect), items that must not
/// appear in an agent's final answer, and how the key was verified by hand.
/// </summary>
public sealed record AnswerKey(IReadOnlyList<Regex> Items, IReadOnlyList<Regex> CheckItems, IReadOnlyList<Regex> Not, string Checked);

/// <summary>
/// One question: its prompt, key, and per arm (or named leg) the calls that answer it. A steps
/// entry that is a string names another entry whose calls it shares. Around each leg, not
/// counted: before steps, the leg, check steps, after steps.
/// </summary>
public sealed record Question(
    string Id,
    string Kind,
    string Prompt,
    IReadOnlyList<string> Needs,
    AnswerKey Key,
    IReadOnlyDictionary<string, IReadOnlyList<Step>> Legs,
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyList<Step> Before,
    IReadOnlyList<Step> Check,
    IReadOnlyList<Step> After)
{
    /// <summary>The steps entry an arm runs, following aliases; null when the arm has none.</summary>
    public string? LegOf(string arm)
    {
        string key = arm;
        for (int hops = 0; hops <= Aliases.Count; hops++)
        {
            if (Legs.ContainsKey(key))
            {
                return key;
            }

            if (!Aliases.TryGetValue(key, out string? next))
            {
                return null;
            }

            key = next;
        }

        return null;
    }
}

/// <summary>Agent-mode settings of an arm: what `claude -p` is given on top of the isolated base.</summary>
public sealed record AgentArm(bool PluginDir, IReadOnlyList<string> Mcp, IReadOnlyList<string> DisallowedTools, string? AppendSystemPrompt);

/// <summary>
/// An arm: preconditions, the skills it puts in context (idle cost), the pattern that recognises
/// its own calls in a transcript (redo detection), and its agent-mode settings.
/// </summary>
public sealed record Arm(string Name, IReadOnlyList<string> Needs, IReadOnlyList<string> Skills, Regex? Own, AgentArm Agent);

/// <summary>A benchmark suite for one Unity project (SPEC-bench, Suites).</summary>
public sealed record Suite(
    string Name,
    string? Project,
    string? Commit,
    IReadOnlyDictionary<string, string> Needs,
    IReadOnlyDictionary<string, McpServer> Servers,
    IReadOnlyList<Arm> Arms,
    string? AgentPrompt,
    string? GateProbe,
    IReadOnlyList<Question> Questions)
{
    private static readonly JsonDocumentOptions _options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <exception cref="FormatException">Not a valid suite; the message names the place.</exception>
    public static Suite Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, _options);
        }
        catch (JsonException e)
        {
            throw new FormatException("not a JSON suite: " + e.Message, e);
        }

        using (doc)
        {
            JsonElement root = Object(doc.RootElement, "suite");
            var needs = StringMap(root, "needs");
            var servers = new Dictionary<string, McpServer>(StringComparer.Ordinal);
            if (root.TryGetProperty("servers", out JsonElement serversElement))
            {
                foreach (JsonProperty server in Object(serversElement, "servers").EnumerateObject())
                {
                    JsonElement s = Object(server.Value, "servers." + server.Name);
                    servers[server.Name] = new McpServer(RequiredString(s, "command", "servers." + server.Name), Strings(s, "args", "servers." + server.Name));
                }
            }

            var arms = new List<Arm>();
            foreach (JsonProperty arm in Object(Required(root, "arms", "suite"), "arms").EnumerateObject())
            {
                arms.Add(ParseArm(arm.Name, Object(arm.Value, "arms." + arm.Name), needs, servers));
            }

            if (arms.Count == 0)
            {
                throw new FormatException("arms: at least one arm");
            }

            var questions = new List<Question>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            int index = 0;
            foreach (JsonElement q in Array(Required(root, "questions", "suite"), "questions"))
            {
                Question question = ParseQuestion(q, $"questions[{index++}]", arms, needs, servers);
                if (!ids.Add(question.Id))
                {
                    throw new FormatException($"questions: id {question.Id} twice");
                }

                questions.Add(question);
            }

            return new Suite(
                RequiredString(root, "suite", "suite"),
                OptionalString(root, "project"),
                OptionalString(root, "commit"),
                needs,
                servers,
                arms,
                OptionalString(root, "agentPrompt"),
                OptionalString(root, "gate"),
                questions);
        }
    }

    private static Arm ParseArm(string name, JsonElement arm, IReadOnlyDictionary<string, string> needs, IReadOnlyDictionary<string, McpServer> servers)
    {
        string where = "arms." + name;
        IReadOnlyList<string> armNeeds = KnownNeeds(Strings(arm, "needs", where), needs, where);
        Regex? own = OptionalString(arm, "own") is { } pattern ? Pattern(pattern, where + ".own") : null;
        var agent = new AgentArm(false, [], [], null);
        if (arm.TryGetProperty("agent", out JsonElement a))
        {
            Object(a, where + ".agent");
            IReadOnlyList<string> mcp = Strings(a, "mcp", where + ".agent");
            foreach (string server in mcp.Where(s => !servers.ContainsKey(s)))
            {
                throw new FormatException($"{where}.agent.mcp: no server {server}");
            }

            agent = new AgentArm(
                a.TryGetProperty("pluginDir", out JsonElement p) && p.ValueKind == JsonValueKind.True,
                mcp,
                Strings(a, "disallowedTools", where + ".agent"),
                OptionalString(a, "appendSystemPrompt"));
        }

        return new Arm(name, armNeeds, Strings(arm, "skills", where), own, agent);
    }

    private static Question ParseQuestion(JsonElement q, string where, IReadOnlyList<Arm> arms, IReadOnlyDictionary<string, string> needs, IReadOnlyDictionary<string, McpServer> servers)
    {
        Object(q, where);
        string id = RequiredString(q, "id", where);
        where = $"{where} ({id})";
        string kind = RequiredString(q, "kind", where);
        if (kind is not ("navigation" or "asset" or "chore"))
        {
            throw new FormatException($"{where}.kind: navigation, asset or chore, not {kind}");
        }

        JsonElement keyElement = Object(Required(q, "key", where), where + ".key");
        string keyWhere = where + ".key";
        var key = new AnswerKey(
            Patterns(keyElement, "items", keyWhere),
            Patterns(keyElement, "check", keyWhere),
            Patterns(keyElement, "not", keyWhere),
            RequiredString(keyElement, "checked", keyWhere));
        if (key.Items.Count + key.CheckItems.Count == 0)
        {
            throw new FormatException(keyWhere + ": at least one item or check item");
        }

        var legs = new Dictionary<string, IReadOnlyList<Step>>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty leg in Object(Required(q, "steps", where), where + ".steps").EnumerateObject())
        {
            if (leg.Value.ValueKind == JsonValueKind.String)
            {
                aliases[leg.Name] = leg.Value.GetString()!;
            }
            else
            {
                legs[leg.Name] = Steps(leg.Value, $"{where}.steps.{leg.Name}", servers);
            }
        }

        foreach ((string from, string to) in aliases)
        {
            if (!legs.ContainsKey(to) && !aliases.ContainsKey(to))
            {
                throw new FormatException($"{where}.steps.{from}: no steps named {to}");
            }
        }

        var question = new Question(
            id,
            kind,
            RequiredString(q, "prompt", where),
            KnownNeeds(Strings(q, "needs", where), needs, where),
            key,
            legs,
            aliases,
            q.TryGetProperty("before", out JsonElement before) ? Steps(before, where + ".before", servers) : [],
            q.TryGetProperty("check", out JsonElement check) ? Steps(check, where + ".check", servers) : [],
            q.TryGetProperty("after", out JsonElement after) ? Steps(after, where + ".after", servers) : []);

        foreach (string alias in aliases.Keys.Where(a => question.LegOf(a) is null))
        {
            throw new FormatException($"{where}.steps.{alias}: aliases go round in a circle");
        }

        if (key.CheckItems.Count > 0 && question.Check.Count == 0)
        {
            throw new FormatException(keyWhere + ".check: the question has no check steps");
        }

        if (!arms.Any(a => question.LegOf(a.Name) is not null))
        {
            throw new FormatException(where + ".steps: no arm has steps");
        }

        return question;
    }

    private static IReadOnlyList<Step> Steps(JsonElement element, string where, IReadOnlyDictionary<string, McpServer> servers)
    {
        var steps = new List<Step>();
        int i = 0;
        foreach (JsonElement s in Array(element, where))
        {
            steps.Add(ParseStep(Object(s, $"{where}[{i}]"), $"{where}[{i}]", servers));
            i++;
        }

        return steps;
    }

    private static Step ParseStep(JsonElement s, string where, IReadOnlyDictionary<string, McpServer> servers)
    {
        StepControl redo = Control(s, where);
        if (OptionalString(s, "run") is { } command)
        {
            TimeSpan timeout = RunStep.DefaultTimeout;
            if (s.TryGetProperty("timeout", out JsonElement t))
            {
                timeout = t.TryGetInt32(out int seconds) && seconds is > 0 and <= 3600
                    ? TimeSpan.FromSeconds(seconds)
                    : throw new FormatException(where + ".timeout: 1 to 3600 seconds");
            }

            return new RunStep(command, timeout, redo);
        }

        if (OptionalString(s, "read") is { } path)
        {
            return new ReadStep(path, OptionalPositive(s, "offset", where), OptionalPositive(s, "limit", where), redo);
        }

        if (OptionalString(s, "mcp") is { } server)
        {
            if (!servers.ContainsKey(server))
            {
                throw new FormatException($"{where}.mcp: no server {server}");
            }

            string args = s.TryGetProperty("args", out JsonElement a) ? Object(a, where + ".args").GetRawText() : "{}";
            using JsonDocument compact = JsonDocument.Parse(args);
            return new McpStep(server, RequiredString(s, "tool", where), JsonSerializer.Serialize(compact.RootElement), redo);
        }

        throw new FormatException(where + ": a step is run, read or mcp");
    }

    private static StepControl Control(JsonElement s, string where)
    {
        var capture = new Dictionary<string, Regex>(StringComparer.Ordinal);
        if (s.TryGetProperty("capture", out JsonElement c))
        {
            foreach (JsonProperty p in Object(c, where + ".capture").EnumerateObject())
            {
                capture[p.Name] = p.Value.ValueKind == JsonValueKind.String
                    ? Pattern(p.Value.GetString()!, $"{where}.capture.{p.Name}")
                    : throw new FormatException($"{where}.capture.{p.Name}: a string");
            }
        }

        string? until = OptionalString(s, "until", where);
        return new StepControl(
            OptionalString(s, "redo", where),
            !(s.TryGetProperty("count", out JsonElement count) && count.ValueKind == JsonValueKind.False),
            until is null ? null : Pattern(until, where + ".until"),
            OptionalPositive(s, "every", where) is { } every ? TimeSpan.FromSeconds(every) : StepControl.Default.Every,
            OptionalPositive(s, "max", where) ?? StepControl.Default.MaxPolls,
            capture);
    }

    private static IReadOnlyList<Regex> Patterns(JsonElement e, string name, string where) =>
        Strings(e, name, where).Select((p, i) => Pattern(p, $"{where}.{name}[{i}]")).ToList();

    private static IReadOnlyList<string> KnownNeeds(IReadOnlyList<string> named, IReadOnlyDictionary<string, string> needs, string where)
    {
        foreach (string need in named.Where(n => !needs.ContainsKey(n)))
        {
            throw new FormatException($"{where}.needs: {need} is not in the suite's needs");
        }

        return named;
    }

    private static Regex Pattern(string pattern, string where)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException e)
        {
            throw new FormatException($"{where}: bad regular expression: {e.Message}", e);
        }
    }

    private static Dictionary<string, string> StringMap(JsonElement root, string name)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty(name, out JsonElement element))
        {
            foreach (JsonProperty p in Object(element, name).EnumerateObject())
            {
                map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : throw new FormatException($"{name}.{p.Name}: a string");
            }
        }

        return map;
    }

    private static JsonElement Required(JsonElement e, string name, string where) =>
        e.TryGetProperty(name, out JsonElement value) ? value : throw new FormatException($"{where}: {name} is missing");

    private static string RequiredString(JsonElement e, string name, string where) =>
        OptionalString(e, name, where) is { Length: > 0 } value ? value : throw new FormatException($"{where}: {name} is missing");

    private static string? OptionalString(JsonElement e, string name, string? where = null) =>
        !e.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null ? null
        : value.ValueKind == JsonValueKind.String ? value.GetString()
        : throw new FormatException($"{(where is null ? "" : where + ".")}{name}: a string");

    private static int? OptionalPositive(JsonElement e, string name, string where) =>
        !e.TryGetProperty(name, out JsonElement value) ? null
        : value.TryGetInt32(out int n) && n > 0 ? n
        : throw new FormatException($"{where}.{name}: a positive whole number");

    private static IReadOnlyList<string> Strings(JsonElement e, string name, string where)
    {
        if (!e.TryGetProperty(name, out JsonElement value))
        {
            return [];
        }

        return Array(value, $"{where}.{name}")
            .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new FormatException($"{where}.{name}: strings only"))
            .ToList();
    }

    private static JsonElement Object(JsonElement e, string where) =>
        e.ValueKind == JsonValueKind.Object ? e : throw new FormatException(where + ": an object");

    private static JsonElement.ArrayEnumerator Array(JsonElement e, string where) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : throw new FormatException(where + ": a list");
}
