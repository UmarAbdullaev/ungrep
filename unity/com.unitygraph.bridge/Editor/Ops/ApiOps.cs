using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// `/api` (SPEC-editor-ops, Endpoints; rule 11): types, members and signatures by reflection
    /// over the loaded assemblies, kept to a few lines. Reflection answers first; when it cannot
    /// (no such type, or a Manual or package page), the answer points the client at the offline
    /// Scripting Reference file if the Documentation module is installed, else at docs.unity3d.com.
    /// The bridge itself never touches the network: it only names the page.
    /// </summary>
    internal static class ApiOps
    {
        // Characters of member names in a type answer, per kind; what a kind leaves unused goes to the next.
        // Together about 250 tokens with the header, so common types stay under 300.
        private static readonly int[] _nameBudgets = { 380, 300, 160, 60 };
        private const int MaxSignatures = 25;
        private const int MaxConstructors = 4;
        private const int MaxCandidates = 8;

        // Segments that end up in a file path and in a URL: names, dots, @ (package versions), # and -.
        private static readonly Regex _segment = new Regex("^[A-Za-z0-9_.@#-]+$", RegexOptions.Compiled);
        private static readonly Regex _typeName = new Regex("^[A-Za-z_][A-Za-z0-9_.`+]*$", RegexOptions.Compiled);

        private static readonly Dictionary<Type, string> _aliases = new Dictionary<Type, string>
        {
            { typeof(void), "void" }, { typeof(bool), "bool" }, { typeof(byte), "byte" }, { typeof(sbyte), "sbyte" },
            { typeof(short), "short" }, { typeof(ushort), "ushort" }, { typeof(int), "int" }, { typeof(uint), "uint" },
            { typeof(long), "long" }, { typeof(ulong), "ulong" }, { typeof(float), "float" }, { typeof(double), "double" },
            { typeof(decimal), "decimal" }, { typeof(char), "char" }, { typeof(string), "string" }, { typeof(object), "object" },
        };

        /// <summary>The offline Documentation folder (the Hub's Documentation module); tests point it elsewhere.</summary>
        public static Func<string> DocsRoot = () => Path.Combine(EditorApplication.applicationContentsPath, "Documentation", "en");

        public static Reply Api(Request request)
        {
            string page = request.Get("page");
            if (!string.IsNullOrWhiteSpace(page))
            {
                return Page(page.Trim());
            }

            string name = (request.Get("type") ?? "").Trim();
            string member = (request.Get("member") ?? "").Trim();
            if (name.Length == 0)
            {
                return Answer.Error("give a type: ug api Transform, ug api Transform.position, ug api UnityEngine.Physics.Raycast");
            }

            if (!_typeName.IsMatch(name) || (member.Length > 0 && !_segment.IsMatch(member)))
            {
                return Answer.Error("not a type or member name: " + name + (member.Length > 0 ? "." + member : ""));
            }

            Type type = Resolve(name, out string ambiguous);
            if (ambiguous != null)
            {
                return Answer.Error(ambiguous);
            }

            int dot = name.LastIndexOf('.');
            if (type == null && member.Length == 0 && dot > 0)
            {
                type = Resolve(name.Substring(0, dot), out ambiguous);
                if (ambiguous != null)
                {
                    return Answer.Error(ambiguous);
                }

                if (type != null)
                {
                    member = name.Substring(dot + 1);
                }
            }

            if (type == null)
            {
                return Miss(name, member);
            }

            try
            {
                return member.Length == 0 ? TypeView(type) : MemberView(type, member);
            }
            catch (Exception e) when (e is TypeLoadException || e is FileNotFoundException || e is NotSupportedException)
            {
                return Answer.Error(type.FullName + " could not be read by reflection: " + e.Message);
            }
        }

        // ---- finding the type ----

        private static Type Resolve(string name, out string ambiguous)
        {
            ambiguous = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            List<Type> found = ByFullName(assemblies, name);
            if (found.Count == 0)
            {
                found = ByFullName(assemblies, "UnityEngine." + name).Concat(ByFullName(assemblies, "UnityEditor." + name)).ToList();
            }

            if (found.Count == 0)
            {
                found = Scan(assemblies, name, StringComparison.Ordinal);
            }

            if (found.Count == 0)
            {
                found = Scan(assemblies, name, StringComparison.OrdinalIgnoreCase);
            }

            if (found.Count > 1)
            {
                List<Type> unity = found.Where(t => t.Namespace != null && (t.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal) || t.Namespace.StartsWith("UnityEditor", StringComparison.Ordinal))).ToList();
                if (unity.Count == 1)
                {
                    found = unity;
                }
            }

            if (found.Count > 1)
            {
                ambiguous = "ambiguous: " + name + " (" + found.Count + " types): "
                    + string.Join(", ", found.Take(MaxCandidates).Select(t => t.FullName)) + (found.Count > MaxCandidates ? ", ..." : "") + "; use the full name";
            }

            return found.Count == 1 ? found[0] : null;
        }

        private static List<Type> ByFullName(Assembly[] assemblies, string fullName)
        {
            var found = new List<Type>();
            foreach (Assembly assembly in assemblies)
            {
                try
                {
                    Type type = assembly.GetType(fullName, false);
                    if (type != null && IsPublic(type))
                    {
                        found.Add(type);
                    }
                }
                catch (Exception e) when (e is TypeLoadException || e is FileNotFoundException || e is NotSupportedException)
                {
                }
            }

            return found.Distinct().ToList();
        }

        // Types whose simple name is `name`, or whose full name ends in `.name` (UI.Button).
        private static List<Type> Scan(Assembly[] assemblies, string name, StringComparison comparison)
        {
            var found = new List<Type>();
            foreach (Assembly assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }
                catch (Exception e) when (e is NotSupportedException || e is FileNotFoundException)
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (!IsPublic(type))
                    {
                        continue;
                    }

                    string simple = Simple(type);
                    string full = type.FullName ?? simple;
                    if (string.Equals(simple, name, comparison) || full.EndsWith("." + name, comparison))
                    {
                        found.Add(type);
                    }
                }
            }

            return found.Distinct().ToList();
        }

        private static bool IsPublic(Type type) => type.IsPublic || type.IsNestedPublic;

        private static string Simple(Type type)
        {
            int tick = type.Name.IndexOf('`');
            return tick < 0 ? type.Name : type.Name.Substring(0, tick);
        }

        // ---- the answers ----

        private static Reply TypeView(Type type)
        {
            var lines = new List<string>
            {
                Kind(type) + " " + type.FullName + Base(type) + "  (" + type.Assembly.GetName().Name + ")",
            };

            if (type.IsEnum)
            {
                lines.Add(Names("values", Enum.GetNames(type), 400));
                return Answer.Lines(lines);
            }

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var props = new List<string>();
            var methods = new List<string>();
            var statics = new List<string>();
            var events = new List<string>();
            foreach (MemberInfo member in type.GetMembers(Flags))
            {
                if (Hidden(member))
                {
                    continue;
                }

                switch (member)
                {
                    case FieldInfo field:
                        (field.IsStatic ? statics : props).Add(field.Name);
                        break;
                    case PropertyInfo property:
                        (property.GetMethod != null && property.GetMethod.IsStatic || property.SetMethod != null && property.SetMethod.IsStatic ? statics : props).Add(property.Name);
                        break;
                    case MethodInfo method when !method.IsSpecialName:
                        (method.IsStatic ? statics : methods).Add(method.Name);
                        break;
                    case EventInfo evt:
                        events.Add(evt.Name);
                        break;
                }
            }

            ConstructorInfo[] constructors = type.GetConstructors().Where(c => !Hidden(c)).ToArray();
            if (constructors.Length > 0)
            {
                string shown = string.Join("  ", constructors.Take(MaxConstructors).Select(c => "(" + Parameters(c.GetParameters()) + ")"));
                lines.Add("new  " + shown + (constructors.Length > MaxConstructors ? "  +" + (constructors.Length - MaxConstructors) + " more" : ""));
            }

            int spill = 0;
            AddNames(lines, "props", props, 0, ref spill);
            AddNames(lines, "methods", methods, 1, ref spill);
            AddNames(lines, "static", statics, 2, ref spill);
            AddNames(lines, "events", events, 3, ref spill);
            lines.Add("ug api " + Simple(type) + ".<member> for signatures (inherited members too)");
            return Answer.Lines(lines);
        }

        private static Reply MemberView(Type type, string name)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            List<MemberInfo> members = type.GetMember(name, Flags).Where(m => !Hidden(m) || Obsolete(m)).ToList();
            if (members.Count == 0)
            {
                members = type.GetMember(name, Flags | BindingFlags.IgnoreCase).Where(m => !Hidden(m) || Obsolete(m)).ToList();
            }

            members = members.Where(m => !(m is MethodInfo method && method.IsSpecialName)).ToList();
            if (members.Any(m => !Obsolete(m)))
            {
                members = members.Where(m => !Obsolete(m)).ToList();
            }

            if (members.Count == 0)
            {
                IEnumerable<string> all = type.GetMembers(Flags).Where(m => !Hidden(m)).Select(m => m.Name);
                return Answer.Error("no member " + name + " on " + type.FullName + Addresses.Near(name, all));
            }

            var lines = new List<string> { type.FullName + "." + members[0].Name };
            foreach (MemberInfo member in members.Take(MaxSignatures))
            {
                lines.Add("  " + Signature(member) + (member.DeclaringType != type && member.DeclaringType != null ? "  (from " + Simple(member.DeclaringType) + ")" : "")
                    + (Obsolete(member) ? "  (obsolete)" : ""));
            }

            if (members.Count > MaxSignatures)
            {
                lines.Add("  +" + (members.Count - MaxSignatures) + " more");
            }

            return Answer.Lines(lines);
        }

        private static string Kind(Type type)
        {
            if (type.IsEnum)
            {
                return "enum";
            }

            if (type.IsInterface)
            {
                return "interface";
            }

            if (type.IsValueType)
            {
                return "struct";
            }

            return type.IsAbstract && type.IsSealed ? "static class" : type.IsAbstract ? "abstract class" : "class";
        }

        private static string Base(Type type) =>
            type.BaseType == null || type.BaseType == typeof(object) || type.IsEnum || type.IsValueType ? "" : " : " + TypeName(type.BaseType);

        private static void AddNames(List<string> lines, string label, List<string> names, int kind, ref int spill)
        {
            if (names.Count == 0)
            {
                spill += _nameBudgets[kind];
                return;
            }

            string line = Names(label, names.Distinct().ToList(), _nameBudgets[kind] + spill);
            spill = Math.Max(0, _nameBudgets[kind] + spill - (line.Length - label.Length - 2));
            lines.Add(line);
        }

        private static string Names(string label, IList<string> names, int budget) => Names(label, names, budget, out _);

        // `label  a, b, c` cut when the names use up the budget; the rest is counted.
        private static string Names(string label, IList<string> names, int budget, out int left)
        {
            string line = label + "  ";
            int used = 0;
            left = 0;
            for (int i = 0; i < names.Count; i++)
            {
                string next = (i == 0 ? "" : ", ") + names[i];
                if (used + next.Length > budget)
                {
                    left = names.Count - i;
                    return line + ", +" + left + " more";
                }

                line += next;
                used += next.Length;
            }

            return line;
        }

        private static bool Hidden(MemberInfo member)
        {
            if (Obsolete(member) || member.IsDefined(typeof(CompilerGeneratedAttribute), false))
            {
                return true;
            }

            object[] browsable = member.GetCustomAttributes(typeof(EditorBrowsableAttribute), false);
            return browsable.Length > 0 && ((EditorBrowsableAttribute)browsable[0]).State == EditorBrowsableState.Never;
        }

        private static bool Obsolete(MemberInfo member) => member.IsDefined(typeof(ObsoleteAttribute), false);

        // ---- signatures ----

        private static string Signature(MemberInfo member)
        {
            switch (member)
            {
                case MethodInfo method:
                    string generic = method.IsGenericMethodDefinition ? "<" + string.Join(", ", method.GetGenericArguments().Select(a => a.Name)) + ">" : "";
                    return (method.IsStatic ? "static " : "") + TypeName(method.ReturnType) + " " + method.Name + generic + "(" + Parameters(method.GetParameters()) + ")";
                case PropertyInfo property:
                    ParameterInfo[] index = property.GetIndexParameters();
                    MethodInfo getter = property.GetGetMethod();
                    MethodInfo setter = property.GetSetMethod();
                    string accessors = (getter != null ? " get;" : "") + (setter != null ? " set;" : "");
                    bool isStatic = (getter ?? setter)?.IsStatic == true;
                    return (isStatic ? "static " : "") + TypeName(property.PropertyType) + " "
                        + (index.Length > 0 ? "this[" + Parameters(index) + "]" : property.Name) + " {" + accessors + " }";
                case FieldInfo field:
                    string modifier = field.IsLiteral ? "const " : (field.IsStatic ? "static " : "") + (field.IsInitOnly ? "readonly " : "");
                    return modifier + TypeName(field.FieldType) + " " + field.Name + (field.IsLiteral ? " = " + Literal(field.GetRawConstantValue(), field.FieldType) : "");
                case EventInfo evt:
                    return "event " + TypeName(evt.EventHandlerType) + " " + evt.Name;
                default:
                    return member.Name;
            }
        }

        private static string Parameters(ParameterInfo[] parameters) =>
            string.Join(", ", parameters.Select(p =>
            {
                string prefix = p.IsOut ? "out " : p.ParameterType.IsByRef ? (p.IsIn ? "in " : "ref ") : p.IsDefined(typeof(ParamArrayAttribute), false) ? "params " : "";
                return prefix + TypeName(p.ParameterType) + " " + p.Name + (p.HasDefaultValue ? " = " + Literal(p.RawDefaultValue, p.ParameterType) : "");
            }));

        private static string Literal(object value, Type type)
        {
            if (value == null || value == DBNull.Value || value == Type.Missing)
            {
                return type.IsValueType ? "default" : "null";
            }

            if (type.IsEnum)
            {
                return TypeName(type) + "." + Enum.ToObject(type, value);
            }

            if (value is string text)
            {
                return "\"" + text + "\"";
            }

            if (value is bool flag)
            {
                return flag ? "true" : "false";
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string TypeName(Type type)
        {
            if (type.IsByRef)
            {
                return TypeName(type.GetElementType());
            }

            if (type.IsArray)
            {
                return TypeName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            }

            if (_aliases.TryGetValue(type, out string alias))
            {
                return alias;
            }

            if (type.IsGenericType)
            {
                Type[] arguments = type.GetGenericArguments();
                return type.GetGenericTypeDefinition() == typeof(Nullable<>)
                    ? TypeName(arguments[0]) + "?"
                    : Simple(type) + "<" + string.Join(", ", arguments.Select(TypeName)) + ">";
            }

            return type.IsNested && !type.IsGenericParameter ? TypeName(type.DeclaringType) + "." + type.Name : type.Name;
        }

        // ---- when reflection cannot answer: the documentation pages ----

        private static Reply Miss(string name, string member)
        {
            string type = name.StartsWith("UnityEngine.", StringComparison.Ordinal) ? name.Substring("UnityEngine.".Length) : name;
            var pages = new List<string> { "ScriptReference/" + type };
            if (member.Length > 0)
            {
                pages = new List<string> { "ScriptReference/" + type + "." + member, "ScriptReference/" + type + "-" + member };
            }

            return Docs(pages, "no type " + name + " in the loaded assemblies");
        }

        private static Reply Page(string page)
        {
            string trimmed = page.Trim('/');
            if (trimmed.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - ".html".Length);
            }

            string[] segments = trimmed.Split('/');
            if (trimmed.Length == 0 || segments.Any(s => s.Length == 0 || s == ".." || s == "." || !_segment.IsMatch(s)))
            {
                return Answer.Error("not a documentation page: " + page + " (like Manual/Prefabs, ScriptReference/Rigidbody.AddForce, com.unity.cinemachine@3.1/manual/index)");
            }

            if (segments[0] == "Manual" || segments[0] == "ScriptReference")
            {
                return segments.Length == 1
                    ? Answer.Error("give a page after " + segments[0] + "/: like " + segments[0] + "/" + (segments[0] == "Manual" ? "Prefabs" : "Rigidbody.AddForce"))
                    : Docs(new List<string> { trimmed }, trimmed + " is not in the local documentation");
            }

            if (segments[0].StartsWith("com.", StringComparison.Ordinal) || segments[0].Contains("@"))
            {
                return Package(segments);
            }

            return Docs(new List<string> { "Manual/" + trimmed }, "Manual/" + trimmed + " is not in the local documentation");
        }

        private static Reply Package(string[] segments)
        {
            string package = segments[0];
            string version = null;
            int at = package.IndexOf('@');
            if (at >= 0)
            {
                version = package.Substring(at + 1);
                package = package.Substring(0, at);
            }
            else
            {
                UnityEditor.PackageManager.PackageInfo installed = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().FirstOrDefault(p => p.name == package);
                if (installed != null)
                {
                    version = string.Join(".", installed.version.Split('.').Take(2));
                }
            }

            if (string.IsNullOrEmpty(version))
            {
                return Answer.Error(package + " is not installed, so its documentation version is unknown: give one, like " + package + "@1.2/manual/index");
            }

            string path = segments.Length > 1 ? string.Join("/", segments.Skip(1)) : "manual/index";
            return Reply.Ok(new Json().Object()
                .Field("error", package + "@" + version + "/" + path + " has no offline copy")
                .Key("online").Array().Value("https://docs.unity3d.com/Packages/" + package + "@" + version + "/" + path + ".html").EndArray()
                .EndObject().ToString());
        }

        // The local file of the first page that exists, else the online addresses of all of them.
        private static Reply Docs(List<string> pages, string error)
        {
            string root = null;
            try
            {
                root = DocsRoot();
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
            }

            foreach (string page in pages)
            {
                string file = root == null ? null : Path.Combine(root, page.Replace('/', Path.DirectorySeparatorChar) + ".html");
                if (file != null && File.Exists(file))
                {
                    return Reply.Ok(new Json().Object().Field("local", file).EndObject().ToString());
                }
            }

            string version = string.Join(".", Application.unityVersion.Split('.').Take(2));
            var json = new Json().Object().Field("error", error).Key("online").Array();
            foreach (string page in pages)
            {
                json.Value("https://docs.unity3d.com/" + version + "/Documentation/" + page + ".html");
            }

            return Reply.Ok(json.EndArray().EndObject().ToString());
        }
    }
}
