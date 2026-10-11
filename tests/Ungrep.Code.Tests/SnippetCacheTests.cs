namespace Ungrep.Code.Tests;

/// <summary>`ug eval`'s cache of compiled code (SPEC-editor-link, Running code).</summary>
public sealed class SnippetCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-snippet-cache-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_kept_build_loads_as_a_new_assembly_that_runs()
    {
        SnippetSettings settings = SnippetCompilerTests.Settings();
        const string code = "var a = 20; a + 1";
        byte[] key = SnippetCache.Key(code, "snippet", settings);
        Assert.Null(SnippetCache.Load(_root, key));

        SnippetBuild built = SnippetCompiler.Compile(code, "snippet", settings);
        SnippetCache.Save(_root, key, built);
        SnippetBuild? kept = SnippetCache.Load(_root, key);
        SnippetBuild? again = SnippetCache.Load(_root, key);

        Assert.NotNull(kept);
        Assert.NotNull(again);
        Assert.Equal((built.EntryType, built.EntryMethod, built.Statements, built.HasResult), (kept.EntryType, kept.EntryMethod, kept.Statements, kept.HasResult));
        Assert.StartsWith(SnippetCompiler.AssemblyPrefix, kept.Name, StringComparison.Ordinal);
        Assert.NotEqual(built.Name, kept.Name);
        Assert.NotEqual(kept.Name, again.Name);
        Assert.Equal(built.Assembly!.Length, kept.Assembly!.Length);
        Assert.Equal(built.Pdb, kept.Pdb);

        // Each loads beside the others: a second assembly of the same name would not load.
        Assert.Equal(21, SnippetCompilerTests.Run(built));
        Assert.Equal(21, SnippetCompilerTests.Run(kept));
        Assert.Equal(21, SnippetCompilerTests.Run(again));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, SnippetCache.Folder)));
    }

    [Fact]
    public void The_key_follows_the_code_its_name_and_every_setting()
    {
        SnippetSettings settings = SnippetCompilerTests.Settings();
        byte[] key = SnippetCache.Key("1 + 1", "snippet", settings);

        Assert.Equal(key, SnippetCache.Key("1 + 1", "snippet", settings));
        Assert.Equal(key, SnippetCache.Key("1 + 1", "snippet", SnippetCompilerTests.Settings()));
        Assert.Equal(SnippetCache.Key("var a = 1;\r\na", "snippet", settings), SnippetCache.Key("var a = 1;\na", "snippet", settings));
        Assert.NotEqual(key, SnippetCache.Key("1 + 2", "snippet", settings));
        Assert.NotEqual(key, SnippetCache.Key("1 + 1", "x.cs", settings));
        Assert.NotEqual(key, SnippetCache.Key("1 + 1", "snippet", settings with { Entry = "T.Run" }));
        Assert.NotEqual(key, SnippetCache.Key("1 + 1", "snippet", settings with { GuardSeconds = 6 }));
        Assert.NotEqual(key, SnippetCache.Key("1 + 1", "snippet", settings with { LanguageVersion = "8.0" }));
        Assert.NotEqual(key, SnippetCache.Key("1 + 1", "snippet", SnippetCompilerTests.Settings("UNITY_EDITOR")));
    }

    [Fact]
    public void A_rewritten_reference_changes_the_key()
    {
        Directory.CreateDirectory(_root);
        string reference = Path.Combine(_root, "Assembly-CSharp.dll");
        File.WriteAllBytes(reference, [1, 2, 3]);
        SnippetSettings settings = new([reference], [], "9.0");
        byte[] key = SnippetCache.Key("1", "snippet", settings);

        File.SetLastWriteTimeUtc(reference, File.GetLastWriteTimeUtc(reference).AddSeconds(5));

        Assert.NotEqual(key, SnippetCache.Key("1", "snippet", settings));
    }

    [Fact]
    public void Only_a_build_that_runs_is_kept_and_only_its_own_key_finds_it()
    {
        SnippetSettings settings = SnippetCompilerTests.Settings();
        byte[] broken = SnippetCache.Key("var a = ;", "snippet", settings);
        SnippetCache.Save(_root, broken, SnippetCompiler.Compile("var a = ;", "snippet", settings));
        byte[] check = SnippetCache.Key("1 + 1", "snippet", settings);
        SnippetCache.Save(_root, check, SnippetCompiler.Compile("1 + 1", "snippet", settings, emit: false));

        Assert.False(Directory.Exists(Path.Combine(_root, SnippetCache.Folder)));

        byte[] key = SnippetCache.Key("2 + 2", "snippet", settings);
        SnippetCache.Save(_root, key, SnippetCompiler.Compile("2 + 2", "snippet", settings));
        byte[] other = (byte[])key.Clone();
        other[^1] ^= 1;

        Assert.Null(SnippetCache.Load(_root, other));
        Assert.NotNull(SnippetCache.Load(_root, key));
    }

    [Fact]
    public void A_damaged_file_is_a_miss()
    {
        SnippetSettings settings = SnippetCompilerTests.Settings();
        byte[] key = SnippetCache.Key("3", "snippet", settings);
        SnippetCache.Save(_root, key, SnippetCompiler.Compile("3", "snippet", settings));
        string file = Directory.GetFiles(Path.Combine(_root, SnippetCache.Folder)).Single();
        byte[] bytes = File.ReadAllBytes(file);
        File.WriteAllBytes(file, bytes[..(bytes.Length / 2)]);

        Assert.Null(SnippetCache.Load(_root, key));
    }
}
