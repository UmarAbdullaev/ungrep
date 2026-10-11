using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityGraph.Bridge.Tests
{
    /// <summary>
    /// Scenes saved under Assets/_UgOpsTests for each test: Main (Camera, Level/Enemy x2,
    /// Level/Player/Model), Other (Camera), a Turret prefab (Barrel/Muzzle) and a material.
    /// </summary>
    public abstract class OpsFixture
    {
        protected const string Folder = "Assets/_UgOpsTests";
        protected const string MainPath = Folder + "/Main.unity";
        protected const string OtherPath = Folder + "/Other.unity";
        protected const string PrefabPath = Folder + "/Turret.prefab";
        protected const string MaterialPath = Folder + "/Paint.mat";

        [SetUp]
        public void CreateScenes()
        {
            AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.CreateFolder("Assets", "_UgOpsTests");

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Camera");
            var level = new GameObject("Level");
            new GameObject("Enemy").transform.SetParent(level.transform);
            new GameObject("Enemy").transform.SetParent(level.transform);
            var player = new GameObject("Player");
            player.transform.SetParent(level.transform);
            new GameObject("Model").transform.SetParent(player.transform);
            player.AddComponent<UgProbe>();
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), MainPath);

            Scene other = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.MoveGameObjectToScene(new GameObject("Camera"), other);
            EditorSceneManager.SaveScene(other, OtherPath);
            EditorSceneManager.CloseScene(other, true);

            var turret = new GameObject("Turret");
            var barrel = new GameObject("Barrel");
            barrel.transform.SetParent(turret.transform);
            new GameObject("Muzzle").transform.SetParent(barrel.transform);
            PrefabUtility.SaveAsPrefabAsset(turret, PrefabPath);
            Object.DestroyImmediate(turret);

            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), MaterialPath);
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        }

        [TearDown]
        public void DeleteScenes()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Folder);
        }

        private protected static Request Request(string path, params string[] query)
        {
            var request = new Request { Path = path };
            for (int i = 0; i + 1 < query.Length; i += 2)
            {
                request.Query[query[i]] = query[i + 1];
            }

            return request;
        }

        // The body of the bridge's answer to an editor-ops request.
        protected static string Ask(string path, params string[] query) =>
            Routes.Handle(Request(path, query), new ConsoleBuffer())().Body;

        protected static string Lines(params string[] lines) => Answer.Lines(lines).Body;

        protected static string Err(string message) => Answer.Error(message).Body;

        // An object created through Undo, as ug edits are, so its scene counts as modified.
        protected static GameObject AddUnsaved(string name)
        {
            var added = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(added, "Add " + name);
            return added;
        }

        protected static GameObject Find(string address)
        {
            GameObject found = Addresses.Find(address, out string error);
            Assert.IsNull(error, error);
            return found;
        }

        protected static string Error(string address)
        {
            Assert.IsNull(Addresses.FindObject(address, out string error));
            return error;
        }
    }

    public class AddressTests : OpsFixture
    {
        [Test]
        public void Paths_resolve_by_name_and_case_insensitively()
        {
            Assert.AreEqual("Model", Find("Level/Player/Model").name);
            Assert.AreEqual("Model", Find("/level/player/model").name);
            Assert.AreEqual("Level/Player/Model", Addresses.Of(Find("Level/Player/Model")));
        }

        [Test]
        public void Repeated_names_need_an_index()
        {
            Assert.AreEqual("ambiguous: Enemy (2 in Level): Level/Enemy#1, Level/Enemy#2", Error("Level/Enemy"));
            GameObject second = Find("Level/Enemy#2");
            Assert.AreSame(second, Find("Level").transform.GetChild(1).gameObject);
            Assert.AreEqual("Level/Enemy#2", Addresses.Of(second));
            Assert.AreEqual("not found: Enemy#3 in Level (2 named Enemy)", Error("Level/Enemy#3"));
        }

        [Test]
        public void Missing_names_suggest_near_ones()
        {
            Assert.AreEqual("not found: Playr in Level (near: Player)", Error("Level/Playr"));
            Assert.AreEqual("not found: Nothing in the loaded scenes", Error("Nothing"));
            Assert.AreEqual("no object given", Error(" "));
        }

        [Test]
        public void Scene_prefixes_pick_one_of_several_loaded_scenes()
        {
            EditorSceneManager.OpenScene(OtherPath, OpenSceneMode.Additive);

            Assert.AreEqual("ambiguous: Camera (2 in the loaded scenes): Main:Camera, Other:Camera", Error("Camera"));
            GameObject camera = Find("Other:Camera");
            Assert.AreEqual("Other", camera.scene.name);
            Assert.AreEqual("Other:Camera", Addresses.Of(camera));
            Assert.AreEqual("Main:Level/Player", Addresses.Of(Find("Level/Player")));
        }

        [Test]
        public void Prefab_assets_and_other_assets()
        {
            GameObject muzzle = Find(PrefabPath + ":Barrel/Muzzle");
            Assert.AreEqual("Muzzle", muzzle.name);
            Assert.AreEqual(PrefabPath + ":Barrel/Muzzle", Addresses.Of(muzzle));
            Assert.AreEqual(PrefabPath, Addresses.Of(Find(PrefabPath)));
            Assert.AreEqual("not found: Barel in " + PrefabPath + " (near: Barrel)", Error(PrefabPath + ":Barel"));

            Assert.IsInstanceOf<Material>(Addresses.FindObject(MaterialPath, out string error));
            Assert.IsNull(error);
            Assert.IsNull(Addresses.Find(MaterialPath, out error));
            Assert.AreEqual(MaterialPath + " is a Material, not a game object", error);
            Assert.AreEqual("not found: " + Folder + "/None.asset", Error(Folder + "/None.asset"));
        }
    }

    public class SceneOpsTests : OpsFixture
    {
        [Test]
        public void Lists_the_loaded_scenes()
        {
            Assert.AreEqual(Lines(MainPath + "  active  2 roots", "edit mode"), Ask("/scene"));
        }

        [Test]
        public void Open_refuses_to_drop_unsaved_changes()
        {
            AddUnsaved("Unsaved");

            Assert.AreEqual(Err("unsaved changes in " + MainPath + ": ug save, or --additive to keep them open"), Ask("/scene/open", "path", OtherPath));
            Assert.AreEqual(1, SceneManager.sceneCount);

            Assert.AreEqual(
                Lines("opened " + OtherPath, MainPath + "  active  modified  3 roots", OtherPath + "  1 root", "edit mode"),
                Ask("/scene/open", "path", OtherPath, "additive", "true"));
            Assert.AreEqual(Err(OtherPath + " is already open"), Ask("/scene/open", "path", OtherPath, "additive", "true"));
        }

        [Test]
        public void Open_replaces_a_saved_scene()
        {
            Assert.AreEqual(Lines("opened " + OtherPath, OtherPath + "  active  1 root", "edit mode"), Ask("/scene/open", "path", OtherPath));
            Assert.AreEqual(Err("not a scene: " + PrefabPath), Ask("/scene/open", "path", PrefabPath));
        }

        [Test]
        public void New_close_and_active_scenes()
        {
            string path = Folder + "/Third.unity";
            Assert.AreEqual(Err("a new scene needs a path like Assets/Scenes/Level2.unity"), Ask("/scene/new", "path", "Third"));
            Assert.AreEqual(Err(MainPath + " already exists: ug scene open " + MainPath), Ask("/scene/new", "path", MainPath));

            Assert.AreEqual(Lines("created " + path, MainPath + "  2 roots", path + "  active  2 roots", "edit mode"), Ask("/scene/new", "path", path, "additive", "true"));
            Assert.AreEqual(Lines(MainPath + "  active  2 roots", path + "  2 roots", "edit mode"), Ask("/scene/active", "path", "Main"));
            Assert.AreEqual(Lines("closed " + path, MainPath + "  active  2 roots", "edit mode"), Ask("/scene/close", "path", path));
            Assert.AreEqual(Err(MainPath + " is the only open scene: ug scene open another one instead"), Ask("/scene/close", "path", MainPath));
        }

        [Test]
        public void Save_writes_modified_scenes_only()
        {
            Assert.AreEqual(Lines("no modified scenes; assets saved"), Ask("/save"));

            AddUnsaved("Added");
            Assert.AreEqual(Lines("saved " + MainPath), Ask("/save"));
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);
            Assert.AreEqual(Err(OtherPath + " is not open"), Ask("/save", "path", OtherPath));
        }

        [Test]
        public void Save_refuses_an_untitled_scene()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AddUnsaved("Added");

            Assert.AreEqual(Err("an untitled scene has changes: save it once in Unity to give it a path"), Ask("/save"));
        }

        [Test]
        public void Stop_and_pause_need_play_mode()
        {
            Assert.AreEqual(Err("not playing"), Ask("/stop"));
            Assert.AreEqual(Err("not playing"), Ask("/pause"));
            Assert.AreEqual("edit mode", SceneOps.PlayState());
        }
    }

    public class ReadOpsTests : OpsFixture
    {
        [Test]
        public void Tree_lists_roots_to_a_depth()
        {
            Assert.AreEqual(Lines("Camera", "Level  (3 children)"), Ask("/tree", "depth", "1"));
            Assert.AreEqual(Lines("Camera", "Level", "  Enemy#1", "  Enemy#2", "  Player  UgProbe  (1 child)"), Ask("/tree"));
        }

        [Test]
        public void Tree_of_one_object_or_prefab()
        {
            Assert.AreEqual(Lines("Level/Player  UgProbe", "  Model"), Ask("/tree", "object", "Level/Player"));
            Assert.AreEqual(Lines(PrefabPath, "  Barrel", "    Muzzle"), Ask("/tree", "object", PrefabPath, "depth", "3"));
            Assert.AreEqual(Err("not found: Nope in the loaded scenes"), Ask("/tree", "object", "Nope"));
        }

        [Test]
        public void Tree_marks_scenes_inactive_objects_and_prefab_instances()
        {
            EditorSceneManager.OpenScene(OtherPath, OpenSceneMode.Additive);
            Find("Level/Enemy#2").SetActive(false);
            var turret = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));

            Assert.AreEqual(
                Lines("Main:", "  Camera", "  Level", "    Enemy#1", "    Enemy#2  inactive", "    Player  UgProbe  (1 child)", "  Turret  [Turret.prefab]", "    Barrel  (1 child)", "Other:", "  Camera"),
                Ask("/tree"));
            Object.DestroyImmediate(turret);
        }

        [Test]
        public void Inspect_gives_an_overview_of_an_object()
        {
            Find("Level/Player").transform.localPosition = new Vector3(1, 2.5f, 0);

            Assert.AreEqual(
                Lines(
                    "Level/Player",
                    "  Transform  position 1, 2.5, 0  rotation 0, 0, 0  scale 1, 1, 1",
                    "  UgProbe  Packages/com.unitygraph.bridge/Tests/Runtime/UgProbe.cs",
                    "  children: Model"),
                Ask("/inspect", "object", "Level/Player"));
        }

        [Test]
        public void Inspect_lists_a_components_fields()
        {
            UgProbe probe = Find("Level/Player").GetComponent<UgProbe>();
            probe.target = Find("Level/Enemy#2").transform;
            probe.paint = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            probe.guns[0] = Find("Camera");

            Assert.AreEqual(
                Lines(
                    "Level/Player  UgProbe  Packages/com.unitygraph.bridge/Tests/Runtime/UgProbe.cs",
                    "  speed  float  5",
                    "  lives  int  3",
                    "  armed  bool  false",
                    "  title  string  \"probe\"",
                    "  mode  enum  Run",
                    "  tint  Color  1, 0, 0, 1",
                    "  offset  Vector3  1, 2, 3",
                    "  mask  LayerMask  Default",
                    "  target  Transform  Level/Enemy#2",
                    "  paint  Material  " + MaterialPath,
                    "  guns  GameObject[2]  Camera, null",
                    "  waves  UgWave[1]  {count: 3, delay: 0.5}",
                    "  boss  UgWave  {count: 1, delay: 2}",
                    "  _secret  int  7"),
                Ask("/inspect", "object", "Level/Player", "component", "ugprobe"));
            Assert.AreEqual(Err("Level/Player has no Rigidbody (has: Transform, UgProbe)"), Ask("/inspect", "object", "Level/Player", "component", "Rigidbody"));
        }

        [Test]
        public void Inspect_reads_assets_and_materials()
        {
            string body = Ask("/inspect", "object", MaterialPath);
            StringAssert.StartsWith("{\"lines\":[\"" + MaterialPath + "  Material  shader Unlit/Color\"", body);
            StringAssert.Contains("\"  _Color  Color  1, 1, 1, 1\"", body);

            StringAssert.StartsWith("{\"lines\":[\"" + PrefabPath + "\",\"  Transform  position 0, 0, 0", Ask("/inspect", "object", PrefabPath));
        }

        [Test]
        public void Inspect_names_the_rest_of_a_cut_value_and_field_shows_it_whole()
        {
            var holder = ScriptableObject.CreateInstance<ValueHolder>();
            holder.numbers = new int[250];
            for (int i = 0; i < holder.numbers.Length; i++)
            {
                holder.numbers[i] = i;
            }

            string path = Folder + "/Holder.asset";
            AssetDatabase.CreateAsset(holder, path);

            string body = Ask("/inspect", "object", path);
            StringAssert.Contains("\"  numbers  int[250]  0, 1, 2, 3, 4, 5, 6, 7, 8, 9, ... 240 more\"", body);
            StringAssert.Contains("\"  nest  Nest  {inner: {...}, level: 0}\"", body);
            StringAssert.EndsWith(",\"  (values cut: ug inspect " + path + " --field numbers)\"]}", body);

            body = Ask("/inspect", "object", path, "field", "numbers");
            StringAssert.StartsWith("{\"lines\":[\"" + path + "  ValueHolder  ", body);
            StringAssert.Contains("  numbers  int[250]\",\"  [0]  0\",\"  [1]  1\"", body);
            StringAssert.EndsWith(",\"  [199]  199\",\"  ... 50 more: ug inspect " + path + " --field numbers --from 200\"]}", body);

            body = Ask("/inspect", "object", path, "field", "numbers", "from", "200");
            StringAssert.Contains("  numbers  int[250]\",\"  [200]  200\"", body);
            StringAssert.EndsWith(",\"  [249]  249\"]}", body);
            StringAssert.EndsWith(",\"  (nothing from 300: 250 elements)\"]}", Ask("/inspect", "object", path, "field", "numbers", "from", "300"));

            StringAssert.EndsWith("  nest  Nest\",\"  {inner: {count: 0, offset: 0, 0, 0, name: \\\"\\\"}, level: 0}\"]}", Ask("/inspect", "object", path, "field", "nest"));
            StringAssert.EndsWith("  numbers[3]  int\",\"  3\"]}", Ask("/inspect", "object", path, "field", "numbers[3]"));
            StringAssert.StartsWith("{\"error\":", Ask("/inspect", "object", path, "field", "nope"));
        }

        [Test]
        public void Inspect_from_pages_fields_and_material_properties()
        {
            var holder = ScriptableObject.CreateInstance<ValueHolder>();
            string path = Folder + "/Holder.asset";
            AssetDatabase.CreateAsset(holder, path);

            string body = Ask("/inspect", "object", path, "from", "9");
            StringAssert.Contains("\",\"  numbers  int[0]  empty\",\"  nest  Nest  {inner: {...}, level: 0}\"", body);
            StringAssert.DoesNotContain("  bounds  ", body);
            StringAssert.EndsWith(",\"  (nothing from field 20: 11 fields)\"]}", Ask("/inspect", "object", path, "from", "20"));

            string all = Ask("/inspect", "object", MaterialPath);
            string rest = Ask("/inspect", "object", MaterialPath, "from", "1");
            StringAssert.Contains("  _Color  Color  1, 1, 1, 1", all);
            Assert.Less(rest.Length, all.Length);
            StringAssert.StartsWith("{\"lines\":[\"" + MaterialPath + "  Material  shader Unlit/Color\"", rest);
            Assert.AreEqual(Lines(MaterialPath + "  Material  _Color  Color  1, 1, 1, 1"), Ask("/inspect", "object", MaterialPath, "field", "_Color"));
        }

        [Test]
        public void An_overview_past_ten_children_names_the_tree_that_lists_them()
        {
            GameObject level = Find("Level");
            for (int i = 0; i < 9; i++)
            {
                new GameObject("Crate" + i).transform.SetParent(level.transform);
            }

            StringAssert.EndsWith(",\"  children: Enemy#1, Enemy#2, Player, Crate0, Crate1, Crate2, Crate3, Crate4, Crate5, Crate6, ... 2 more (ug tree Level --depth 1)\"]}", Ask("/inspect", "object", "Level"));
        }

        [Test]
        public void Find_matches_names_tags_layers_and_components()
        {
            Assert.AreEqual(Lines("Level/Enemy#1", "Level/Enemy#2"), Ask("/find", "name", "enemy"));
            Assert.AreEqual(Lines("Level/Player"), Ask("/find", "component", "MonoBehaviour"));
            Assert.AreEqual(Lines("no object in the loaded scenes matches"), Ask("/find", "name", "enemy", "tag", "Player"));
            Assert.AreEqual(Err("no layer Nope"), Ask("/find", "layer", "Nope"));
            Assert.AreEqual(Err("give a name, --tag, --layer or --component"), Ask("/find"));
        }

        [Test]
        public void Past_100_matches_find_names_all_which_lists_every_one()
        {
            GameObject level = Find("Level");
            for (int i = 0; i < 101; i++)
            {
                new GameObject("Crate" + i).transform.SetParent(level.transform);
            }

            string cut = Ask("/find", "name", "crate");
            string all = Ask("/find", "name", "crate", "all", "true");

            StringAssert.EndsWith(",\"... 1 more (101 in all): narrow the filters, or ug find crate --live --all\"]}", cut);
            StringAssert.Contains("\"Level/Crate100\"", all);
            StringAssert.DoesNotContain("more", all);
        }

        [Test]
        public void Past_three_changed_objects_the_digest_writes_every_change_to_its_file()
        {
            Digest before = Digest.Take(null);
            GameObject level = Find("Level");
            for (int i = 0; i < 5; i++)
            {
                new GameObject("Spawned" + i).transform.SetParent(level.transform);
            }

            UnityEngine.Object.DestroyImmediate(Find("Level/Enemy#2"));
            string part = before.Objects();

            StringAssert.IsMatch(@"^changed Spawned\d \(new\), Spawned\d \(new\), Spawned\d \(new\), \+2 more \(Library/unitygraph/digest\.txt\), 1 gone$", part);
            string[] written = System.IO.File.ReadAllText(System.IO.Path.Combine(Bridge.ProjectRoot, Digest.ChangesFile)).TrimEnd('\n').Split('\n');
            CollectionAssert.AreEquivalent(
                new[] { "Level/Spawned0 (new)", "Level/Spawned1 (new)", "Level/Spawned2 (new)", "Level/Spawned3 (new)", "Level/Spawned4 (new)", "Enemy (gone)" }, written);
            Assert.AreEqual("Enemy (gone)", written[written.Length - 1]);
        }

        [Test]
        public void An_ambiguous_address_past_ten_names_find_live()
        {
            GameObject level = Find("Level");
            for (int i = 0; i < 12; i++)
            {
                new GameObject("Box").transform.SetParent(level.transform);
            }

            string answer = Ask("/inspect", "object", "Level/Box");

            StringAssert.StartsWith("{\"error\":\"ambiguous: Box (12 in ", answer);
            StringAssert.EndsWith(", ... 2 more: ug find --live Box\"}", answer);
        }
    }
}
