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
    }
}
