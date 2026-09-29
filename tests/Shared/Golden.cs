using System.Runtime.CompilerServices;

namespace UnityGraph.Tests;

/// <summary>
/// Golden-file assertion: output is compared with a committed file,
/// `tests/&lt;TestProject&gt;/golden/&lt;TestFile&gt;.&lt;Test&gt;.&lt;ext&gt;`. On a mismatch the actual output
/// is written beside it as `.received.&lt;ext&gt;` to diff or copy over; UG_ACCEPT_GOLDEN=1
/// overwrites the golden files instead.
/// A missing golden file fails, so CI can't pass on output nobody looked at.
/// </summary>
public static class Golden
{
    public static void Match(
        string actual,
        string extension = "txt",
        [CallerFilePath] string sourceFile = "",
        [CallerMemberName] string test = "")
    {
        string dir = Path.Combine(RepoRoot(), "tests", TestProject(), "golden");
        // Only the file name of the caller path is used: CI builds may map source paths to /_/.
        string name = Path.GetFileNameWithoutExtension(sourceFile.Replace('\\', '/').Split('/')[^1]) + "." + test;
        string goldenPath = Path.Combine(dir, name + "." + extension);
        string receivedPath = Path.Combine(dir, name + ".received." + extension);
        actual = actual.ReplaceLineEndings("\n");

        if (Environment.GetEnvironmentVariable("UG_ACCEPT_GOLDEN") == "1")
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(goldenPath, actual);
            File.Delete(receivedPath);
            return;
        }

        string? expected = File.Exists(goldenPath) ? File.ReadAllText(goldenPath).ReplaceLineEndings("\n") : null;
        if (expected == actual)
        {
            File.Delete(receivedPath);
            return;
        }

        Directory.CreateDirectory(dir);
        File.WriteAllText(receivedPath, actual);
        Assert.Fail(expected is null
            ? $"No golden file {goldenPath}. Check {receivedPath}, then rename it or rerun with UG_ACCEPT_GOLDEN=1."
            : $"Output differs from {goldenPath} ({FirstDifference(expected, actual)}). Actual output: {receivedPath}");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "unitygraph.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("unitygraph.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The test project folder has the test assembly's name.</summary>
    private static string TestProject() =>
        Path.GetFileNameWithoutExtension(System.Reflection.Assembly.GetEntryAssembly()!.Location);

    private static string FirstDifference(string expected, string actual)
    {
        string[] e = expected.Split('\n');
        string[] a = actual.Split('\n');
        for (int i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            string el = i < e.Length ? e[i] : "<end>";
            string al = i < a.Length ? a[i] : "<end>";
            if (el != al)
            {
                return $"line {i + 1}: expected \"{el}\", got \"{al}\"";
            }
        }

        return "same lines";
    }
}
