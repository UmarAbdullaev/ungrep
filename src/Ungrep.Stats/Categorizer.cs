using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ungrep.Stats;

/// <summary>
/// Sorts a tool call into the category the context cost is charged to. A line-for-line port of
/// `categorize` in research/transcript-analysis/usage.js, so the numbers stay comparable with the
/// Evidence section of SPEC.md. Regexes use ECMAScript semantics for the same reason.
/// </summary>
public static partial class Categorizer
{
    private const string UnityYaml = @"\.(unity|prefab|asset|mat|anim|controller|overridecontroller|meta|inputactions)\b";

    public static string Categorize(string toolName, JsonElement input)
    {
        if (toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return CategorizeMcp(toolName);
        }

        if (toolName is "Bash" or "PowerShell")
        {
            return CategorizeShell(GetString(input, "command"));
        }

        string file = GetString(input, "file_path");
        if (file.Length == 0)
        {
            file = GetString(input, "path");
        }

        switch (toolName)
        {
            case "Read":
                if (UnityYamlRegex().IsMatch(file)) return "read/search: unity yaml";
                if (CsFileRegex().IsMatch(file)) return "read: .cs";
                if (MdFileRegex().IsMatch(file)) return "read: .md (plan/map/docs)";
                if (ImageFileRegex().IsMatch(file)) return "read: image";
                return "read: other";

            case "Grep" or "Glob":
                string raw = input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText();
                return UnityYamlRegex().IsMatch(raw) ? "read/search: unity yaml" : "search: grep/glob";

            case "Edit" or "Write" or "MultiEdit":
                if (CsFileRegex().IsMatch(file)) return "write: .cs";
                if (MdFileRegex().IsMatch(file)) return "write: .md";
                return "write: other";

            case "Agent" or "Task":
                return "subagent";

            case "ToolSearch":
                return "toolsearch";

            default:
                return "other: " + toolName;
        }
    }

    private static string CategorizeMcp(string toolName)
    {
        if (!UnityOrPipelineRegex().IsMatch(toolName))
        {
            return BrowserRegex().IsMatch(toolName) ? "browser" : "other mcp";
        }

        string shortName = McpPrefixRegex().Replace(toolName, "", 1);
        if (ConsoleRegex().IsMatch(shortName)) return "unity: console/logs";
        if (TestRegex().IsMatch(shortName)) return "unity: tests";
        if (McpCompileRegex().IsMatch(shortName)) return "unity: compile/refresh/status";
        if (McpPlayRegex().IsMatch(shortName)) return "unity: play/screenshot";
        if (McpEvalRegex().IsMatch(shortName)) return "unity: eval/run_script";
        if (McpInspectRegex().IsMatch(shortName)) return "unity: scene/asset inspect+edit";
        return "unity: other mcp";
    }

    private static string CategorizeShell(string command)
    {
        if (UnityCliRegex().IsMatch(command))
        {
            Match m = UnityCliCommandRegex().Match(command);
            string k = m.Success ? m.Groups[1].Value : "?";
            if (ConsoleRegex().IsMatch(k)) return "unity: console/logs";
            if (TestRegex().IsMatch(k)) return "unity: tests";
            if (CliCompileRegex().IsMatch(k)) return "unity: compile/refresh/status";
            if (CliPlayRegex().IsMatch(k)) return "unity: play/screenshot";
            if (CliEvalRegex().IsMatch(k)) return "unity: eval/run_script";
            return "unity: other cli";
        }

        if (EditorLogRegex().IsMatch(command)) return "unity: console/logs";
        if (GitRegex().IsMatch(command)) return "shell: git";
        if (ShellReadRegex().IsMatch(command))
        {
            return UnityYamlRegex().IsMatch(command) ? "read/search: unity yaml" : "shell: search/read";
        }

        if (WaitRegex().IsMatch(command)) return "shell: waiting";
        return "shell: other";
    }

    private static string GetString(JsonElement input, string property) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : "";

    private const RegexOptions Js = RegexOptions.ECMAScript;
    private const RegexOptions JsI = RegexOptions.ECMAScript | RegexOptions.IgnoreCase;

    [GeneratedRegex(UnityYaml, JsI)] private static partial Regex UnityYamlRegex();
    [GeneratedRegex(@"\.cs$", JsI)] private static partial Regex CsFileRegex();
    [GeneratedRegex(@"\.md$", JsI)] private static partial Regex MdFileRegex();
    [GeneratedRegex(@"\.(png|jpg|jpeg)$", JsI)] private static partial Regex ImageFileRegex();

    [GeneratedRegex("^mcp__[^_]+(?:_[^_]+)*?__", Js)] private static partial Regex McpPrefixRegex();
    [GeneratedRegex("unity|pipeline", JsI)] private static partial Regex UnityOrPipelineRegex();
    [GeneratedRegex("browser|chrome", JsI)] private static partial Regex BrowserRegex();
    [GeneratedRegex("console|log", Js)] private static partial Regex ConsoleRegex();
    [GeneratedRegex("test", Js)] private static partial Regex TestRegex();
    [GeneratedRegex("compile|refresh|status|reimport|domain", Js)] private static partial Regex McpCompileRegex();
    [GeneratedRegex("play|screenshot|capture|pause|stop", Js)] private static partial Regex McpPlayRegex();
    [GeneratedRegex("eval|execute|run_script|script", Js)] private static partial Regex McpEvalRegex();
    [GeneratedRegex("scene|gameobject|component|prefab|hierarchy|asset|find|inspect|get_", Js)] private static partial Regex McpInspectRegex();

    [GeneratedRegex(@"unity(\.exe)?\s+command", JsI)] private static partial Regex UnityCliRegex();
    [GeneratedRegex(@"unity(?:\.exe)?\s+command\b[^\n]*?\s(?:--project-path\s+""[^""]+""\s+)?([a-z_]+)", JsI)] private static partial Regex UnityCliCommandRegex();
    [GeneratedRegex("compile|refresh|status|reimport", Js)] private static partial Regex CliCompileRegex();
    [GeneratedRegex("play|screenshot", Js)] private static partial Regex CliPlayRegex();
    [GeneratedRegex("eval|run_script|execute", Js)] private static partial Regex CliEvalRegex();
    [GeneratedRegex(@"editor\.log", JsI)] private static partial Regex EditorLogRegex();
    [GeneratedRegex(@"\bgit\b", Js)] private static partial Regex GitRegex();
    [GeneratedRegex(@"\b(grep|rg|find|ls|cat|head|tail|sed|wc|Select-String|Get-ChildItem|Get-Content)\b", JsI)] private static partial Regex ShellReadRegex();
    [GeneratedRegex(@"\b(sleep|Start-Sleep|timeout)\b", JsI)] private static partial Regex WaitRegex();
}
