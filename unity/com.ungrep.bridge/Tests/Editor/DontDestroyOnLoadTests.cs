using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Ungrep.Bridge.Tests
{
    /// <summary>
    /// DontDestroyOnLoad in play mode (SPEC-editor-ops O14). Each test enters play mode from a saved
    /// scene `Game` (root `Level`) and leaves it; play mode reloads the domain, so a test keeps no
    /// object or reflection handle across the yield and sets nothing up outside its own body.
    /// </summary>
    public class DontDestroyOnLoadTests
    {
        private const string Folder = "Assets/_UgDdolTests";
        private const string GamePath = Folder + "/Game.unity";
        private const string PrefabPath = Folder + "/Crate.prefab";
        private const string Lost = "  (play mode: lost when play stops)";

        [UnityTearDown]
        public IEnumerator LeavePlayMode()
        {
            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Folder);
        }

        [UnityTest]
        public IEnumerator Kept_objects_are_listed_found_inspected_and_set_in_play_mode()
        {
            SaveGame();
            Assert.AreEqual(Lines(GamePath + "  active  1 root", "edit mode"), Ask("/scene"));
            Assert.IsFalse(Addresses.DontDestroyOnLoadScene().IsValid());

            yield return new EnterPlayMode();

            Assert.AreEqual(Lines(GamePath + "  active  1 root", "playing"), Ask("/scene"));
            GameObject boot = Keep("Boot");
            boot.AddComponent<AudioSource>();
            new GameObject("Music").transform.SetParent(boot.transform);

            Assert.AreEqual(Lines(GamePath + "  active  1 root", "DontDestroyOnLoad  1 root", "playing"), Ask("/scene"));
            Assert.AreEqual("Boot", Send("/inspect", "object", "Boot").Lines[0]);
            Assert.AreEqual("Boot/Music", Send("/inspect", "object", "DontDestroyOnLoad:Boot/Music").Lines[0]);
            Assert.AreEqual(Lines("Boot"), Ask("/find", "component", "AudioSource"));
            Assert.AreEqual(Lines("Boot/Music"), Ask("/find", "name", "music"));
            Assert.AreEqual(Lines("Game:", "  Level", "DontDestroyOnLoad:", "  Boot  AudioSource", "    Music"), Ask("/tree"));
            Assert.AreEqual(Lines("Boot  AudioSource", "  Music"), Ask("/tree", "object", "Boot", "depth", "1"));
            string set = Send("/set", "object", "Boot", "component", "AudioSource", "field", "m_Volume", "value", "0.25").Lines[0];
            StringAssert.StartsWith("Boot  AudioSource.", set);
            StringAssert.EndsWith("  1 -> 0.25" + Lost, set);
            Assert.AreEqual(0.25f, boot.GetComponent<AudioSource>().volume);
            Assert.AreEqual(Err("not found: Bot in the loaded scenes (near: Boot)"), Ask("/inspect", "object", "Bot"));

            yield return new ExitPlayMode();

            Assert.AreEqual(Lines(GamePath + "  active  1 root", "edit mode"), Ask("/scene"));
        }

        [UnityTest]
        public IEnumerator A_root_name_in_both_scenes_puts_the_scene_in_those_addresses_only()
        {
            SaveGame();

            yield return new EnterPlayMode();

            GameObject played = new GameObject("Boot");
            GameObject kept = Keep("Boot");
            GameObject solo = Keep("Solo");
            Assert.AreEqual("Game:Boot", Addresses.Of(played));
            Assert.AreEqual("DontDestroyOnLoad:Boot", Addresses.Of(kept));
            Assert.AreEqual("Solo", Addresses.Of(solo));
            Assert.AreEqual("Level", Addresses.Of(GameObject.Find("Level")));
            Assert.AreSame(kept, Addresses.Find("DontDestroyOnLoad:Boot", out _));
            Assert.AreSame(played, Addresses.Find("Game:Boot", out _));
            Assert.AreSame(solo, Addresses.Find("Solo", out _));
            Assert.AreEqual(Err("ambiguous: Boot (2 in the loaded scenes): Game:Boot, DontDestroyOnLoad:Boot"), Ask("/inspect", "object", "Boot"));
            Assert.AreEqual(Lines("Game:Boot", "DontDestroyOnLoad:Boot"), Ask("/find", "name", "Boot"));
        }

        [UnityTest]
        public IEnumerator Objects_move_into_and_out_of_DontDestroyOnLoad_which_cannot_be_active()
        {
            SaveGame();
            var crate = new GameObject("Crate");
            PrefabUtility.SaveAsPrefabAsset(crate, PrefabPath);
            Object.DestroyImmediate(crate);

            yield return new EnterPlayMode();

            Assert.AreEqual(Lines("created Keeper" + Lost), Ask("/object/create", "name", "Keeper", "scene", "DontDestroyOnLoad"));
            Assert.AreEqual("DontDestroyOnLoad", GameObject.Find("Keeper").scene.name);
            Assert.AreEqual(Lines("created Crate (Crate prefab instance)" + Lost), Ask("/object/create", "prefab", PrefabPath, "scene", "DontDestroyOnLoad"));
            Assert.AreEqual("DontDestroyOnLoad", GameObject.Find("Crate").scene.name);

            new GameObject("Probe");
            Assert.AreEqual(Lines("moved Probe -> Probe" + Lost), Ask("/object/move", "object", "Probe", "scene", "DontDestroyOnLoad"));
            Assert.AreEqual("DontDestroyOnLoad", GameObject.Find("Probe").scene.name);
            Assert.AreEqual(Lines("moved Probe -> Probe" + Lost), Ask("/object/move", "object", "Probe", "scene", "Game"));
            Assert.AreEqual("Game", GameObject.Find("Probe").scene.name);

            Assert.AreEqual(Err("DontDestroyOnLoad cannot be the active scene"), Ask("/scene/active", "path", "DontDestroyOnLoad"));
            Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        }

        [UnityTest]
        public IEnumerator The_probe_names_the_scene_the_internal_getter_gives_and_leaves_nothing()
        {
            SaveGame();

            yield return new EnterPlayMode();

            Assert.IsFalse(Addresses.DontDestroyOnLoadScene().IsValid(), "an empty DontDestroyOnLoad is not listed");
            Scene probed = Addresses.ProbeDontDestroyOnLoad();
            Assert.AreEqual("DontDestroyOnLoad", probed.name);
            Assert.AreEqual(0, probed.rootCount);

            Keep("Boot");
            Assert.AreEqual(probed, Addresses.DontDestroyOnLoadScene());
            Assert.AreEqual(probed, Addresses.ProbeDontDestroyOnLoad());
            Assert.AreEqual(1, probed.rootCount);
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);
        }

        private static void SaveGame()
        {
            AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.CreateFolder("Assets", "_UgDdolTests");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Level");
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), GamePath);
        }

        private static GameObject Keep(string name)
        {
            var kept = new GameObject(name);
            Object.DontDestroyOnLoad(kept);
            return kept;
        }

        private static Reply Send(string path, params string[] query)
        {
            var request = new Request { Path = path };
            for (int i = 0; i + 1 < query.Length; i += 2)
            {
                request.Query[query[i]] = query[i + 1];
            }

            return Routes.Handle(request, new ConsoleBuffer())();
        }

        private static string Ask(string path, params string[] query) => Send(path, query).Body;

        private static string Lines(params string[] lines) => Answer.Lines(lines).Body;

        private static string Err(string message) => Answer.Error(message).Body;
    }
}
