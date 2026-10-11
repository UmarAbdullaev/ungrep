using System.Text;
using System.Text.Json;

namespace UnityGraph.Bench;

/// <summary>
/// A skill's two costs (SPEC.md Overhead budget): the line Claude Code lists every session,
/// `- [plugin:]name: description`, and the body after the front matter, loaded when the skill is
/// used. A skill under `&lt;plugin&gt;/skills/&lt;name&gt;/` is listed with its plugin's name.
/// </summary>
public sealed record SkillFile(string Path, string Listing, string Body)
{
    /// <summary>Tokens the way the budgets count them: UTF-8 bytes / 4, rounded up.</summary>
    public static int Tokens(string text) => (Encoding.UTF8.GetByteCount(text) + 3) / 4;

    /// <summary>`${plugin}` is the plugin root, `~` the home folder; other relative paths are under <paramref name="baseDir"/>.</summary>
    public static string Resolve(string path, string? pluginRoot, string baseDir)
    {
        if (pluginRoot is not null)
        {
            path = path.Replace("${plugin}", pluginRoot, StringComparison.Ordinal);
        }

        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
        {
            path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
        }

        return System.IO.Path.GetFullPath(path, baseDir);
    }

    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="FormatException">No front matter, or no name or description in it.</exception>
    public static SkillFile Read(string path)
    {
        string text = File.ReadAllText(path).ReplaceLineEndings("\n");
        int end = text.StartsWith("---\n", StringComparison.Ordinal) ? text.IndexOf("\n---\n", 3, StringComparison.Ordinal) : -1;
        if (end < 0)
        {
            throw new FormatException(path + ": no front matter");
        }

        Dictionary<string, string> fields = FrontMatter(text[4..end]);
        if (!fields.TryGetValue("name", out string? name) || !fields.TryGetValue("description", out string? description))
        {
            throw new FormatException(path + ": front matter has no name or description");
        }

        string? plugin = PluginName(path);
        string listing = $"- {(plugin is null ? "" : plugin + ":")}{name}: {description}\n";
        return new SkillFile(path, listing, text[(end + 5)..]);
    }

    /// <summary>`key: value` lines; a quoted value is unquoted, a `>` or `|` value takes the indented lines below it.</summary>
    private static Dictionary<string, string> FrontMatter(string block)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] lines = block.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (value is ">" or "|" or ">-" or "|-")
            {
                var parts = new List<string>();
                while (i + 1 < lines.Length && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
                {
                    parts.Add(lines[++i].Trim());
                }

                value = string.Join(value.StartsWith('>') ? " " : "\n", parts).Trim();
            }
            else if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            fields[key] = value;
        }

        return fields;
    }

    private static string? PluginName(string skillPath)
    {
        // <root>/skills/<name>/SKILL.md
        DirectoryInfo? skills = Directory.GetParent(skillPath)?.Parent;
        if (skills?.Name != "skills" || skills.Parent is not { } root)
        {
            return null;
        }

        string manifest = System.IO.Path.Combine(root.FullName, ".claude-plugin", "plugin.json");
        if (!File.Exists(manifest))
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
        return doc.RootElement.TryGetProperty("name", out JsonElement name) ? name.GetString() : null;
    }
}
