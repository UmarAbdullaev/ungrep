using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ungrep.Code;

/// <summary>
/// Compiled `ug eval` code kept in `Library/ungrep/eval/cache/` (SPEC-editor-link, Running code),
/// so code run before skips Roslyn: the key is the code, its name and every compile setting, with each
/// reference file's size and time, so a script compile or a new ug compiles again. One file per first
/// byte of the key, written over by the next key that shares it: at most 256 files. Touches no Roslyn
/// type, so a hit never loads the compiler.
/// </summary>
public static class SnippetCache
{
    public const string Folder = "Library/ungrep/eval/cache";

    private const int Format = 1;
    private static readonly byte[] _magic = "UGEC"u8.ToArray();

    /// <summary>The key of <paramref name="code"/> named <paramref name="name"/> compiled with <paramref name="settings"/>.</summary>
    public static byte[] Key(string code, string name, SnippetSettings settings)
    {
        var text = new StringBuilder();
        text.Append(Format.ToString(CultureInfo.InvariantCulture)).Append('\0')
            .Append(typeof(SnippetCache).Module.ModuleVersionId.ToString("N")).Append('\0')
            .Append(name).Append('\0')
            .Append(settings.Entry).Append('\0')
            .Append(settings.GuardSeconds.ToString("R", CultureInfo.InvariantCulture)).Append('\0')
            .Append(settings.LanguageVersion).Append('\0')
            .AppendJoin('\n', settings.Defines).Append('\0');
        foreach (string reference in settings.References)
        {
            var file = new FileInfo(reference);
            if (file.Exists)
            {
                text.Append(reference).Append('\t')
                    .Append(file.Length.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        text.Append('\0').Append(code.ReplaceLineEndings("\n"));
        return SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>The build kept under <paramref name="key"/>, renamed so it loads as a new assembly; null when none.</summary>
    public static SnippetBuild? Load(string root, byte[] key)
    {
        try
        {
            using var stream = new FileStream(Slot(root, key), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (!reader.ReadBytes(_magic.Length).AsSpan().SequenceEqual(_magic) || reader.ReadInt32() != Format || !reader.ReadBytes(key.Length).AsSpan().SequenceEqual(key))
            {
                return null;
            }

            string name = reader.ReadString();
            string entryType = reader.ReadString();
            string entryMethod = reader.ReadString();
            bool statements = reader.ReadBoolean();
            bool hasResult = reader.ReadBoolean();
            byte[] assembly = Read(reader);
            byte[] pdb = Read(reader);
            string fresh = SnippetCompiler.AssemblyPrefix + Guid.NewGuid().ToString("N");
            if (!name.StartsWith(SnippetCompiler.AssemblyPrefix, StringComparison.Ordinal) || name.Length != fresh.Length || Rename(assembly, name, fresh) == 0)
            {
                return null;
            }

            return new SnippetBuild
            {
                Assembly = assembly,
                Pdb = pdb,
                Name = fresh,
                EntryType = entryType,
                EntryMethod = entryMethod,
                Statements = statements,
                HasResult = hasResult,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>Keeps <paramref name="build"/> under <paramref name="key"/>; a build with nothing to run, or a file that cannot be written, is not kept.</summary>
    public static void Save(string root, byte[] key, SnippetBuild build)
    {
        if (!build.Ok || build.Assembly == null || build.Name == null || build.EntryType == null || build.EntryMethod == null)
        {
            return;
        }

        string path = Slot(root, key);
        string temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(_magic);
                writer.Write(Format);
                writer.Write(key);
                writer.Write(build.Name);
                writer.Write(build.EntryType);
                writer.Write(build.EntryMethod);
                writer.Write(build.Statements);
                writer.Write(build.HasResult);
                writer.Write(build.Assembly.Length);
                writer.Write(build.Assembly);
                byte[] pdb = build.Pdb ?? [];
                writer.Write(pdb.Length);
                writer.Write(pdb);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another ug writing the same file, or a read-only Library: compiled again next time.
        }
    }

    // A length and that many bytes; a file cut short is not a build.
    private static byte[] Read(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        byte[] bytes = length >= 0 ? reader.ReadBytes(length) : [];
        return bytes.Length == length ? bytes : throw new EndOfStreamException();
    }

    private static string Slot(string root, byte[] key) =>
        Path.Combine(root, Folder, key[0].ToString("x2", CultureInfo.InvariantCulture) + ".bin");

    // The name is in the metadata's string heap (the assembly's and the module's `.dll`); one of the
    // same length keeps every offset. Answers how many times it was there.
    private static int Rename(byte[] image, string from, string to)
    {
        byte[] old = Encoding.UTF8.GetBytes(from);
        byte[] replacement = Encoding.UTF8.GetBytes(to);
        int count = 0;
        for (int at = image.AsSpan().IndexOf(old); at >= 0;)
        {
            replacement.CopyTo(image, at);
            count++;
            int next = image.AsSpan(at + old.Length).IndexOf(old);
            at = next < 0 ? -1 : at + old.Length + next;
        }

        return count;
    }
}
