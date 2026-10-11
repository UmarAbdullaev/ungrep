using System.Diagnostics;

namespace Ungrep.Core.Graph;

/// <summary>Thrown when another writer holds the graph for longer than the caller would wait.</summary>
public sealed class GraphLockedException(TimeSpan waited)
    : Exception($"graph is locked by another writer ({WriteLock.RelativePath}, waited {waited.TotalMilliseconds:0} ms)");

/// <summary>
/// The single-writer lock at `Library/ungrep/write.lock`: an exclusive OS lock on the open
/// file (a share-none handle on Windows, flock on macOS and Linux), so it goes away with the
/// process that holds it. The file itself is never deleted.
/// </summary>
internal sealed class WriteLock : IDisposable
{
    public const string RelativePath = "Library/ungrep/write.lock";

    private static readonly TimeSpan _retry = TimeSpan.FromMilliseconds(10);

    private readonly FileStream _file;

    private WriteLock(FileStream file) => _file = file;

    private const string FileName = "write.lock";

    /// <summary>
    /// Locks `write.lock` in <paramref name="graphDirectory"/>, waiting up to
    /// <paramref name="timeout"/>; null when the lock is still held elsewhere.
    /// </summary>
    public static WriteLock? TryAcquire(string graphDirectory, TimeSpan timeout)
    {
        string path = Path.Combine(graphDirectory, FileName);
        Directory.CreateDirectory(graphDirectory);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new WriteLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1));
            }
            catch (IOException) when (clock.Elapsed < timeout)
            {
                Thread.Sleep(_retry);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>
/// `Library/ungrep/dirty.txt`: files a hook could not reindex because another writer held the
/// graph. One project-relative path per line; the next writer takes them and reindexes.
/// </summary>
public static class DirtyFiles
{
    public const string RelativePath = "Library/ungrep/dirty.txt";

    private static readonly TimeSpan _wait = TimeSpan.FromMilliseconds(250);

    /// <summary>Appends <paramref name="path"/>; false if the file stayed busy (the caller fails open).</summary>
    public static bool Mark(string projectRoot, string path)
    {
        if (path.Length == 0 || path.Contains('\n') || path.Contains('\r'))
        {
            throw new ArgumentException("path must be a single non-empty line", nameof(path));
        }

        string file = Path.Combine(projectRoot, RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return Exclusive(file, FileMode.Append, _wait, stream =>
        {
            using var writer = new StreamWriter(stream);
            writer.Write(path + "\n");
        });
    }

    // Called by GraphStore.TakeDirty, which holds the write lock, with the graph's folder.
    internal static IReadOnlyList<string> Take(string graphDirectory)
    {
        string file = Path.Combine(graphDirectory, Path.GetFileName(RelativePath));
        if (!File.Exists(file))
        {
            return [];
        }

        string text = "";
        bool taken = Exclusive(file, FileMode.Open, TimeSpan.FromSeconds(1), stream =>
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            text = reader.ReadToEnd();
            stream.SetLength(0);
        });
        if (!taken)
        {
            throw new IOException("dirty list stayed busy: " + file);
        }

        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static bool Exclusive(string file, FileMode mode, TimeSpan timeout, Action<FileStream> use)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(file, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite, FileShare.None);
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (IOException) when (clock.Elapsed < timeout)
            {
                Thread.Sleep(5);
                continue;
            }
            catch (IOException)
            {
                return false;
            }

            using (stream)
            {
                use(stream);
            }

            return true;
        }
    }
}
