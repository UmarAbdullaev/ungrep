using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Ungrep.Bridge
{
    /// <summary>
    /// The answer shape of every editor-ops request (SPEC-editor-ops, Endpoints): `{"lines": [...]}`
    /// already formatted as ug prints them, or `{"error": ...}`, always with status 200. A `wait`
    /// field asks the client to wait for that editor state before printing (play mode changes).
    /// </summary>
    internal static class Answer
    {
        public static Reply Error(string message) => Reply.Ok(new Json().Object().Field("error", message).EndObject().ToString());

        public static bool IsError(Reply reply) => reply.Body.StartsWith("{\"error\"", StringComparison.Ordinal);

        public static Reply Line(string line) => Lines(new[] { line });

        public static Reply Lines(IEnumerable<string> lines, string wait = null)
        {
            var kept = new List<string>(lines);
            var json = new Json().Object().Key("lines").Array();
            foreach (string line in kept)
            {
                json.Value(line);
            }

            json.EndArray();
            if (wait != null)
            {
                json.Field("wait", wait);
            }

            Reply reply = Reply.Ok(json.EndObject().ToString());
            reply.Lines = kept;
            return reply;
        }

        /// <summary>The answer with more lines after its own; an answer that is not lines is returned as it is.</summary>
        public static Reply Extend(Reply reply, IEnumerable<string> more)
        {
            if (reply.Lines == null)
            {
                return reply;
            }

            var all = new List<string>(reply.Lines);
            all.AddRange(more);
            return Lines(all);
        }

        /// <summary>
        /// Runs an edit as one undo group named `ug <name>`, so one Ctrl+Z in the editor reverts it. An
        /// error reverts what the edit recorded. A group left with no records (an error, or an edit that
        /// changed nothing, like a field set to its value) still lists under its name, so ug undo would
        /// print it and undo the edit before: a new group drops it, and the last recorded name is back.
        /// </summary>
        public static Reply Undoable(string name, Func<Reply> edit)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            GroupName = "ug " + name;
            Undo.SetCurrentGroupName(GroupName);
            Reply reply = null;
            try
            {
                reply = edit();
                return reply;
            }
            finally
            {
                bool failed = reply == null || IsError(reply);
                if (failed)
                {
                    Undo.RevertAllInCurrentGroup();
                }

                Undo.FlushUndoRecordObjects();
                Undo.CollapseUndoOperations(group);
                if (failed || RecordedNothing())
                {
                    Undo.IncrementCurrentGroup();
                }
            }
        }

        /// <summary>The running edit's undo group name; Undo records use it so Edit > Undo shows the command.</summary>
        public static string GroupName { get; private set; } = "ug edit";

        // Unity keeps the undo and redo names internally (Edit > Undo shows them); read them by
        // reflection, and fall back to nameless answers when that method is gone.
        public static bool UndoRecords(out List<string> undos, out List<string> redos)
        {
            undos = new List<string>();
            redos = new List<string>();
            MethodInfo method = typeof(Undo).GetMethod(
                "GetRecords",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(List<string>), typeof(List<string>) },
                null);
            if (method == null)
            {
                return false;
            }

            try
            {
                method.Invoke(null, new object[] { undos, redos });
                return true;
            }
            catch (TargetInvocationException)
            {
                return false;
            }
        }

        // The undo records list each group's name, then its records' names; the current group with no
        // records lists its name alone. So under a name no record has, that name last means no records.
        private static bool RecordedNothing()
        {
            const string probe = "ug: no records?";
            string name = Undo.GetCurrentGroupName();
            Undo.SetCurrentGroupName(probe);
            bool none = UndoRecords(out List<string> undos, out _) && undos.Count > 0 && undos[undos.Count - 1] == probe;
            Undo.SetCurrentGroupName(name);
            return none;
        }

        /// <summary>What happens to an edit of this object: unsaved in its scene, lost after play, or written.</summary>
        public static string Unsaved(GameObject target)
        {
            string contents = Addresses.ContentsPath(target);
            if (contents != null)
            {
                return "  (written " + contents + ")";
            }

            if (EditorUtility.IsPersistent(target))
            {
                return "  (written " + AssetDatabase.GetAssetPath(target) + ")";
            }

            if (EditorApplication.isPlaying)
            {
                return "  (play mode: lost when play stops)";
            }

            string scene = string.IsNullOrEmpty(target.scene.path) ? "the untitled scene" : System.IO.Path.GetFileName(target.scene.path);
            return "  (" + scene + " modified, not saved)";
        }

        /// <summary>The same for an asset's main object or a settings asset: saved by ug save.</summary>
        public static string UnsavedAsset(UnityEngine.Object asset) =>
            asset is GameObject go ? Unsaved(go) : "  (" + AssetDatabase.GetAssetPath(asset) + " modified, not saved)";

        public static string Count(int count, string one, string many) =>
            count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);
    }
}
