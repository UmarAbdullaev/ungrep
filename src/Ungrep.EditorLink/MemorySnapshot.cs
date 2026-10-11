using System.Text;

namespace Ungrep.EditorLink;

/// <summary>One native object in a memory snapshot: its type, name, instance ID and native size.</summary>
public sealed record SnapshotObject(string Type, string Name, long InstanceId, ulong Size);

/// <summary>
/// The memory totals Unity writes into a snapshot (`ProfileTargetMemoryStats`, format 11 and up).
/// </summary>
public sealed record SnapshotStats(
    ulong TotalUsed, ulong TotalReserved, ulong Graphics, ulong Audio, ulong GcHeapUsed, ulong GcHeapReserved, ulong Profiler);

/// <summary>
/// A Unity memory snapshot (`.snap`) as ug reads it without the Memory Profiler package
/// (SPEC-editor-ops, Profiler): metadata, memory totals and the native objects.
/// </summary>
public sealed class MemorySnapshot
{
    /// <summary>The newest format version ug knows (`EntityIDAs8ByteStructs`, Unity 6000.3).</summary>
    public const uint NewestFormat = 18;

    private const uint HeaderSignature = 0xAEABCDCD;
    private const uint DirectorySignature = 0xCDCDAEAB;
    private const uint FooterSignature = 0xABCDCDAE;
    private const uint SectionVersion = 0x20170724;

    // Entry types, in the order of the package's EntryType (com.unity.memoryprofiler 1.1.12).
    private const int MetadataVersion = 0;
    private const int MetadataRecordDate = 1;
    private const int NativeTypesName = 5;
    private const int NativeObjectsTypeIndex = 7;
    private const int NativeObjectsInstanceId = 10;
    private const int NativeObjectsName = 11;
    private const int NativeObjectsSize = 13;
    private const int ManagedHeapSectionsBytes = 19;
    private const int ProfileTargetInfo = 59;
    private const int ProfileTargetMemoryStats = 60;
    private const int KnownEntries = 92;

    private MemorySnapshot()
    {
    }

    public uint FormatVersion { get; private init; }

    public DateTime? RecordDate { get; private init; }

    /// <summary>Equal in two snapshots of the same editor or player session: instance IDs then match.</summary>
    public uint? SessionGuid { get; private init; }

    public string? Product { get; private init; }

    public string? UnityVersion { get; private init; }

    public string? Platform { get; private init; }

    public SnapshotStats? Stats { get; private init; }

    public ulong ManagedHeapBytes { get; private init; }

    public IReadOnlyList<SnapshotObject> Objects { get; private init; } = [];

    /// <summary>Reads a snapshot file; <see cref="InvalidDataException"/> says why one cannot be read.</summary>
    public static MemorySnapshot Read(string path)
    {
        // The stream's own using closes the file when SnapFile refuses it in its constructor.
        using FileStream stream = File.OpenRead(path);
        using var file = new SnapFile(stream);
        uint version = file.Single(MetadataVersion) is { Length: >= 4 } v ? BitConverter.ToUInt32(v, 0) : 0;
        if (version > NewestFormat)
        {
            throw new InvalidDataException("format version " + version + " is newer than ug reads (up to " + NewestFormat + ")");
        }

        byte[]? date = file.Single(MetadataRecordDate);
        long ticks = date is { Length: >= 8 } ? BitConverter.ToInt64(date, 0) : 0;
        byte[]? info = file.Single(ProfileTargetInfo);
        byte[]? stats = file.Single(ProfileTargetMemoryStats);

        string[] typeNames = file.Strings(NativeTypesName);
        int[] typeIndex = file.Integers(NativeObjectsTypeIndex);
        long[] ids = file.Integers64(NativeObjectsInstanceId);
        string[] names = file.Strings(NativeObjectsName);
        long[] sizes = file.Integers64(NativeObjectsSize);
        var objects = new List<SnapshotObject>(sizes.Length);
        for (int i = 0; i < sizes.Length; i++)
        {
            int type = i < typeIndex.Length ? typeIndex[i] : -1;
            objects.Add(new SnapshotObject(
                type >= 0 && type < typeNames.Length ? typeNames[type] : "?",
                i < names.Length ? names[i] : "",
                i < ids.Length ? ids[i] : 0,
                (ulong)sizes[i]));
        }

        return new MemorySnapshot
        {
            FormatVersion = version,
            RecordDate = ticks > 0 && ticks < DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : null,
            SessionGuid = info is { Length: >= 4 } ? BitConverter.ToUInt32(info, 0) : null,
            Platform = info is { Length: >= 8 } ? PlatformName(BitConverter.ToInt32(info, 4)) : null,
            UnityVersion = info is { Length: >= 68 } ? Text(info, 48, 52, 16) : null,
            Product = info is { Length: >= 328 } ? Text(info, 68, 72, 256) : null,
            Stats = stats is { Length: >= 72 } ? new SnapshotStats(
                Word(stats, 8), Word(stats, 16), Word(stats, 32), Word(stats, 40), Word(stats, 48), Word(stats, 56), Word(stats, 64)) : null,
            ManagedHeapBytes = file.DynamicTotal(ManagedHeapSectionsBytes),
            Objects = objects,
        };

        static ulong Word(byte[] data, int at) => BitConverter.ToUInt64(data, at);
    }

    // A length-prefixed text buffer of ProfileTargetInfo.
    private static string Text(byte[] data, int lengthAt, int at, int capacity)
    {
        int length = (int)Math.Min(BitConverter.ToUInt32(data, lengthAt), (uint)capacity);
        return Encoding.UTF8.GetString(data, at, length).TrimEnd('\0');
    }

    // UnityEngine.RuntimePlatform values for the platforms a snapshot usually comes from.
    private static string PlatformName(int value) => value switch
    {
        0 => "OSXEditor",
        1 => "OSXPlayer",
        2 => "WindowsPlayer",
        7 => "WindowsEditor",
        8 => "IPhonePlayer",
        11 => "Android",
        13 => "LinuxPlayer",
        16 => "LinuxEditor",
        17 => "WebGLPlayer",
        18 => "WSAPlayerX86",
        19 => "WSAPlayerX64",
        20 => "WSAPlayerARM",
        25 => "PS4",
        27 => "XboxOne",
        31 => "tvOS",
        32 => "Switch",
        38 => "PS5",
        _ => "platform " + value,
    };

    // The file's directory, blocks and entry headers; reads map block offsets to chunks.
    private sealed class SnapFile : IDisposable
    {
        private readonly FileStream _stream;
        private readonly BinaryReader _reader;
        private readonly (ulong ChunkSize, ulong Total, long[] Chunks)[] _blocks;
        private readonly (ushort Format, uint Block, uint EntriesMeta, ulong HeaderMeta, long[]? Starts)[] _entries;

        public SnapFile(FileStream stream)
        {
            _stream = stream;
            _reader = new BinaryReader(stream);
            try
            {
                if (stream.Length < 32 || Uint(0) != HeaderSignature)
                {
                    throw new InvalidDataException("not a memory snapshot (no snapshot signature at the start)");
                }

                if (Uint(stream.Length - 4) != FooterSignature)
                {
                    throw new InvalidDataException("not a complete memory snapshot (no end signature: still being written, or cut short)");
                }

                long directory = Long(stream.Length - 12);
                Check(directory, 24);
                if (Uint(directory) != DirectorySignature || Uint(directory + 4) != SectionVersion)
                {
                    throw new InvalidDataException("damaged memory snapshot (bad directory signature)");
                }

                long blockSection = Long(directory + 8);
                Check(blockSection, 8);
                if (Uint(blockSection) != SectionVersion)
                {
                    throw new InvalidDataException("damaged memory snapshot (bad block section signature)");
                }

                int blockCount = Int(blockSection + 4);
                if (blockCount < 1 || blockCount > 1 << 20)
                {
                    throw new InvalidDataException("damaged memory snapshot (" + blockCount + " blocks)");
                }

                _blocks = new (ulong, ulong, long[])[blockCount];
                for (int i = 0; i < blockCount; i++)
                {
                    long at = Long(blockSection + 8 + (8L * i));
                    Check(at, 16);
                    ulong chunkSize = (ulong)Long(at);
                    ulong total = (ulong)Long(at + 8);
                    if (chunkSize == 0)
                    {
                        throw new InvalidDataException("damaged memory snapshot (block " + i + " has no chunk size)");
                    }

                    ulong count = (total / chunkSize) + (total % chunkSize != 0 ? 1UL : 0UL);
                    Check(at + 16, (long)count * 8);
                    var chunks = new long[count];
                    for (ulong c = 0; c < count; c++)
                    {
                        chunks[c] = Long(at + 16 + (8 * (long)c));
                    }

                    _blocks[i] = (chunkSize, total, chunks);
                }

                int entryCount = Math.Min(Int(directory + 16), KnownEntries);
                _entries = new (ushort, uint, uint, ulong, long[]?)[KnownEntries];
                for (int i = 0; i < entryCount; i++)
                {
                    long at = Long(directory + 20 + (8L * i));
                    if (at == 0)
                    {
                        continue;
                    }

                    Check(at, 18);
                    _stream.Position = at;
                    ushort format = _reader.ReadUInt16();
                    uint block = _reader.ReadUInt32();
                    uint entriesMeta = _reader.ReadUInt32();
                    ulong headerMeta = _reader.ReadUInt64();
                    long[]? starts = null;
                    if (format == 3)
                    {
                        // Element starts: the header's value, then the file's longs; the last long is the end.
                        Check(at + 18, 8L * entriesMeta);
                        starts = new long[entriesMeta + 1];
                        starts[0] = (long)headerMeta;
                        for (uint e = 0; e < entriesMeta; e++)
                        {
                            starts[e + 1] = _reader.ReadInt64();
                        }
                    }

                    if (format > 3 || (format != 0 && block >= _blocks.Length))
                    {
                        throw new InvalidDataException("damaged memory snapshot (entry " + i + ")");
                    }

                    _entries[i] = (format, block, entriesMeta, headerMeta, starts);
                }
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException("damaged memory snapshot (cut short)");
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            _stream.Dispose();
        }

        /// <summary>A single-element entry's bytes, or null when the snapshot has none.</summary>
        public byte[]? Single(int entry)
        {
            var e = _entries[entry];
            return e.Format == 1 ? Bytes(e.Block, (long)e.HeaderMeta, e.EntriesMeta) : null;
        }

        /// <summary>A fixed-size array of 4-byte integers.</summary>
        public int[] Integers(int entry)
        {
            (byte[] data, int size, int count) = Elements(entry);
            var values = new int[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = size >= 4 ? BitConverter.ToInt32(data, i * size) : 0;
            }

            return values;
        }

        /// <summary>A fixed-size array of 4- or 8-byte integers, widened.</summary>
        public long[] Integers64(int entry)
        {
            (byte[] data, int size, int count) = Elements(entry);
            var values = new long[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = size >= 8 ? BitConverter.ToInt64(data, i * size) : size >= 4 ? BitConverter.ToInt32(data, i * size) : 0;
            }

            return values;
        }

        /// <summary>A sized-element array of UTF-8 strings.</summary>
        public string[] Strings(int entry)
        {
            var e = _entries[entry];
            if (e.Format != 3 || e.Starts == null || e.EntriesMeta == 0)
            {
                return [];
            }

            long first = e.Starts[0];
            long end = e.Starts[^1];
            byte[] data = Bytes(e.Block, first, end - first);
            var values = new string[e.EntriesMeta];
            for (int i = 0; i < values.Length; i++)
            {
                long start = e.Starts[i] - first;
                long length = e.Starts[i + 1] - e.Starts[i];
                if (start < 0 || length < 0 || start + length > data.Length)
                {
                    throw new InvalidDataException("damaged memory snapshot (string " + i + " of entry " + entry + ")");
                }

                values[i] = Encoding.UTF8.GetString(data, (int)start, (int)length);
            }

            return values;
        }

        /// <summary>The bytes of all elements of a sized-element array together.</summary>
        public ulong DynamicTotal(int entry)
        {
            var e = _entries[entry];
            return e.Format == 3 && e.Starts != null ? (ulong)Math.Max(0, e.Starts[^1] - e.Starts[0]) : 0;
        }

        private (byte[] Data, int Size, int Count) Elements(int entry)
        {
            var e = _entries[entry];
            if (e.Format != 2)
            {
                return ([], 0, 0);
            }

            int count = (int)(uint)e.HeaderMeta;
            int size = (int)e.EntriesMeta;
            return (Bytes(e.Block, 0, (long)count * size), size, count);
        }

        // Block-relative bytes, gathered across the chunks that hold them.
        private byte[] Bytes(uint block, long offset, long count)
        {
            (ulong chunkSize, ulong total, long[] chunks) = _blocks[block];
            if (offset < 0 || count < 0 || (ulong)(offset + count) > total || count > int.MaxValue)
            {
                throw new InvalidDataException("damaged memory snapshot (read past block " + block + ")");
            }

            var data = new byte[count];
            long done = 0;
            while (done < count)
            {
                long at = offset + done;
                long chunk = at / (long)chunkSize;
                long within = at % (long)chunkSize;
                long length = Math.Min(count - done, (long)chunkSize - within);
                Check(chunks[chunk] + within, length);
                _stream.Position = chunks[chunk] + within;
                _stream.ReadExactly(data, (int)done, (int)length);
                done += length;
            }

            return data;
        }

        private void Check(long at, long length)
        {
            if (at < 0 || length < 0 || at + length > _stream.Length)
            {
                throw new InvalidDataException("damaged memory snapshot (offset " + at + " past the end)");
            }
        }

        private uint Uint(long at)
        {
            _stream.Position = at;
            return _reader.ReadUInt32();
        }

        private int Int(long at)
        {
            _stream.Position = at;
            return _reader.ReadInt32();
        }

        private long Long(long at)
        {
            _stream.Position = at;
            return _reader.ReadInt64();
        }
    }
}
