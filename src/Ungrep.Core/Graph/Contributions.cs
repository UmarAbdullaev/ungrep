namespace Ungrep.Core.Graph;

/// <summary>
/// Writes indexer output into the graph. A key two files declare (the same type in two
/// assemblies, a GUID copied with its `.meta`) stays with its first or current owner, and the
/// other file gets a problem line.
/// </summary>
public static class Contributions
{
    /// <summary>Adds <paramref name="files"/> to a bulk index; the caller commits.</summary>
    public static (int Nodes, int Edges) AddAll(BulkWriter writer, IEnumerable<FileContribution> files, List<string> problems)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        int nodes = 0, edges = 0;
        foreach (FileContribution file in files)
        {
            var kept = new List<Node>(file.Nodes.Count);
            foreach (Node node in file.Nodes)
            {
                if (owners.TryAdd(node.Key, file.File.Path))
                {
                    kept.Add(node);
                }
                else
                {
                    problems.Add(Conflict(file.File.Path, node.Key, owners[node.Key]));
                }
            }

            if (TryWrite(writer.Add, file, kept, problems))
            {
                (nodes, edges) = (nodes + kept.Count, edges + file.Edges.Count);
            }
        }

        return (nodes, edges);
    }

    /// <summary>
    /// Replaces <paramref name="files"/> (sorted by path) in <paramref name="store"/>. A key
    /// stays with its current owner; a file that gave way to a file later in this batch is
    /// written again once that file has had its turn.
    /// </summary>
    public static (int Nodes, int Edges) Write(GraphStore store, List<FileContribution> files, List<string> problems)
    {
        var batch = files.Select(f => f.File.Path).ToHashSet(StringComparer.Ordinal);
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        var retry = new List<FileContribution>();
        int nodes = 0, edges = 0;
        foreach (FileContribution file in files)
        {
            string path = file.File.Path;
            var kept = new List<Node>(file.Nodes.Count);
            bool deferred = false;
            foreach (Node node in file.Nodes)
            {
                string? owner = claimed.GetValueOrDefault(node.Key);
                if (owner is null && store.GetNode(node.Key)?.File is { } stored && stored != path)
                {
                    if (batch.Contains(stored))
                    {
                        deferred = true;
                        continue;
                    }

                    owner = stored;
                }

                if (owner is null)
                {
                    kept.Add(node);
                    claimed[node.Key] = path;
                }
                else
                {
                    problems.Add(Conflict(path, node.Key, owner));
                }
            }

            if (TryWrite(store.ReplaceFile, file, kept, problems))
            {
                (nodes, edges) = (nodes + kept.Count, edges + file.Edges.Count);
            }

            if (deferred)
            {
                retry.Add(file);
            }
        }

        foreach (FileContribution file in retry)
        {
            string path = file.File.Path;
            var kept = new List<Node>(file.Nodes.Count);
            foreach (Node node in file.Nodes)
            {
                if (store.GetNode(node.Key)?.File is { } owner && owner != path)
                {
                    problems.Add(Conflict(path, node.Key, owner));
                }
                else
                {
                    kept.Add(node);
                }
            }

            int before = store.KeysInFile(path).Count;
            nodes += TryWrite(store.ReplaceFile, file, kept, problems) ? kept.Count - before : -before;
        }

        return (nodes, edges);
    }

    // A row the store rejects is an indexer bug. The file keeps its record without rows and gets a
    // problem line, so one odd file never stops the index (fail open).
    private static bool TryWrite(Action<FileRecord, IReadOnlyList<Node>, IReadOnlyList<Edge>> write, FileContribution file, List<Node> kept, List<string> problems)
    {
        try
        {
            write(file.File, kept, file.Edges);
            return true;
        }
        catch (ArgumentException e)
        {
            problems.Add(e.Message.Replace($" (Parameter '{e.ParamName}')", "", StringComparison.Ordinal) + "; its rows are left out");
            write(file.File, [], []);
            return false;
        }
    }

    private static string Conflict(string path, string key, string owner) => $"{path}: {key} is also declared in {owner}; kept there";
}
