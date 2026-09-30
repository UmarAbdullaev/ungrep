using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// Package requests (SPEC-editor-ops, Endpoints and Safety rule 10) through Unity's own Package
    /// Manager client: it resolves, downloads and writes `Packages/manifest.json`, ug does not. Each
    /// request finishes on a later editor update, so these return a poll that gives null until the
    /// Package Manager has answered. `add` may reach the registry or a git host: only on an explicit command.
    /// </summary>
    internal static class PackageOps
    {
        public const string BridgePackage = "com.unitygraph.bridge";

        /// <summary>`/package/list`: the manifest's direct dependencies, from the local cache (no network).</summary>
        public static Func<Reply> List()
        {
            ListRequest list = Client.List(true, false);
            return Poll(() => list.Status != StatusCode.InProgress, () =>
            {
                if (list.Status == StatusCode.Failure)
                {
                    return Answer.Error("could not list packages: " + list.Error.message);
                }

                return Answer.Lines(ListLines(list.Result));
            });
        }

        /// <summary>`/package/add?id=`: a registry name (`com.x`, `com.x@1.2.0`), a git URL or `file:` path.</summary>
        public static Func<Reply> Add(Request request) => Add(request, EditorApplication.isPlaying);

        public static Func<Reply> Add(Request request, bool playing)
        {
            string id = (request.Get("id") ?? "").Trim();
            string refused = id.Length == 0 ? "no package id given: a name, name@version, a git URL or file:path" : Playing(playing);
            if (refused != null)
            {
                return Now(Answer.Error(refused));
            }

            AddRequest add = Client.Add(id);
            return Poll(() => add.Status != StatusCode.InProgress, () =>
                add.Status == StatusCode.Failure
                    ? Answer.Error("could not add " + id + ": " + add.Error.message)
                    : Answer.Line("added " + Describe(add.Result) + Written()));
        }

        /// <summary>`/package/remove?name=`: a direct dependency of the manifest, by package name.</summary>
        public static Func<Reply> Remove(Request request) => Remove(request, EditorApplication.isPlaying);

        public static Func<Reply> Remove(Request request, bool playing)
        {
            string name = (request.Get("name") ?? "").Trim();
            string refused = name.Length == 0 ? "no package name given"
                : name.Split('@')[0].Equals(BridgePackage, StringComparison.OrdinalIgnoreCase)
                    ? "removing " + BridgePackage + " would end this connection: remove it in Package Manager (Window > Package Manager)"
                    : Playing(playing);
            if (refused != null)
            {
                return Now(Answer.Error(refused));
            }

            RemoveRequest remove = Client.Remove(name);
            return Poll(() => remove.Status != StatusCode.InProgress, () =>
                remove.Status == StatusCode.Failure
                    ? Answer.Error("could not remove " + name + ": " + remove.Error.message)
                    : Answer.Line("removed " + remove.PackageIdOrName + Written()));
        }

        /// <summary>A poll for the server's pump: null while the request is running, then its answer.</summary>
        public static Func<Reply> Poll(Func<bool> completed, Func<Reply> answer) =>
            () => completed() ? answer() : null;

        private static Func<Reply> Now(Reply reply) => () => reply;

        // Adding or removing a package reloads scripts, which ends play mode.
        private static string Playing(bool playing) =>
            playing ? "package changes reload scripts and end play mode: ug stop first" : null;

        private static string Written() =>
            "  (Unity updated Packages/manifest.json; scripts may recompile, ug compile waits; not undoable)";

        // `com.x  1.2.0  registry`, and where a local or git package comes from.
        private static string Describe(UnityEditor.PackageManager.PackageInfo package)
        {
            string line = package.name + "  " + package.version + "  " + package.source.ToString().ToLowerInvariant();
            int at = package.packageId.IndexOf('@');
            bool located = package.source == PackageSource.Local || package.source == PackageSource.Git || package.source == PackageSource.Embedded;
            return located && at >= 0 ? line + "  " + package.packageId.Substring(at + 1) : line;
        }

        // Unity's engine modules (com.unity.modules.*) are counted, not listed: they are never the user's choice.
        private static bool IsModule(UnityEditor.PackageManager.PackageInfo package) =>
            package.name.StartsWith("com.unity.modules.", StringComparison.Ordinal);

        private static IEnumerable<string> ListLines(PackageCollection packages)
        {
            List<string> lines = packages
                .Where(package => !IsModule(package))
                .OrderBy(package => package.name, StringComparer.Ordinal)
                .Select(Describe)
                .ToList();
            int builtIn = packages.Count(IsModule);
            if (builtIn > 0)
            {
                lines.Add("+ " + Answer.Count(builtIn, "built-in module", "built-in modules"));
            }

            return lines.Count == 0 ? new[] { "no packages" } : lines;
        }
    }
}
