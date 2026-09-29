using System.Security.Cryptography;

namespace UnityGraph.Core.Graph;

/// <summary>A file as it is on disk now: project-relative path, size, last write time.</summary>
public sealed record FileStat(string Path, long Size, long MtimeNs)
{
    /// <summary>Reads a file's size and mtime; null when it does not exist.</summary>
    public static FileStat? Of(string projectRoot, string path)
    {
        var info = new FileInfo(System.IO.Path.Combine(projectRoot, path));
        return info.Exists ? new FileStat(path, info.Length, MtimeNsOf(info)) : null;
    }

    /// <summary>Last write time in nanoseconds since the Unix epoch (100 ns resolution).</summary>
    public static long MtimeNsOf(FileInfo info) => (info.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100;
}

/// <summary>
/// What changed since files were indexed. <see cref="Touched"/> files have a new size or mtime
/// but the same content; <see cref="GraphStore.UpdateStats"/> records them so they are not hashed
/// again. Every list is sorted by path (ordinal).
/// </summary>
public sealed record FreshnessReport(
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<FileStat> Touched)
{
    public bool IsFresh => Changed.Count == 0 && Added.Count == 0 && Removed.Count == 0;
}

/// <summary>Content hashes as stored in <see cref="FileRecord.Hash"/>.</summary>
public static class FileHash
{
    /// <summary>SHA-256 of the file's bytes, 64 lowercase hex characters.</summary>
    public static string Of(string fullPath) => Convert.ToHexString(Bytes(fullPath)).ToLowerInvariant();

    internal static byte[] Bytes(string fullPath)
    {
        using FileStream stream = File.OpenRead(fullPath);
        return SHA256.HashData(stream);
    }
}
