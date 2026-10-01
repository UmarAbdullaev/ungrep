using System;
using System.Collections.Generic;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// The editor-ops requests by path (SPEC-editor-ops, Endpoints). Most answer at once; a few
    /// (package requests) answer on a later editor update, so every route gives the pump a
    /// <see cref="Func{Reply}"/> that returns null until its answer is ready.
    /// </summary>
    internal static class Routes
    {
        private static readonly Dictionary<string, Func<Request, ConsoleBuffer, Func<Reply>>> _routes =
            new Dictionary<string, Func<Request, ConsoleBuffer, Func<Reply>>>(StringComparer.Ordinal)
            {
                // Scenes and play mode.
                { "/scene", (r, c) => Now(SceneOps.Scenes()) },
                { "/scene/open", (r, c) => Checked(() => SceneOps.Open(r)) },
                { "/scene/new", (r, c) => Checked(() => SceneOps.New(r)) },
                { "/scene/close", (r, c) => Checked(() => SceneOps.Close(r)) },
                { "/scene/active", (r, c) => Checked(() => SceneOps.Activate(r)) },
                { "/save", (r, c) => Checked(() => SceneOps.Save(r)) },
                { "/play", (r, c) => Checked(SceneOps.Play) },
                { "/pause", (r, c) => Now(SceneOps.Pause()) },
                { "/stop", (r, c) => Now(SceneOps.Stop(c)) },

                // Reads.
                { "/tree", (r, c) => Checked(() => ReadOps.Tree(r)) },
                { "/inspect", (r, c) => Checked(() => ReadOps.Inspect(r)) },
                { "/find", (r, c) => Checked(() => ReadOps.Find(r)) },

                // Edits.
                { "/object/create", (r, c) => Checked(() => EditOps.Create(r)) },
                { "/object/delete", (r, c) => Checked(() => EditOps.Delete(r)) },
                { "/object/rename", (r, c) => Checked(() => EditOps.Rename(r)) },
                { "/object/move", (r, c) => Checked(() => EditOps.Move(r)) },
                { "/object/active", (r, c) => Checked(() => EditOps.Active(r)) },
                { "/object/duplicate", (r, c) => Checked(() => EditOps.Duplicate(r)) },
                { "/component/add", (r, c) => Checked(() => EditOps.AddComponent(r)) },
                { "/component/remove", (r, c) => Checked(() => EditOps.RemoveComponent(r)) },
                { "/set", (r, c) => Checked(() => EditOps.Set(r)) },
                { "/event/list", (r, c) => Checked(() => EventOps.List(r)) },
                { "/event/add", (r, c) => Checked(() => EventOps.Add(r)) },
                { "/event/remove", (r, c) => Checked(() => EventOps.Remove(r)) },
                { "/undo", (r, c) => Checked(() => EditOps.UndoLast(false)) },
                { "/redo", (r, c) => Checked(() => EditOps.UndoLast(true)) },
                { "/select", (r, c) => Checked(() => EditOps.Select(r)) },

                // Prefabs.
                { "/prefab/create", (r, c) => Checked(() => PrefabOps.Create(r)) },
                { "/prefab/apply", (r, c) => Checked(() => PrefabOps.Apply(r)) },
                { "/prefab/revert", (r, c) => Checked(() => PrefabOps.Revert(r)) },
                { "/prefab/unpack", (r, c) => Checked(() => PrefabOps.Unpack(r)) },

                // Assets.
                { "/asset/folder", (r, c) => Checked(() => AssetOps.Folder(r)) },
                { "/asset/create", (r, c) => Checked(() => AssetOps.Create(r)) },
                { "/asset/move", (r, c) => Checked(() => AssetOps.Move(r)) },
                { "/asset/rename", (r, c) => Checked(() => AssetOps.Rename(r)) },
                { "/asset/duplicate", (r, c) => Checked(() => AssetOps.Duplicate(r)) },
                { "/asset/delete", (r, c) => Checked(() => AssetOps.Delete(r)) },
                { "/asset/reimport", (r, c) => Checked(() => AssetOps.Reimport(r)) },

                // Project settings and packages.
                { "/project", (r, c) => Checked(ProjectOps.Project) },
                { "/package/list", (r, c) => Deferred(PackageOps.List) },
                { "/package/add", (r, c) => Deferred(() => PackageOps.Add(r)) },
                { "/package/remove", (r, c) => Deferred(() => PackageOps.Remove(r)) },

                // Screenshots, API lookup and stats.
                { "/shot", (r, c) => Checked(() => ShotOps.Shot(r)) },
                { "/api", (r, c) => Checked(() => ApiOps.Api(r)) },
                { "/stats", (r, c) => Checked(StatsOps.Stats) },
            };

        /// <summary>The handler for an editor-ops request, or null for a path that is not one.</summary>
        public static Func<Reply> Handle(Request request, ConsoleBuffer console) =>
            _routes.TryGetValue(request.Path, out Func<Request, ConsoleBuffer, Func<Reply>> route) ? route(request, console) : null;

        private static Func<Reply> Now(Reply reply) => () => reply;

        // Refused while compiling or importing; a handler that throws answers the exception as an error.
        private static Func<Reply> Checked(Func<Reply> handler)
        {
            Reply reply = SceneOps.Busy();
            if (reply == null)
            {
                try
                {
                    reply = handler();
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    reply = Answer.Error("Unity threw " + e.GetType().Name + ": " + e.Message);
                }
            }

            return () => reply;
        }

        // A request that finishes on a later update: refused at once while busy, else the poll it starts.
        private static Func<Reply> Deferred(Func<Func<Reply>> start)
        {
            Reply busy = SceneOps.Busy();
            if (busy != null)
            {
                return () => busy;
            }

            try
            {
                return start();
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                Reply failed = Answer.Error("Unity threw " + e.GetType().Name + ": " + e.Message);
                return () => failed;
            }
        }
    }
}
