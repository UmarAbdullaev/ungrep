using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityGraph.Bridge
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
            var json = new Json().Object().Key("lines").Array();
            foreach (string line in lines)
            {
                json.Value(line);
            }

            json.EndArray();
            if (wait != null)
            {
                json.Field("wait", wait);
            }

            return Reply.Ok(json.EndObject().ToString());
        }

        /// <summary>Runs an edit as one undo group named `ug <name>`, so one Ctrl+Z in the editor reverts it.</summary>
        public static Reply Undoable(string name, Func<Reply> edit)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            GroupName = "ug " + name;
            Undo.SetCurrentGroupName(GroupName);
            try
            {
                return edit();
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }
        }

        /// <summary>The running edit's undo group name; Undo records use it so Edit > Undo shows the command.</summary>
        public static string GroupName { get; private set; } = "ug edit";

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
