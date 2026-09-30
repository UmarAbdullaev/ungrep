using System.CommandLine;
using System.Globalization;
using UnityGraph.Core;
using UnityGraph.Core.Graph;
using UnityGraph.Query;

namespace UnityGraph.Cli;

/// <summary>
/// `ug find`, `ug refs`, `ug explain` (SPEC-query): bring the code graph up to date, then answer
/// from it within the budget. `ug find --live` asks the open editor instead (SPEC-editor-ops).
/// </summary>
internal static class QueryCommands
{
    public static IEnumerable<Command> Create()
    {
        var text = new Argument<string?>("text") { Description = "Name or words of a name.", Arity = ArgumentArity.ZeroOrOne };
        var kind = new Option<string>("--kind") { Description = "Only this kind: type, method, field, property, event, asset, game_object, prefab_instance." };
        kind.AcceptOnlyFromAmong("type", "method", "field", "property", "event", "asset", "game_object", "prefab_instance");
        var live = new Option<bool>("--live") { Description = "Search the open editor's loaded scenes (unsaved and play-mode state) instead of the graph." };
        var tag = new Option<string?>("--tag") { Description = "With --live: only objects with this tag." };
        var layer = new Option<string?>("--layer") { Description = "With --live: only objects on this layer (name or number)." };
        var component = new Option<string?>("--component") { Description = "With --live: only objects with this component type." };
        Command find = Command("find", "Find types, members, assets and game objects by name.", [text, kind, live, tag, layer, component], (result, engine, budget) =>
            engine.Find(result.GetValue(text)!, result.GetValue(kind) is { } k ? KindNames.ParseNode(k) : null, budget),
            (result, start) => result.GetValue(live)
                ? EditorCommands.LiveFind(start, result.GetValue(text), result.GetValue(tag), result.GetValue(layer), result.GetValue(component))
                : null);
        find.Validators.Add(result =>
        {
            if (result.GetValue(live))
            {
                if (result.GetValue(kind) != null)
                {
                    result.AddError("--kind is for the graph; --live finds game objects only");
                }
            }
            else if (result.GetValue(tag) != null || result.GetValue(layer) != null || result.GetValue(component) != null)
            {
                result.AddError("--tag, --layer and --component need --live");
            }
            else if (string.IsNullOrWhiteSpace(result.GetValue(text)))
            {
                result.AddError("give a name to find");
            }
        });
        yield return find;

        var symbol = new Argument<string>("symbol") { Description = "Type or member: Name, Type.Member, Namespace.Type.Member(args), or a cs: key." };
        yield return Command("refs", "Everything that points at a symbol: callers, overrides, subclasses, users.", [symbol], (result, engine, budget) =>
            engine.Refs(result.GetValue(symbol)!, budget));

        var explained = new Argument<string>("symbol") { Description = "Type or member: Name, Type.Member, Namespace.Type.Member(args), or a cs: key." };
        var members = new Option<bool>("--members") { Description = "List a type's members too." };
        yield return Command("explain", "What a symbol is: location, Unity facts, members, users.", [explained, members], (result, engine, budget) =>
            engine.Explain(result.GetValue(explained)!, result.GetValue(members), budget));
    }

    // instead: answers without the graph when it returns an answer (`find --live`); gets the start folder.
    private static Command Command(string name, string description, Symbol[] symbols, Func<ParseResult, QueryEngine, int, string> ask, Func<ParseResult, string, (int, string)?>? instead = null)
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
            if (instead?.Invoke(result, start) is (int code, string answer))
            {
                result.InvocationConfiguration.Output.Write(answer.ReplaceLineEndings());
                return code;
            }

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

        GraphUpdate update = GraphUpdate.Load(unity.Root, config);
        if (update.Code is null)
        {
            text = GraphUpdate.NoProjects + "\n";
            return 0;
        }

        try
        {
            using GraphStore store = GraphStore.Open(unity.Root, StoreMode.ReadWrite, graphDirectory: graphDirectory);
            bool created = store.Created;
            IndexSummary summary = update.Update(store);
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
