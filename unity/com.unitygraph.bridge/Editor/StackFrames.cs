using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// Finds the first stack frame in the project's own code: a file under `Assets/` or an
    /// embedded package (`Packages/<name>/` inside the project).
    /// </summary>
    internal static class StackFrames
    {
        // Unity log traces: `Game.Foo:Bar () (at Assets/Scripts/Foo.cs:12)`.
        // Exception traces: `at Game.Foo.Bar () [0x00001] in D:\Proj\Assets\Scripts\Foo.cs:12`.
        private static readonly Regex _frame = new Regex(@"(?:\(at | in )(?<file>.+?):(?<line>\d+)\)?\s*$", RegexOptions.Compiled);

        // The file names evals were compiled under (`snippet`, `Tools/Build.cs`); their frames are own.
        // Locked: logs arrive on any thread.
        private static readonly HashSet<string> _evalSources = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Frames in <paramref name="source"/>, the name an eval's code was compiled under, count as own from now on.</summary>
        public static void AddEvalSource(string source)
        {
            lock (_evalSources)
            {
                _evalSources.Add(source);
            }
        }

        /// <summary>`Assets/Scripts/Foo.cs:12` (or `snippet:3` in an eval's code), or null when no frame is the project's own.</summary>
        public static string OwnFrame(string stackTrace, string projectRoot)
        {
            if (string.IsNullOrEmpty(stackTrace))
            {
                return null;
            }

            foreach (string line in stackTrace.Split('\n'))
            {
                Match match = _frame.Match(line.TrimEnd('\r'));
                if (match.Success && IsEvalSource(match.Groups["file"].Value))
                {
                    return match.Groups["file"].Value + ":" + match.Groups["line"].Value;
                }

                if (match.Success && Relative(match.Groups["file"].Value, projectRoot) is string file && IsOwn(file, projectRoot))
                {
                    return file + ":" + match.Groups["line"].Value;
                }
            }

            return null;
        }

        private static bool IsEvalSource(string file)
        {
            lock (_evalSources)
            {
                return _evalSources.Contains(file);
            }
        }

        /// <summary>A project-relative path with `/`, or null when the path is outside the project.</summary>
        public static string Relative(string path, string projectRoot)
        {
            // Mono frames without symbols name the module instead: `in <41d29b35...>:0`.
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOf('<') >= 0)
            {
                return null;
            }

            string normal = path.Replace('\\', '/');
            if (!Path.IsPathRooted(normal))
            {
                return normal.StartsWith("./", StringComparison.Ordinal) ? normal.Substring(2) : normal;
            }

            string root = projectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            return normal.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? normal.Substring(root.Length) : null;
        }

        public static bool IsOwn(string relativePath, string projectRoot)
        {
            if (relativePath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return true;
            }

            if (!relativePath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return false;
            }

            int end = relativePath.IndexOf('/', "Packages/".Length);
            return end > 0 && Directory.Exists(Path.Combine(projectRoot, relativePath.Substring(0, end)));
        }
    }
}
