using System.Text.Json;

namespace UnityGraph.Core;

/// <summary>
/// Per-project settings from `.claude/unitygraph.json` in the Unity project: one on/off switch
/// per module and the answer budget. Every setting has a default in code, so the file is
/// optional and holds only what differs.
/// </summary>
public sealed class UnityGraphConfig
{
    public const string RelativePath = ".claude/unitygraph.json";

    public const int DefaultAnswerBudget = 1200;
    public const int MinAnswerBudget = 200;
    public const int MaxAnswerBudget = 8000;

    /// <summary>
    /// Modules with a switch, and their defaults. `project-map` writes into the project, so it
    /// is opt-in (SPEC Decisions #7).
    /// </summary>
    public static IReadOnlyDictionary<string, bool> ModuleDefaults { get; } = new SortedDictionary<string, bool>(StringComparer.Ordinal)
    {
        ["asset-index"] = true,
        ["code-index"] = true,
        ["editor-link"] = true,
        ["guards"] = true,
        ["project-map"] = false,
        ["query"] = true,
        ["unity-loop"] = true,
        ["viewer"] = true,
        ["worker-agent"] = true,
    };

    private readonly IReadOnlyDictionary<string, bool> _modules;

    private UnityGraphConfig(IReadOnlyDictionary<string, bool> modules, int answerBudget, VendoredFolders? vendored, bool docsOnline = true)
    {
        _modules = modules;
        AnswerBudget = answerBudget;
        DocsOnline = docsOnline;
        Vendored = vendored ?? VendoredFolders.Default;
        VendoredIsDefault = vendored is null;
    }

    public static UnityGraphConfig Default { get; } = new(ModuleDefaults, DefaultAnswerBudget, null);

    /// <summary>Third-party folders: the `vendored` setting, or <see cref="VendoredFolders.Default"/>.</summary>
    public VendoredFolders Vendored { get; }

    public bool VendoredIsDefault { get; }

    /// <summary>Whether `ug api` and `ug docs` may fetch a page from docs.unity3d.com when nothing local answers (`docs.online`).</summary>
    public bool DocsOnline { get; }

    /// <summary>Maximum tokens in one query answer (design rule 1).</summary>
    public int AnswerBudget { get; }

    /// <summary>Modules switched away from their default.</summary>
    public IEnumerable<string> Overrides => _modules.Where(m => m.Value != ModuleDefaults[m.Key]).Select(m => m.Key);

    /// <exception cref="ArgumentException">Not a module with a switch.</exception>
    public bool IsEnabled(string module) =>
        _modules.TryGetValue(module, out bool enabled) ? enabled : throw new ArgumentException("unknown module: " + module, nameof(module));

    /// <summary>Loads the project's config, or the defaults when the file doesn't exist.</summary>
    /// <exception cref="FormatException">The file exists but isn't valid.</exception>
    public static UnityGraphConfig Load(string projectRoot)
    {
        string path = Path.Combine(projectRoot, RelativePath);
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : Default;
    }

    /// <exception cref="FormatException">With a message that names the bad setting.</exception>
    public static UnityGraphConfig Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            throw new FormatException("not valid JSON: " + e.Message, e);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("expected a JSON object");
            }

            var modules = new SortedDictionary<string, bool>(ModuleDefaults.ToDictionary(), StringComparer.Ordinal);
            int budget = DefaultAnswerBudget;
            VendoredFolders? vendored = null;
            bool docsOnline = true;
            foreach (JsonProperty setting in doc.RootElement.EnumerateObject())
            {
                switch (setting.Name)
                {
                    case "$schema":
                        break;
                    case "modules":
                        ReadModules(setting.Value, modules);
                        break;
                    case "answerBudget":
                        if (!setting.Value.TryGetInt32(out budget) || budget < MinAnswerBudget || budget > MaxAnswerBudget)
                        {
                            throw new FormatException($"answerBudget must be a whole number from {MinAnswerBudget} to {MaxAnswerBudget}");
                        }

                        break;
                    case "vendored":
                        vendored = ReadVendored(setting.Value);
                        break;
                    case "docs":
                        docsOnline = ReadDocs(setting.Value);
                        break;
                    default:
                        throw new FormatException($"unknown setting '{setting.Name}' (known: modules, answerBudget, vendored, docs)");
                }
            }

            return new UnityGraphConfig(modules, budget, vendored, docsOnline);
        }
    }

    private static bool ReadDocs(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("docs must be an object like { \"online\": false }");
        }

        bool online = true;
        foreach (JsonProperty setting in value.EnumerateObject())
        {
            if (setting.Name != "online")
            {
                throw new FormatException($"unknown setting 'docs.{setting.Name}' (known: online)");
            }

            if (setting.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new FormatException("docs.online must be true or false");
            }

            online = setting.Value.GetBoolean();
        }

        return online;
    }

    private static VendoredFolders ReadVendored(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("vendored must be an array of folder prefixes like \"Assets/Plugins/\"");
        }

        var prefixes = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new FormatException($"vendored: {item.GetRawText()} is not a string");
            }

            string prefix = item.GetString()!;
            if (VendoredFolders.Problem(prefix) is { } problem)
            {
                throw new FormatException($"vendored: \"{prefix}\" {problem}");
            }

            prefixes.Add(prefix);
        }

        return new VendoredFolders(prefixes);
    }

    private static void ReadModules(JsonElement value, SortedDictionary<string, bool> modules)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("modules must be an object of module names to true/false");
        }

        foreach (JsonProperty module in value.EnumerateObject())
        {
            if (!ModuleDefaults.ContainsKey(module.Name))
            {
                throw new FormatException($"unknown module '{module.Name}' (known: {string.Join(", ", ModuleDefaults.Keys)})");
            }

            if (module.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new FormatException($"modules.{module.Name} must be true or false");
            }

            modules[module.Name] = module.Value.GetBoolean();
        }
    }
}
