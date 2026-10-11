using System.Globalization;
using System.Text;

namespace UnityGraph.EditorLink;

/// <summary>
/// `ug perf snapshot list|compare` (SPEC-editor-ops, Profiler): memory snapshots read in the client,
/// with Unity closed and without the Memory Profiler package.
/// </summary>
public static class SnapshotAnswers
{
    /// <summary>Where `ug perf snapshot take` writes, relative to the project.</summary>
    public const string Folder = "Library/unitygraph/snapshots";

    /// <summary>The Memory Profiler package's folder, relative to the project.</summary>
    public const string PackageFolder = "MemoryCaptures";

    private const int ListCap = 20;

    /// <summary>Snapshots newest first, in <paramref name="folder"/> or the two usual folders.</summary>
    public static string List(string root, string? folder, bool all)
    {
        string[] folders = folder == null ? [Path.Combine(root, Folder), Path.Combine(root, PackageFolder)] : [Path.Combine(root, folder)];
        if (folder != null && !Directory.Exists(folders[0]))
        {
            return "no folder " + Shown(root, folders[0]) + "\n";
        }

        var files = folders.Where(Directory.Exists)
            .SelectMany(f => Directory.EnumerateFiles(f, "*.snap"))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        if (files.Count == 0)
        {
            return "no snapshots in " + string.Join(" or ", folders.Select(f => Shown(root, f) + "/")) + ": ug perf snapshot take\n";
        }

        var text = new StringBuilder();
        foreach (FileInfo file in all ? files : files.Take(ListCap))
        {
            text.Append(Shown(root, file.FullName)).Append("  ").Append(Size((ulong)file.Length)).Append("  ");
            try
            {
                MemorySnapshot snapshot = MemorySnapshot.Read(file.FullName);
                text.Append(Describe(snapshot, file));
                if (snapshot.Stats != null)
                {
                    text.Append("  used ").Append(Size(snapshot.Stats.TotalUsed)).Append(',');
                }

                text.Append(' ').Append(Number(snapshot.Objects.Count)).Append(" native objects");
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                text.Append(e.Message);
            }

            text.Append('\n');
        }

        if (!all && files.Count > ListCap)
        {
            text.Append("... ").Append(files.Count - ListCap).Append(" more: ug perf snapshot list --all")
                .Append(folder == null ? "" : " --folder " + folder).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Finds a snapshot by path (relative to the project or absolute) or by file name in the usual folders.</summary>
    public static string? Resolve(string root, string given)
    {
        string path = Path.Combine(root, given);
        if (File.Exists(path))
        {
            return Path.GetFullPath(path);
        }

        if (given.IndexOfAny(['/', '\\']) < 0)
        {
            string name = given.EndsWith(".snap", StringComparison.OrdinalIgnoreCase) ? given : given + ".snap";
            return new[] { Folder, PackageFolder }.Select(f => Path.GetFullPath(Path.Combine(root, f, name))).FirstOrDefault(File.Exists);
        }

        return null;
    }

    /// <summary>What changed from snapshot <paramref name="a"/> to <paramref name="b"/>: totals, types and objects.</summary>
    public static string Compare(string root, string a, string b, int top)
    {
        MemorySnapshot first = MemorySnapshot.Read(a);
        MemorySnapshot second = MemorySnapshot.Read(b);
        bool sameSession = first.SessionGuid != null && first.SessionGuid == second.SessionGuid;
        var text = new StringBuilder();
        text.Append("a  ").Append(Shown(root, a)).Append("  ").Append(Describe(first, new FileInfo(a))).Append('\n');
        text.Append("b  ").Append(Shown(root, b)).Append("  ")
            .Append(sameSession ? Time(second, new FileInfo(b)) + "  same session (objects matched by instance ID)" : Describe(second, new FileInfo(b)) + "  (objects matched by type and name)")
            .Append('\n');

        if (first.Stats != null && second.Stats != null)
        {
            (string Label, ulong A, ulong B)[] stats =
            [
                ("total used", first.Stats.TotalUsed, second.Stats.TotalUsed),
                ("reserved", first.Stats.TotalReserved, second.Stats.TotalReserved),
                ("graphics", first.Stats.Graphics, second.Stats.Graphics),
                ("audio", first.Stats.Audio, second.Stats.Audio),
                ("GC heap used", first.Stats.GcHeapUsed, second.Stats.GcHeapUsed),
                ("GC heap reserved", first.Stats.GcHeapReserved, second.Stats.GcHeapReserved),
                ("profiler", first.Stats.Profiler, second.Stats.Profiler),
            ];
            text.Append("memory  ").Append(string.Join(", ", stats.Select(s => s.Label + " " + Change(s.A, s.B)))).Append('\n');
        }
        else
        {
            text.Append("memory  totals not in format version ").Append(Math.Min(first.FormatVersion, second.FormatVersion)).Append(" (Unity 2021.2 and up write them)\n");
        }

        text.Append("managed heap sections  ").Append(Change(first.ManagedHeapBytes, second.ManagedHeapBytes)).Append('\n');
        ulong sizeA = Total(first.Objects);
        ulong sizeB = Total(second.Objects);
        text.Append("native objects  ").Append(Number(first.Objects.Count)).Append(" -> ").Append(Number(second.Objects.Count))
            .Append(" (").Append(Signed(second.Objects.Count - first.Objects.Count)).Append("), ").Append(Change(sizeA, sizeB)).Append('\n');

        string more = "ug perf snapshot compare " + Quoted(Shown(root, a)) + " " + Quoted(Shown(root, b)) + " --top " + (top * 3).ToString(CultureInfo.InvariantCulture);
        var typesA = first.Objects.GroupBy(o => o.Type).ToDictionary(g => g.Key, g => (Count: g.Count(), Size: Total(g)));
        var typesB = second.Objects.GroupBy(o => o.Type).ToDictionary(g => g.Key, g => (Count: g.Count(), Size: Total(g)));
        var types = typesA.Keys.Union(typesB.Keys)
            .Select(t => (Type: t, A: typesA.GetValueOrDefault(t), B: typesB.GetValueOrDefault(t)))
            .Where(t => t.A != t.B)
            .OrderByDescending(t => Math.Abs((double)t.B.Size - t.A.Size))
            .ThenByDescending(t => Math.Abs(t.B.Count - t.A.Count))
            .ToList();
        text.Append("types by size change").Append(types.Count == 0 ? ": none\n" : ":\n");
        foreach (var t in types.Take(top))
        {
            text.Append("  ").Append(t.Type).Append("  ").Append(Delta(t.A.Size, t.B.Size)).Append("  ")
                .Append(Number(t.A.Count)).Append(" -> ").Append(Number(t.B.Count)).Append('\n');
        }

        More(text, types.Count, top, more);

        (List<SnapshotObject> added, List<SnapshotObject> gone, List<(SnapshotObject A, SnapshotObject B)> grown) = Match(first.Objects, second.Objects, sameSession);
        Objects(text, "new in b", added, top, more);
        Objects(text, "gone from a", gone, top, more);
        var changed = grown.Where(p => p.A.Size != p.B.Size).OrderByDescending(p => Math.Abs((double)p.B.Size - p.A.Size)).ToList();
        text.Append("changed size: ").Append(changed.Count == 0 ? "none\n" : Number(changed.Count) + " objects, " + Delta(Total(changed.Select(p => p.A)), Total(changed.Select(p => p.B))) + "; largest change:\n");
        foreach (var (was, now) in changed.Take(top))
        {
            text.Append("  ").Append(Delta(was.Size, now.Size)).Append("  ").Append(now.Type).Append("  ").Append(Named(now))
                .Append("  ").Append(Size(was.Size)).Append(" -> ").Append(Size(now.Size)).Append('\n');
        }

        More(text, changed.Count, top, more);
        text.Append("managed objects are not compared: crawling the managed heap is the Memory Profiler package's work\n");
        return text.ToString();
    }

    // Objects only in one snapshot, and pairs present in both. One session: instance IDs; else type
    // and name, the n-th of a name in a matched with the n-th in b.
    private static (List<SnapshotObject> Added, List<SnapshotObject> Gone, List<(SnapshotObject, SnapshotObject)> Kept) Match(
        IReadOnlyList<SnapshotObject> a, IReadOnlyList<SnapshotObject> b, bool byId)
    {
        var waiting = new Dictionary<(string, string, long), Queue<SnapshotObject>>();
        foreach (SnapshotObject o in a)
        {
            var key = Key(o);
            if (!waiting.TryGetValue(key, out Queue<SnapshotObject>? queue))
            {
                waiting[key] = queue = new Queue<SnapshotObject>();
            }

            queue.Enqueue(o);
        }

        var added = new List<SnapshotObject>();
        var kept = new List<(SnapshotObject, SnapshotObject)>();
        foreach (SnapshotObject o in b)
        {
            if (waiting.TryGetValue(Key(o), out Queue<SnapshotObject>? queue) && queue.Count > 0)
            {
                kept.Add((queue.Dequeue(), o));
            }
            else
            {
                added.Add(o);
            }
        }

        return (added, [.. waiting.Values.SelectMany(q => q)], kept);

        (string, string, long) Key(SnapshotObject o) => byId ? ("", "", o.InstanceId) : (o.Type, o.Name, 0);
    }

    private static void Objects(StringBuilder text, string title, List<SnapshotObject> objects, int top, string more)
    {
        text.Append(title).Append(": ").Append(objects.Count == 0 ? "none\n" : Number(objects.Count) + " objects, " + Size(Total(objects)) + "; largest:\n");
        foreach (SnapshotObject o in objects.OrderByDescending(o => o.Size).Take(top))
        {
            text.Append("  ").Append(Size(o.Size)).Append("  ").Append(o.Type).Append("  ").Append(Named(o)).Append('\n');
        }

        More(text, objects.Count, top, more);
    }

    private static void More(StringBuilder text, int count, int top, string more)
    {
        if (count > top)
        {
            text.Append("  ... ").Append(Number(count - top)).Append(" more: ").Append(more).Append('\n');
        }
    }

    private static string Describe(MemorySnapshot snapshot, FileInfo file)
    {
        var parts = new List<string> { Time(snapshot, file) };
        parts.AddRange(new[] { snapshot.Product, snapshot.UnityVersion, snapshot.Platform }.OfType<string>().Where(p => p.Length > 0));
        return string.Join("  ", parts);
    }

    // The capture time in local time; the file's time when the snapshot has none.
    private static string Time(MemorySnapshot snapshot, FileInfo file) =>
        (snapshot.RecordDate?.ToLocalTime() ?? file.LastWriteTime).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Named(SnapshotObject o) => o.Name.Length == 0 ? "(no name, id " + o.InstanceId.ToString(CultureInfo.InvariantCulture) + ")" : o.Name;

    private static ulong Total(IEnumerable<SnapshotObject> objects) => objects.Aggregate(0UL, (sum, o) => sum + o.Size);

    private static string Change(ulong a, ulong b) => Size(a) + " -> " + Size(b) + (a == b ? "" : " (" + Delta(a, b) + ")");

    private static string Delta(ulong a, ulong b) => b >= a ? "+" + Size(b - a) : "-" + Size(a - b);

    private static string Signed(int value) => (value >= 0 ? "+" : "") + Number(value);

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Quoted(string path) => path.Contains(' ', StringComparison.Ordinal) ? "\"" + path + "\"" : path;

    // A path relative to the project when inside it, else the full path; forward slashes either way.
    private static string Shown(string root, string path)
    {
        string full = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(root, full);
        return (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative).Replace('\\', '/');
    }

    /// <summary>Bytes as B, KB, MB or GB with one decimal.</summary>
    public static string Size(ulong bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        < 1024UL * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB",
    };
}
