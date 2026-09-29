using System.CommandLine;
using System.Globalization;
using UnityGraph.Code;
using UnityGraph.Core;
using UnityGraph.Core.Graph;
using UnityGraph.Query;

namespace UnityGraph.Cli;

/// <summary>
/// `ug find`, `ug refs`, `ug explain` (SPEC-query): bring the code graph up to date, then answer
/// from it within the budget.
/// </summary>
internal static class QueryCommands
{
    public static IEnumerable<Command> Create()
    {
        var text = new Argument<string>("text") { Description = "Name or words of a name." };
        var kind = new Option<string>("--kind") { Description = "Only this kind: type, method, field, property, event." };
        kind.AcceptOnlyFromAmong("type", "method", "field", "property", "event");
        yield return Command("find", "Find types and members by name.", [text, kind], (result, engine, budget) =>
            engine.Find(result.GetValue(text)!, result.GetValue(kind) is { } k ? KindNames.ParseNode(k) : null, budget));

        var symbol = new Argument<string>("symbol") { Description = "Type or member: Name, Type.Member, Namespace.Type.Member(args), or a cs: key." };
        yield return Command("refs", "Everything that points at a symbol: callers, overrides, subclasses, users.", [symbol], (result, engine, budget) =>
            engine.Refs(result.GetValue(symbol)!, budget));

        var explained = new Argument<string>("symbol") { Description = "Type or member: Name, Type.Member, Namespace.Type.Member(args), or a cs: key." };
        var members = new Option<bool>("--members") { Description = "List a type's members too." };
        yield return Command("explain", "What a symbol is: location, Unity facts, members, users.", [explained, members], (result, engine, budget) =>
            engine.Explain(result.GetValue(explained)!, result.GetValue(members), budget));
    }

    private static Command Command(string name, string description, Symbol[] symbols, Func<ParseResult, QueryEngine, int, string> ask)
    {
        var project = new Option<string>("--project") { Description = "Folder inside the Unity project. Default: current folder." };
        var budget = new Option<int?>("--budget") { Description = $"Answer budget in tokens, {Answer.MinBudget} to {Answer.MaxBudget}. Default: answerBudget in .claude/unitygraph.json." };
        var graphDir = new Option<string>("--graph-dir") { Hidden = true };
        var command = new Command(name, description);
        foreach (Symbol symbol in symbols)
        {
            if (symbol is Argument argument)
            {
                command.Arguments.Add(argument);
            }
            else
            {
                command.Options.Add((Option)symbol);
            }
        }

        command.Options.Add(project);
        command.Options.Add(budget);
        command.Options.Add(graphDir);
        command.SetAction(result =>
        {
            string start = result.GetValue(project) ?? Directory.GetCurrentDirectory();
            int exitCode = Run(start, result.GetValue(budget), result.GetValue(graphDir), (engine, b) => ask(result, engine, b), out string text);
            result.InvocationConfiguration.Output.Write(text.ReplaceLineEndings());
            return exitCode;
        });
        return command;
    }

    public static int Run(string start, int? budget, string? graphDirectory, Func<QueryEngine, int, string> ask, out string text)
    {
        UnityProject? unity = UnityProject.Find(start);
        if (unity is null)
        {
            text = $"no Unity project at or above {Path.GetFullPath(start)}\n";
            return 1;
        }

        UnityGraphConfig config;
        try
        {
            config = UnityGraphConfig.Load(unity.Root);
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            text = $"{UnityGraphConfig.RelativePath}: {e.Message}\n";
            return 1;
        }

        int tokens = budget ?? config.AnswerBudget;
        if (tokens is < Answer.MinBudget or > Answer.MaxBudget)
        {
            text = $"--budget must be {Answer.MinBudget} to {Answer.MaxBudget}\n";
            return 1;
        }

        if (!config.IsEnabled("code-index") || !config.IsEnabled("query"))
        {
            text = "code-index or query is off in " + UnityGraphConfig.RelativePath + "; nothing to query\n";
            return 0;
        }

        CodeWorkspace workspace = CodeWorkspace.Load(unity.Root, config.Vendored);
        if (!workspace.HasProjects)
        {
            text = "no .csproj files: in Unity, Preferences > External Tools > Regenerate project files\n";
            return 0;
        }

        try
        {
            using GraphStore store = GraphStore.Open(unity.Root, StoreMode.ReadWrite, graphDirectory: graphDirectory);
            bool created = store.Created;
            IndexSummary summary = CodeIndex.Update(workspace, store);
            string indexed = created
                ? string.Create(CultureInfo.InvariantCulture, $"indexed {summary.Files:N0} files in {summary.Elapsed.TotalSeconds:0.0} s\n")
                : "";
            text = indexed + ask(new QueryEngine(store), tokens);
            return 0;
        }
        catch (GraphLockedException)
        {
            try
            {
                using GraphStore store = GraphStore.Open(unity.Root, StoreMode.ReadOnly, graphDirectory: graphDirectory);
                text = ask(new QueryEngine(store), tokens) + "note: graph busy, edits from the last seconds may be missing\n";
                return 0;
            }
            catch (GraphRebuildRequiredException)
            {
                text = "the graph is being built by another ug; try again in a few seconds\n";
                return 1;
            }
        }
    }
}
