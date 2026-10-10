using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// `/eval/refs` and `/eval` (SPEC-editor-link, Running code): the files ug compiles a snippet
    /// against, and running the assembly ug compiled: loaded from memory with its PDB, its entry
    /// bound as `ug run` binds a method, run through <see cref="CodeRun"/>.
    /// </summary>
    internal static class EvalRunner
    {
        /// <summary>The name prefix of the assemblies ug compiles; they are not references.</summary>
        public const string AssemblyPrefix = "ug-eval-";

        // The C# version Unity compiles editor scripts with.
        private const string Language =
#if UNITY_2021_2_OR_NEWER
            "9.0";
#elif UNITY_2020_2_OR_NEWER
            "8.0";
#else
            "7.3";
#endif

        private static int _listed = -1;
        private static Reply _refs;

        /// <summary>
        /// `{"refs": [...], "defines": [...], "lang": "9.0"}`: the file of each loaded assembly, one per
        /// name, and the runtime's `Facades`; listed again when more assemblies have loaded.
        /// </summary>
        public static Reply Refs()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            int count = assemblies.Count(a => !a.IsDynamic && !a.FullName.StartsWith(AssemblyPrefix, StringComparison.Ordinal));
            if (_refs != null && count == _listed)
            {
                return _refs;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<string>();
            foreach (Assembly assembly in assemblies)
            {
                string location = assembly.IsDynamic ? null : Location(assembly);
                if (!string.IsNullOrEmpty(location) && names.Add(assembly.GetName().Name))
                {
                    files.Add(location);
                }
            }

            string facades = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location) ?? "", "Facades");
            if (Directory.Exists(facades))
            {
                foreach (string file in Directory.GetFiles(facades, "*.dll").OrderBy(f => f, StringComparer.Ordinal))
                {
                    if (names.Add(Path.GetFileNameWithoutExtension(file)))
                    {
                        files.Add(file);
                    }
                }
            }

            var json = new Json().Object().Key("refs").Array();
            foreach (string file in files)
            {
                json.Value(file.Replace('\\', '/'));
            }

            json.EndArray().Key("defines").Array();
            foreach (string define in EditorUserBuildSettings.activeScriptCompilationDefines)
            {
                json.Value(define);
            }

            _refs = Reply.Ok(json.EndArray().Field("lang", Language).EndObject().ToString());
            _listed = count;
            return _refs;
        }

        public static Func<Reply> Run(Request request, ConsoleBuffer console)
        {
            string source = request.Get("source") ?? "snippet";
            int.TryParse(request.Get("n"), NumberStyles.None, CultureInfo.InvariantCulture, out int n);
            var args = new List<string>();
            for (int i = 0; i < n; i++)
            {
                args.Add(request.Get("a" + i.ToString(CultureInfo.InvariantCulture)) ?? "");
            }

            double timeout = double.TryParse(request.Get("timeout"), NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && t > 0 ? t : CodeRun.DefaultTimeout;
            CodeRun.Call call = null;
            string problem = Load(request.Body, out Assembly assembly);
            if (problem == null)
            {
                problem = Prepare(assembly, source, request.Get("what"), request.Get("entry") ?? "", args, timeout, out call);
            }

            if (problem != null)
            {
                Reply refused = Answer.Error("did not run " + source + ": " + problem);
                return () => refused;
            }

            return CodeRun.Start(call, console);
        }

        /// <summary>The body's assembly (`<4-byte little-endian length><assembly><pdb>`) loaded from memory, or why not.</summary>
        public static string Load(byte[] body, out Assembly assembly)
        {
            assembly = null;
            if (!Split(body, out byte[] image, out byte[] symbols))
            {
                return "no assembly in the request (ug eval sends one)";
            }

            try
            {
                assembly = symbols.Length > 0 ? Assembly.Load(image, symbols) : Assembly.Load(image);
                return null;
            }
            catch (BadImageFormatException e)
            {
                return "not an assembly: " + e.Message;
            }
        }

        public static bool Split(byte[] body, out byte[] image, out byte[] symbols)
        {
            image = null;
            symbols = null;
            if (body == null || body.Length < 4)
            {
                return false;
            }

            int length = body[0] | body[1] << 8 | body[2] << 16 | body[3] << 24;
            if (length <= 0 || length > body.Length - 4)
            {
                return false;
            }

            image = new byte[length];
            Buffer.BlockCopy(body, 4, image, 0, length);
            symbols = new byte[body.Length - 4 - length];
            Buffer.BlockCopy(body, 4 + length, symbols, 0, symbols.Length);
            return true;
        }

        /// <summary>
        /// The call to <paramref name="entry"/> (`Ns.Type+Nested.Method`) with the arguments bound,
        /// named <paramref name="what"/> or else its signature, its frames named <paramref name="source"/>.
        /// </summary>
        public static string Prepare(Assembly assembly, string source, string what, string entry, List<string> args, double timeout, out CodeRun.Call call)
        {
            call = null;
            int dot = entry.LastIndexOf('.');
            if (dot <= 0 || dot == entry.Length - 1)
            {
                return "entry must be Type.Method";
            }

            string typeName = entry.Substring(0, dot);
            string methodName = entry.Substring(dot + 1);
            Type type = assembly.GetType(typeName, false);
            if (type == null)
            {
                return "no type " + typeName + " in the compiled code";
            }

            List<MethodInfo> candidates = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == methodName && !m.IsGenericMethodDefinition).ToList();
            if (candidates.Count == 0)
            {
                return "no static method " + methodName + " on " + typeName.Replace('+', '.');
            }

            string problem = MethodRunner.Choose(candidates, args, timeout, out call);
            if (call != null)
            {
                call.Snippet = source;
                call.What = string.IsNullOrEmpty(what) ? call.What : what;
            }

            return problem;
        }

        private static string Location(Assembly assembly)
        {
            try
            {
                return assembly.Location;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }
    }
}
