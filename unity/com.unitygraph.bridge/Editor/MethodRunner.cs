using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEditor;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// Runs a static parameterless method of one of the project's own assemblies, or a menu item
    /// when the target contains `/`. Never runs Unity's or the runtime's own methods.
    /// </summary>
    internal static class MethodRunner
    {
        private const int MaxResult = 2048;

        internal sealed class Outcome
        {
            public bool Ran;
            public string Result;
            public string Error;
            public string Frame;
            public double Milliseconds;
        }

        public static Outcome Run(string target, Func<string, bool> isOwnAssembly, string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return new Outcome { Error = "no target" };
            }

            var clock = Stopwatch.StartNew();
            if (target.Contains("/"))
            {
                string refused = RefusedMenu(target);
                if (refused != null)
                {
                    return new Outcome { Error = refused };
                }

                bool ran = EditorApplication.ExecuteMenuItem(target);
                return new Outcome { Ran = ran, Error = ran ? null : "no menu item " + target, Milliseconds = clock.Elapsed.TotalMilliseconds };
            }

            string error = Resolve(target, isOwnAssembly, out MethodInfo method);
            if (method == null)
            {
                return new Outcome { Error = error };
            }

            clock.Restart();
            try
            {
                object value = method.Invoke(null, null);
                string text = method.ReturnType == typeof(void) ? null : value == null ? "null" : value.ToString();
                if (text != null && text.Length > MaxResult)
                {
                    text = text.Substring(0, MaxResult);
                }

                return new Outcome { Ran = true, Result = text, Milliseconds = clock.Elapsed.TotalMilliseconds };
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                Exception inner = e.InnerException;
                return new Outcome
                {
                    Ran = true,
                    Error = inner.GetType().FullName + ": " + inner.Message,
                    Frame = StackFrames.OwnFrame(inner.StackTrace, projectRoot),
                    Milliseconds = clock.Elapsed.TotalMilliseconds,
                };
            }
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

        /// <summary>Finds `Type.Method` (full or short type name); the error says why nothing was found.</summary>
        public static string Resolve(string target, Func<string, bool> isOwnAssembly, out MethodInfo method)
        {
            method = null;
            int dot = target.LastIndexOf('.');
            if (dot <= 0 || dot == target.Length - 1)
            {
                return "target must be Type.Method or a menu path: " + target;
            }

            string typeName = target.Substring(0, dot);
            string methodName = target.Substring(dot + 1);
            var types = new List<Type>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || !isOwnAssembly(assembly.GetName().Name))
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

                types.AddRange(all.Where(t => DisplayName(t) == typeName || t.Name == typeName));
            }

            if (types.Count == 0)
            {
                return "no type " + typeName + " in the project's assemblies";
            }

            if (types.Count > 1)
            {
                return "ambiguous " + typeName + ": " + string.Join(", ", types.Select(DisplayName).OrderBy(n => n, StringComparer.Ordinal));
            }

            method = types[0].GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == 0 && !m.IsGenericMethodDefinition);
            return method == null ? "no static parameterless method " + methodName + " on " + DisplayName(types[0]) : null;
        }

        /// <summary>Whether an assembly is Unity's, the runtime's or a test framework's, which are never run.</summary>
        public static bool IsFramework(string assemblyName) =>
            assemblyName == "mscorlib" || assemblyName == "netstandard" || assemblyName.StartsWith("System", StringComparison.Ordinal)
            || assemblyName.StartsWith("Mono.", StringComparison.Ordinal) || assemblyName.StartsWith("UnityEngine", StringComparison.Ordinal)
            || assemblyName.StartsWith("UnityEditor", StringComparison.Ordinal) || assemblyName.StartsWith("Unity.", StringComparison.Ordinal)
            || assemblyName.StartsWith("nunit", StringComparison.OrdinalIgnoreCase) || assemblyName.StartsWith("UnityGraph.Bridge", StringComparison.Ordinal);

        private static string DisplayName(Type type) => type.FullName?.Replace('+', '.');
    }
}
