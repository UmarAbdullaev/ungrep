using System.Globalization;
using System.Text;

namespace Ungrep.EditorLink.Tests;

/// <summary>
/// The `.snap` reader and `ug perf snapshot list|compare` on snapshots written here in Unity's
/// layout: blocks split into chunks stored out of order, single, fixed-size and sized-element
/// entries, 4- and 8-byte instance IDs, and the files ug refuses.
/// </summary>
public sealed class MemorySnapshotTests : IDisposable
{
    private static readonly DateTime _taken = new(2026, 10, 11, 9, 30, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-snap-" + Guid.NewGuid().ToString("N")[..8]);

    public MemorySnapshotTests() => Directory.CreateDirectory(Path.Combine(_root, SnapshotAnswers.Folder));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Reads_metadata_totals_and_native_objects()
    {
        string path = Write("a.snap", new Snap());

        MemorySnapshot snapshot = MemorySnapshot.Read(path);

        Assert.Equal(17u, snapshot.FormatVersion);
        Assert.Equal(_taken, snapshot.RecordDate);
        Assert.Equal(0xC0FFEEu, snapshot.SessionGuid);
        Assert.Equal("Purge Squad", snapshot.Product);
        Assert.Equal("6000.3.14f1", snapshot.UnityVersion);
        Assert.Equal("WindowsEditor", snapshot.Platform);
        Assert.Equal(new SnapshotStats(400_000_000, 600_000_000, 50_000_000, 1_000_000, 30_000_000, 40_000_000, 2_000_000), snapshot.Stats);
        Assert.Equal(4096ul, snapshot.ManagedHeapBytes);
        Assert.Equal(
            new[]
            {
                new SnapshotObject("Texture2D", "Hero", 1200, 4_194_304),
                new SnapshotObject("Mesh", "Rock", 1300, 65_536),
                new SnapshotObject("Texture2D", "", 1400, 1024),
            },
            snapshot.Objects);
    }

    [Fact]
    public void Reads_8_byte_instance_ids_of_format_18()
    {
        string path = Write("b.snap", new Snap { Version = 18, WideIds = true, Objects = [("Mesh", "Rock", -5_000_000_000, 100)] });

        Assert.Equal(new[] { new SnapshotObject("Mesh", "Rock", -5_000_000_000, 100) }, MemorySnapshot.Read(path).Objects);
    }

    [Theory]
    [InlineData("not a memory snapshot (no snapshot signature at the start)", "junk")]
    [InlineData("not a complete memory snapshot (no end signature: still being written, or cut short)", "cut")]
    [InlineData("format version 19 is newer than ug reads (up to 18)", "newer")]
    [InlineData("damaged memory snapshot (bad directory signature)", "directory")]
    public void Refuses_files_it_cannot_read(string message, string damage)
    {
        byte[] bytes = new Snap { Version = damage == "newer" ? 19u : 17u }.Bytes();
        if (damage == "junk")
        {
            bytes = Encoding.UTF8.GetBytes("this is not a snapshot, only some text that is long enough");
        }
        else if (damage == "cut")
        {
            bytes = bytes[..^7];
        }
        else if (damage == "directory")
        {
            long directory = BitConverter.ToInt64(bytes, bytes.Length - 12);
            bytes[directory] ^= 0xFF;
        }

        string path = Path.Combine(_root, "bad.snap");
        File.WriteAllBytes(path, bytes);

        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => MemorySnapshot.Read(path)).Message);
    }

    [Fact]
    public void List_shows_each_snapshot_newest_first_and_says_why_one_is_unreadable()
    {
        string old = Write("old.snap", new Snap());
        File.SetLastWriteTimeUtc(old, _taken.AddMinutes(-5));
        string broken = Path.Combine(_root, SnapshotAnswers.Folder, "broken.snap");
        File.WriteAllText(broken, "nothing here, nothing at all, not one byte of it");
        File.SetLastWriteTimeUtc(broken, _taken.AddMinutes(5));
        Directory.CreateDirectory(Path.Combine(_root, SnapshotAnswers.PackageFolder));
        string package = Path.Combine(_root, SnapshotAnswers.PackageFolder, "pkg.snap");
        File.WriteAllBytes(package, new Snap { Objects = [("Mesh", "Rock", 1, 10)] }.Bytes());
        File.SetLastWriteTimeUtc(package, _taken);
        string when = Local(_taken);

        Assert.Equal(
            "Library/ungrep/snapshots/broken.snap  48 B  not a memory snapshot (no snapshot signature at the start)\n"
            + "MemoryCaptures/pkg.snap  " + Size(package) + "  " + when + "  Purge Squad  6000.3.14f1  WindowsEditor  used 381.5 MB, 1 native objects\n"
            + "Library/ungrep/snapshots/old.snap  " + Size(old) + "  " + when + "  Purge Squad  6000.3.14f1  WindowsEditor  used 381.5 MB, 3 native objects\n",
            SnapshotAnswers.List(_root, null, all: false));
    }

    [Fact]
    public void List_says_where_it_looked()
    {
        Assert.Equal("no snapshots in Library/ungrep/snapshots/ or MemoryCaptures/: ug perf snapshot take\n", SnapshotAnswers.List(_root, null, all: false));
        Assert.Equal("no folder Captures\n", SnapshotAnswers.List(_root, "Captures", all: false));
    }

    [Fact]
    public void Resolve_takes_a_path_or_a_bare_name()
    {
        string path = Path.GetFullPath(Write("a.snap", new Snap()));

        Assert.Equal(path, SnapshotAnswers.Resolve(_root, "a"));
        Assert.Equal(path, SnapshotAnswers.Resolve(_root, "a.snap"));
        Assert.Equal(path, SnapshotAnswers.Resolve(_root, "Library/ungrep/snapshots/a.snap"));
        Assert.Null(SnapshotAnswers.Resolve(_root, "b"));
        Assert.Null(SnapshotAnswers.Resolve(_root, "Library/b.snap"));
    }

    [Fact]
    public void Compare_in_one_session_matches_objects_by_instance_id()
    {
        string a = Write("a.snap", new Snap());
        string b = Write("b.snap", new Snap
        {
            Taken = _taken.AddMinutes(1),
            Used = 410_000_000,
            Objects = [("Texture2D", "Hero", 1200, 8_388_608), ("Texture2D", "", 1400, 1024), ("AudioClip", "Theme", 1500, 2_000_000)],
        });

        string text = SnapshotAnswers.Compare(_root, a, b, 10);

        Assert.Equal(
            "a  Library/ungrep/snapshots/a.snap  " + Local(_taken) + "  Purge Squad  6000.3.14f1  WindowsEditor\n"
            + "b  Library/ungrep/snapshots/b.snap  " + Local(_taken.AddMinutes(1)) + "  same session (objects matched by instance ID)\n"
            + "memory  total used 381.5 MB -> 391.0 MB (+9.5 MB), reserved 572.2 MB -> 572.2 MB, graphics 47.7 MB -> 47.7 MB, audio 976.6 KB -> 976.6 KB, "
            + "GC heap used 28.6 MB -> 28.6 MB, GC heap reserved 38.1 MB -> 38.1 MB, profiler 1.9 MB -> 1.9 MB\n"
            + "managed heap sections  4.0 KB -> 4.0 KB\n"
            + "native objects  3 -> 3 (+0), 4.1 MB -> 9.9 MB (+5.8 MB)\n"
            + "types by size change:\n"
            + "  Texture2D  +4.0 MB  2 -> 2\n"
            + "  AudioClip  +1.9 MB  0 -> 1\n"
            + "  Mesh  -64.0 KB  1 -> 0\n"
            + "new in b: 1 objects, 1.9 MB; largest:\n"
            + "  1.9 MB  AudioClip  Theme\n"
            + "gone from a: 1 objects, 64.0 KB; largest:\n"
            + "  64.0 KB  Mesh  Rock\n"
            + "changed size: 1 objects, +4.0 MB; largest change:\n"
            + "  +4.0 MB  Texture2D  Hero  4.0 MB -> 8.0 MB\n"
            + "managed objects are not compared: crawling the managed heap is the Memory Profiler package's work\n",
            text);
    }

    [Fact]
    public void Compare_across_sessions_matches_by_type_and_name_and_caps_each_list()
    {
        string a = Write("a.snap", new Snap { Objects = [("Mesh", "Rock", 1, 10), ("Mesh", "Tree", 2, 20)] });
        string b = Write("b.snap", new Snap
        {
            Session = 7,
            Objects = [("Mesh", "Rock", 91, 10), ("Mesh", "Tree", 92, 30), ("Mesh", "A", 93, 3), ("Mesh", "B", 94, 2), ("Mesh", "", 95, 1)],
        });

        string text = SnapshotAnswers.Compare(_root, a, b, 1);

        Assert.Contains("  (objects matched by type and name)\n", text, StringComparison.Ordinal);
        Assert.Contains(
            "new in b: 3 objects, 6 B; largest:\n  3 B  Mesh  A\n  ... 2 more: ug perf snapshot compare Library/ungrep/snapshots/a.snap Library/ungrep/snapshots/b.snap --top 3\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("gone from a: none\nchanged size: 1 objects, +10 B; largest change:\n  +10 B  Mesh  Tree  20 B -> 30 B\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_names_an_object_without_a_name_by_its_id()
    {
        string a = Write("a.snap", new Snap { Objects = [] });
        string b = Write("b.snap", new Snap { Objects = [("Shader", "", 77, 5)] });

        Assert.Contains("  5 B  Shader  (no name, id 77)\n", SnapshotAnswers.Compare(_root, a, b, 10), StringComparison.Ordinal);
    }

    private string Write(string name, Snap snap)
    {
        string path = Path.Combine(_root, SnapshotAnswers.Folder, name);
        File.WriteAllBytes(path, snap.Bytes());
        return path;
    }

    private static string Local(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Size(string path) => SnapshotAnswers.Size((ulong)new FileInfo(path).Length);

    /// <summary>
    /// A snapshot in Unity's file layout: header signature; chunks; block headers; a block
    /// section; entry headers; the directory; its offset and the footer signature.
    /// </summary>
    private sealed class Snap
    {
        public uint Version { get; init; } = 17;

        public bool WideIds { get; init; }

        public DateTime Taken { get; init; } = _taken;

        public uint Session { get; init; } = 0xC0FFEE;

        public ulong Used { get; init; } = 400_000_000;

        public (string Type, string Name, long Id, long Size)[] Objects { get; init; } =
            [("Texture2D", "Hero", 1200, 4_194_304), ("Mesh", "Rock", 1300, 65_536), ("Texture2D", "", 1400, 1024)];

        public byte[] Bytes()
        {
            // Block 0 holds the single entries and the sized-element arrays; each fixed-size
            // array has a block of its own, as Unity writes them.
            var shared = new List<byte>();
            var entries = new Dictionary<int, (ushort Format, uint Block, uint EntriesMeta, ulong HeaderMeta, long[] Ends)>();

            Single(0, BitConverter.GetBytes(Version));
            Single(1, BitConverter.GetBytes(Taken.Ticks));
            string[] types = Objects.Select(o => o.Type).Distinct().ToArray();
            Strings(5, types);
            Strings(11, Objects.Select(o => o.Name).ToArray());
            Sized(19, [new byte[1024], new byte[3072]]);
            var info = new byte[328];
            BitConverter.GetBytes(Session).CopyTo(info, 0);
            BitConverter.GetBytes(7).CopyTo(info, 4);
            Text(info, 48, 52, "6000.3.14f1");
            Text(info, 68, 72, "Purge Squad");
            Single(59, info);
            var stats = new byte[72];
            ulong[] words = [Used, 600_000_000, 50_000_000, 1_000_000, 30_000_000, 40_000_000, 2_000_000];
            int[] at = [8, 16, 32, 40, 48, 56, 64];
            for (int i = 0; i < words.Length; i++)
            {
                BitConverter.GetBytes(words[i]).CopyTo(stats, at[i]);
            }

            Single(60, stats);

            var blocks = new List<byte[]> { shared.ToArray() };
            Fixed(7, 4, Objects.Select(o => BitConverter.GetBytes(Array.IndexOf(types, o.Type))));
            Fixed(10, WideIds ? 8u : 4u, Objects.Select(o => WideIds ? BitConverter.GetBytes(o.Id) : BitConverter.GetBytes((int)o.Id)));
            Fixed(13, 8, Objects.Select(o => BitConverter.GetBytes(o.Size)));

            var file = new MemoryStream();
            var writer = new BinaryWriter(file);
            writer.Write(0xAEABCDCDu);

            // Chunks of 16 bytes, written last chunk first so offsets must be followed.
            const int ChunkSize = 16;
            var chunkOffsets = new List<long[]>();
            foreach (byte[] block in blocks)
            {
                int count = (block.Length + ChunkSize - 1) / ChunkSize;
                var offsets = new long[count];
                for (int c = count - 1; c >= 0; c--)
                {
                    offsets[c] = file.Position;
                    writer.Write(block, c * ChunkSize, Math.Min(ChunkSize, block.Length - (c * ChunkSize)));
                    writer.Write(new byte[3]);
                }

                chunkOffsets.Add(offsets);
            }

            var blockHeaders = new long[blocks.Count];
            for (int b = 0; b < blocks.Count; b++)
            {
                blockHeaders[b] = file.Position;
                writer.Write((long)ChunkSize);
                writer.Write((long)blocks[b].Length);
                foreach (long offset in chunkOffsets[b])
                {
                    writer.Write(offset);
                }
            }

            long blockSection = file.Position;
            writer.Write(0x20170724u);
            writer.Write(blocks.Count);
            foreach (long header in blockHeaders)
            {
                writer.Write(header);
            }

            const int EntryCount = 61;
            var entryOffsets = new long[EntryCount];
            foreach ((int type, var e) in entries)
            {
                entryOffsets[type] = file.Position;
                writer.Write(e.Format);
                writer.Write(e.Block);
                writer.Write(e.EntriesMeta);
                writer.Write(e.HeaderMeta);
                foreach (long end in e.Ends)
                {
                    writer.Write(end);
                }
            }

            long directory = file.Position;
            writer.Write(0xCDCDAEABu);
            writer.Write(0x20170724u);
            writer.Write(blockSection);
            writer.Write(EntryCount);
            foreach (long offset in entryOffsets)
            {
                writer.Write(offset);
            }

            writer.Write(directory);
            writer.Write(0xABCDCDAEu);
            return file.ToArray();

            void Single(int type, byte[] data)
            {
                entries[type] = (1, 0, (uint)data.Length, (ulong)shared.Count, []);
                shared.AddRange(data);
            }

            void Strings(int type, string[] values) => Sized(type, values.Select(v => Encoding.UTF8.GetBytes(v)).ToArray());

            // Starts: the header holds the first; the file holds each next start, the last being the end.
            void Sized(int type, byte[][] values)
            {
                long first = shared.Count;
                var ends = new List<long>();
                foreach (byte[] value in values)
                {
                    shared.AddRange(value);
                    ends.Add(shared.Count);
                }

                entries[type] = (3, 0, (uint)values.Length, (ulong)first, [.. ends]);
            }

            void Fixed(int type, uint size, IEnumerable<byte[]> values)
            {
                byte[] data = values.SelectMany(v => v).ToArray();
                entries[type] = (2, (uint)blocks.Count, size, (ulong)(data.Length / size), []);
                blocks.Add(data.Length == 0 ? new byte[1] : data);
            }

            static void Text(byte[] buffer, int lengthAt, int textAt, string text)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                BitConverter.GetBytes((uint)bytes.Length).CopyTo(buffer, lengthAt);
                bytes.CopyTo(buffer, textAt);
            }
        }
    }
}
