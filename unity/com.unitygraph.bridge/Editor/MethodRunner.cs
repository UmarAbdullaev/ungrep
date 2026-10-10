using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// `/run` (SPEC-editor-link, Running code): a static method of the project's own assemblies with
    /// arguments read by `ug set`'s grammar, the overload whose arguments fit best, or a menu item
    /// when the target contains `/`. `--list` lists what can be run. Unity's and .NET's own methods
    /// are left to `ug eval`.
    /// </summary>
    internal static class MethodRunner
    {
        private const int MaxListed = 40;
        private const int MaxOverloads = 10;
        private const int MaxNames = 10;

        private static readonly Regex _named = new Regex(@"^([A-Za-z_]\w*)=(.*)$", RegexOptions.Singleline);

        /// <summary>Which assemblies are the project's own; tests replace it.</summary>
        internal static Func<Assembly, bool> IsOwn = DefaultIsOwn;

        private sealed class Binding
        {
            public MethodInfo Method;
            public object[] Values;
            public int Rank;
            public int Defaults;
            public bool Expanded;
            public string Problem;
        }

        public static Func<Reply> Run(Request request, ConsoleBuffer console)
        {
            if (request.Get("list") != null)
            {
                string filter = (request.Get("list") ?? "").Trim();
                Reply listed = request.Get("menu") != null ? Menus(filter) : List(filter);
                return () => listed;
            }

            string target = (request.Get("target") ?? "").Trim();
            int.TryParse(request.Get("n"), NumberStyles.None, CultureInfo.InvariantCulture, out int n);
            var args = new List<string>();
            for (int i = 0; i < n; i++)
            {
                args.Add(request.Get("a" + i.ToString(CultureInfo.InvariantCulture)) ?? "");
            }

            double timeout = double.TryParse(request.Get("timeout"), NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && t > 0 ? t : CodeRun.DefaultTimeout;
            if (target.Length == 0)
            {
                Reply none = Answer.Error("give a target: ug run Type.Method [args], ug run \"Menu/Path\", ug run --list [filter]");
                return () => none;
            }

            string problem = Prepare(target, args, timeout, out CodeRun.Call call);
            Reply refused = problem == null ? null : Answer.Error("did not run " + target + ": " + problem);
            return refused != null ? () => refused : CodeRun.Start(call, console);
        }

        /// <summary>The call a target and its arguments make, or why there is none.</summary>
        public static string Prepare(string target, List<string> args, double timeout, out CodeRun.Call call)
        {
            call = null;
            if (target.Contains("/"))
            {
                return Menu(target, args, timeout, out call);
            }

            int dot = target.LastIndexOf('.');
            if (dot <= 0 || dot == target.Length - 1)
            {
                return "target must be Type.Method or a menu path";
            }

            string typeName = target.Substring(0, dot);
            string methodName = target.Substring(dot + 1);
            List<Type> types = OwnTypes().Where(type => Matches(type, typeName)).ToList();
            if (types.Count == 0)
            {
                return NotOwn(typeName, methodName);
            }

            if (types.Count > 1)
            {
                return "ambiguous " + typeName + ": " + string.Join(", ", types.Select(DisplayName).OrderBy(x => x, StringComparer.Ordinal));
            }

            Type type = types[0];
            List<MethodInfo> candidates = Runnable(type).Where(m => m.Name == methodName).ToList();
            if (candidates.Count == 0)
            {
                return NoMethod(type, methodName);
            }

            List<Binding> bindings = candidates.Select(m => Bind(m, args)).ToList();
            List<Binding> fits = bindings.Where(b => b.Problem == null).OrderBy(b => b.Rank).ThenBy(b => b.Defaults).ThenBy(b => b.Expanded).ToList();
            if (fits.Count == 0)
            {
                return NoFit(bindings);
            }

            if (fits.Count > 1 && Score(fits[0]) == Score(fits[1]))
            {
                var lines = new List<string> { "ambiguous call: these overloads fit equally; name the arguments (param=value) or write them more exactly" };
                lines.AddRange(fits.Where(f => Score(f) == Score(fits[0])).Take(MaxOverloads).Select(f => "  " + ApiOps.Signature(f.Method)));
                return string.Join("\n", lines);
            }

            Binding chosen = fits[0];
            ParameterInfo[] parameters = chosen.Method.GetParameters();
            string what = DisplayName(type) + "." + methodName + "(" + string.Join(", ", parameters.Select(p => (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + ApiOps.TypeName(p.ParameterType) + " " + p.Name)) + ")";
            call = new CodeRun.Call
            {
                What = what,
                UndoName = "run " + DisplayName(type) + "." + methodName,
                Invoke = () => chosen.Method.Invoke(null, chosen.Values),
                HasResult = chosen.Method.ReturnType != typeof(void),
                Outs = () => parameters.Where(p => p.ParameterType.IsByRef).Select(p => new KeyValuePair<string, object>(p.Name, chosen.Values[p.Position])),
                Timeout = timeout,
            };
            return null;
        }

        /// <summary>Why a menu item is never run (it quits the editor or builds a player), or null.</summary>
        public static string RefusedMenu(string menuPath)
        {
            string path = string.Join("/", menuPath.Split('/').Select(part => part.Trim()));
            if (path.Equals("File/Quit", StringComparison.OrdinalIgnoreCase) || path.Equals("File/Exit", StringComparison.OrdinalIgnoreCase))
            {
                return "refused, it would close the editor (ug does not quit Unity)";
            }

            return path.StartsWith("File/Build", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Build/", StringComparison.OrdinalIgnoreCase)
                ? "refused, it builds a player (ug does not build; use Unity's Build Profiles window)"
                : null;
        }

        /// <summary>Whether an assembly is Unity's, the runtime's or a test framework's, which are never run.</summary>
        public static bool IsFramework(string assemblyName) =>
            assemblyName == "mscorlib" || assemblyName == "netstandard" || assemblyName.StartsWith("System", StringComparison.Ordinal)
            || assemblyName.StartsWith("Mono.", StringComparison.Ordinal) || assemblyName.StartsWith("UnityEngine", StringComparison.Ordinal)
            || assemblyName.StartsWith("UnityEditor", StringComparison.Ordinal) || assemblyName.StartsWith("Unity.", StringComparison.Ordinal)
            || assemblyName.StartsWith("nunit", StringComparison.OrdinalIgnoreCase) || assemblyName.StartsWith("UnityGraph.Bridge", StringComparison.Ordinal);

        // ---- binding arguments to one overload ----

        private static (int, int, bool) Score(Binding b) => (b.Rank, b.Defaults, b.Expanded);

        private static Binding Bind(MethodInfo method, List<string> args)
        {
            ParameterInfo[] parameters = method.GetParameters();
            var binding = new Binding { Method = method, Values = new object[parameters.Length] };
            var given = new bool[parameters.Length];
            List<int> inputs = Enumerable.Range(0, parameters.Length).Where(i => !parameters[i].IsOut).ToList();
            int paramsIndex = parameters.Length > 0 && parameters[parameters.Length - 1].IsDefined(typeof(ParamArrayAttribute), false) ? parameters.Length - 1 : -1;
            int position = 0;
            bool named = false;
            for (int a = 0; a < args.Count; a++)
            {
                if (Named(args[a], parameters, out int index, out string text))
                {
                    named = true;
                    if (given[index])
                    {
                        return Fail(binding, parameters[index].Name + " is given twice");
                    }

                    if (!Read(binding, parameters[index], text, given))
                    {
                        return binding;
                    }

                    continue;
                }

                if (named)
                {
                    return Fail(binding, "a positional argument after a named one: " + args[a]);
                }

                if (position >= inputs.Count)
                {
                    return Fail(binding, "takes " + Answer.Count(inputs.Count, "argument", "arguments") + ", given " + args.Count.ToString(CultureInfo.InvariantCulture));
                }

                int next = inputs[position++];
                if (next == paramsIndex)
                {
                    var rest = new List<string>();
                    for (; a < args.Count && !Named(args[a], parameters, out _, out _); a++)
                    {
                        rest.Add(Unescape(args[a]));
                    }

                    a--;
                    if (!ReadParams(binding, parameters[next], rest, given))
                    {
                        return binding;
                    }

                    continue;
                }

                if (!Read(binding, parameters[next], Unescape(args[a]), given))
                {
                    return binding;
                }
            }

            foreach (int i in inputs.Where(i => !given[i]))
            {
                ParameterInfo parameter = parameters[i];
                if (i == paramsIndex)
                {
                    binding.Values[i] = Array.CreateInstance(parameter.ParameterType.GetElementType(), 0);
                }
                else if (parameter.HasDefaultValue)
                {
                    binding.Values[i] = parameter.DefaultValue == null || parameter.DefaultValue == DBNull.Value || parameter.DefaultValue == Type.Missing
                        ? Default(parameter.ParameterType)
                        : parameter.DefaultValue;
                    binding.Defaults++;
                }
                else
                {
                    return Fail(binding, "missing " + parameter.Name + " (" + ApiOps.TypeName(parameter.ParameterType) + ")");
                }
            }

            return binding;
        }

        private static bool Read(Binding binding, ParameterInfo parameter, string text, bool[] given)
        {
            string problem = Values.ParseAs(parameter.ParameterType, text, out object value, out int rank);
            if (problem != null)
            {
                Fail(binding, parameter.Name + ": " + problem);
                return false;
            }

            binding.Values[parameter.Position] = value;
            binding.Rank += rank;
            given[parameter.Position] = true;
            return true;
        }

        // `params T[]`: one `[a, b]` list, or the remaining arguments one by one.
        private static bool ReadParams(Binding binding, ParameterInfo parameter, List<string> items, bool[] given)
        {
            Type element = parameter.ParameterType.GetElementType();
            string single = items.Count == 1 ? items[0].Trim() : null;
            if (single != null && (single.StartsWith("[", StringComparison.Ordinal) || single == "empty"))
            {
                return Read(binding, parameter, single, given);
            }

            binding.Expanded = true;
            string problem = Values.FillList(parameter.ParameterType, element, items, out object value, out int rank);
            if (problem != null)
            {
                Fail(binding, parameter.Name + problem);
                return false;
            }

            binding.Values[parameter.Position] = value;
            binding.Rank += rank;
            given[parameter.Position] = true;
            return true;
        }

        // `name=value` where name is one of the method's parameters; `\name=value` is a positional text.
        private static bool Named(string arg, ParameterInfo[] parameters, out int index, out string text)
        {
            index = -1;
            text = null;
            Match match = _named.Match(arg);
            if (!match.Success)
            {
                return false;
            }

            ParameterInfo parameter = parameters.FirstOrDefault(p => !p.IsOut && p.Name == match.Groups[1].Value);
            if (parameter == null)
            {
                return false;
            }

            index = parameter.Position;
            text = match.Groups[2].Value;
            return true;
        }

        private static string Unescape(string arg) => arg.StartsWith("\\", StringComparison.Ordinal) ? arg.Substring(1) : arg;

        private static Binding Fail(Binding binding, string problem)
        {
            binding.Problem = problem;
            return binding;
        }

        private static object Default(Type type) => type.IsValueType && !type.IsByRef ? Activator.CreateInstance(type) : null;

        private static string NoFit(List<Binding> bindings)
        {
            if (bindings.Count == 1)
            {
                return bindings[0].Problem + "\n  " + ApiOps.Signature(bindings[0].Method);
            }

            MethodInfo first = bindings[0].Method;
            string name = DisplayName(first.DeclaringType) + "." + first.Name;

            var lines = new List<string> { "no overload takes these arguments" };
            lines.AddRange(bindings.Take(MaxOverloads).Select(b => "  " + ApiOps.Signature(b.Method) + "  (" + b.Problem + ")"));
            if (bindings.Count > MaxOverloads)
            {
                lines.Add("  +" + (bindings.Count - MaxOverloads).ToString(CultureInfo.InvariantCulture) + " more overloads: ug run --list " + name);
            }

            return string.Join("\n", lines);
        }

        // ---- menu items ----

        private static string Menu(string path, List<string> args, double timeout, out CodeRun.Call call)
        {
            call = null;
            string refused = RefusedMenu(path);
            if (refused != null)
            {
                return refused;
            }

            if (args.Count > 0)
            {
                return "a menu item takes no arguments";
            }

            call = new CodeRun.Call
            {
                What = path,
                UndoName = "run " + path,
                Invoke = () => EditorApplication.ExecuteMenuItem(path) ? null : new CodeRun.Refusal { Message = "did not run " + path + ": no menu item " + path + " (ug run --list --menu lists them)" },
                Timeout = timeout,
            };
            return null;
        }

        private static Reply Menus(string filter)
        {
            var items = new List<string>();
            foreach (MethodInfo method in TypeCache.GetMethodsWithAttribute<MenuItem>())
            {
                foreach (MenuItem item in method.GetCustomAttributes(typeof(MenuItem), false).Cast<MenuItem>())
                {
                    if (!item.validate && Contains(item.menuItem, filter) && RefusedMenu(item.menuItem) == null)
                    {
                        items.Add(item.menuItem + "  " + DisplayName(method.DeclaringType) + "." + method.Name);
                    }
                }
            }

            items = items.Distinct().OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();
            if (items.Count == 0)
            {
                return Answer.Line(filter.Length == 0 ? "no [MenuItem] methods" : "no menu item contains " + filter);
            }

            return Answer.Lines(Capped(items, items.Count + " menu items" + (filter.Length == 0 ? "" : " with " + filter)));
        }

        // ---- listing what can be run ----

        private static Reply List(string filter)
        {
            List<Type> types = OwnTypes().Where(t => Runnable(t).Any()).OrderBy(DisplayName, StringComparer.Ordinal).ToList();
            if (filter.Length == 0)
            {
                var counts = types.Select(t => DisplayName(t) + "  " + Answer.Count(Runnable(t).Count(), "static method", "static methods")).ToList();
                return Answer.Lines(Capped(counts, Answer.Count(types.Count, "type", "types") + " with static methods; ug run --list <Type.> lists one"));
            }

            bool typeOnly = filter.EndsWith(".", StringComparison.Ordinal);
            string typeFilter = filter.TrimEnd('.');
            var lines = new List<string>();
            int methods = 0;
            foreach (Type type in types)
            {
                List<MethodInfo> found = Runnable(type)
                    .Where(m => typeOnly ? Matches(type, typeFilter) : Contains(DisplayName(type) + "." + m.Name, filter))
                    .OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
                if (found.Count == 0)
                {
                    continue;
                }

                methods += found.Count;
                lines.Add(DisplayName(type) + "  " + Answer.Count(found.Count, "static method", "static methods"));
                lines.AddRange(found.Select(m => "  " + ApiOps.Signature(m)));
            }

            if (lines.Count == 0)
            {
                return Answer.Line("no static method in the project's assemblies matches " + filter + " (ug find searches the graph)");
            }

            return Answer.Lines(Capped(lines, null));
        }

        private static List<string> Capped(List<string> lines, string head)
        {
            var kept = new List<string>();
            if (head != null)
            {
                kept.Add(head);
            }

            kept.AddRange(lines.Take(MaxListed));
            if (lines.Count > MaxListed)
            {
                kept.Add("(+" + (lines.Count - MaxListed).ToString(CultureInfo.InvariantCulture) + " more: give a longer filter)");
            }

            return kept;
        }

        // ---- the project's types and methods ----

        private static IEnumerable<Type> OwnTypes()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || !IsOwn(assembly))
                {
                    continue;
                }

                Type[] all;
                try
                {
                    all = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    all = e.Types.Where(t => t != null).ToArray();
                }

                foreach (Type type in all)
                {
                    if (!type.IsGenericTypeDefinition && !type.Name.Contains("<"))
                    {
                        yield return type;
                    }
                }
            }
        }

        // Script assemblies (Assets and every package compiled from source) and DLL plugins under Assets/ or an embedded
        // package, minus Unity's, .NET's, test frameworks' and the bridge's. Precompiled DLLs of registry packages
        // (Unity Version Control's thousands of types) are left out.
        private static bool DefaultIsOwn(Assembly assembly)
        {
            if (IsFramework(assembly.GetName().Name))
            {
                return false;
            }

            string location;
            try
            {
                location = assembly.Location;
            }
            catch (NotSupportedException)
            {
                return false;
            }

            if (string.IsNullOrEmpty(location))
            {
                return false;
            }

            return OwnLocation(location, Bridge.ProjectRoot);
        }

        internal static bool OwnLocation(string location, string projectRoot)
        {
            string path = location.Replace('\\', '/');
            string root = projectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            return path.StartsWith(root + "Library/ScriptAssemblies/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + "Assets/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + "Packages/", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<MethodInfo> Runnable(Type type) =>
            type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsGenericMethodDefinition && !m.IsSpecialName && !m.Name.Contains("<"));

        private static bool Matches(Type type, string name)
        {
            string display = DisplayName(type);
            return display == name || type.Name == name || display.EndsWith("." + name, StringComparison.Ordinal);
        }

        private static bool Contains(string text, string filter) =>
            filter.Length == 0 || text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string NoMethod(Type type, string methodName)
        {
            string name = DisplayName(type);
            MethodInfo instance = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(m => m.Name == methodName);
            if (instance != null)
            {
                return "an instance method: ug eval calls it on an object";
            }

            if (type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Any(m => m.Name == methodName && m.IsGenericMethodDefinition))
            {
                return "a generic method: ug eval calls it with its type arguments";
            }

            List<string> names = Runnable(type).Select(m => m.Name).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            return "no static method " + methodName + " on " + name
                + (names.Count == 0 ? "" : " (static methods: " + string.Join(", ", names.Take(MaxNames)) + (names.Count > MaxNames ? ", ..." : "") + ")");
        }

        // A type that is not the project's: say whose it is and how ug eval calls it.
        private static string NotOwn(string typeName, string methodName)
        {
            Type other = ApiOps.Resolve(typeName, out _);
            if (other == null)
            {
                return "no type " + typeName + " in the project's assemblies (ug run --list lists them)";
            }

            string assembly = other.Assembly.GetName().Name;
            string owner = assembly.StartsWith("Unity", StringComparison.Ordinal) ? "Unity's"
                : assembly == "mscorlib" || assembly == "netstandard" || assembly.StartsWith("System", StringComparison.Ordinal) ? ".NET's"
                : assembly + "'s";
            return DisplayName(other) + " is " + owner + ", not the project's: ug eval \"" + DisplayName(other) + "." + methodName + "()\" calls it";
        }

        private static string DisplayName(Type type) => type.FullName?.Replace('+', '.') ?? type.Name;
    }
}
