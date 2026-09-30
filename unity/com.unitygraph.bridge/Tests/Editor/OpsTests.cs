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
            string body = SceneOps.Scenes().Body;

            Assert.AreEqual(
                "{\"play\":\"edit\",\"scenes\":[{\"path\":\"" + MainPath + "\",\"name\":\"Main\",\"active\":true,\"modified\":false,\"loaded\":true,\"roots\":2}]}",
                body);
        }

        [Test]
        public void Open_refuses_to_drop_unsaved_changes()
        {
            AddUnsaved("Unsaved");

            Assert.AreEqual(
                "{\"error\":\"unsaved changes in " + MainPath + ": ug save, or --additive to keep them open\"}",
                SceneOps.Open(Request("/scene/open", "path", OtherPath)).Body);
            Assert.AreEqual(1, SceneManager.sceneCount);

            StringAssert.StartsWith("{\"opened\":\"" + OtherPath + "\"", SceneOps.Open(Request("/scene/open", "path", OtherPath, "additive", "true")).Body);
            Assert.AreEqual(2, SceneManager.sceneCount);
            Assert.IsTrue(SceneManager.GetSceneAt(0).isDirty, "the unsaved scene stays open and modified");
            Assert.AreEqual("{\"error\":\"" + OtherPath + " is already open\"}", SceneOps.Open(Request("/scene/open", "path", OtherPath, "additive", "true")).Body);
        }

        [Test]
        public void Open_replaces_a_saved_scene()
        {
            string body = SceneOps.Open(Request("/scene/open", "path", OtherPath)).Body;

            StringAssert.Contains("\"name\":\"Other\",\"active\":true", body);
            Assert.AreEqual(1, SceneManager.sceneCount);
            Assert.AreEqual("{\"error\":\"not a scene: " + PrefabPath + "\"}", SceneOps.Open(Request("/scene/open", "path", PrefabPath)).Body);
        }

        [Test]
        public void Save_writes_modified_scenes_only()
        {
            Assert.AreEqual("{\"saved\":[]}", SceneOps.Save(Request("/save")).Body);

            AddUnsaved("Added");
            Assert.AreEqual("{\"saved\":[\"" + MainPath + "\"]}", SceneOps.Save(Request("/save")).Body);
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);
            Assert.AreEqual("{\"error\":\"" + OtherPath + " is not open\"}", SceneOps.Save(Request("/save", "path", OtherPath)).Body);
        }

        [Test]
        public void Save_refuses_an_untitled_scene()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AddUnsaved("Added");

            Assert.AreEqual(
                "{\"error\":\"an untitled scene has changes: save it once in Unity to give it a path\"}",
                SceneOps.Save(Request("/save")).Body);
        }

        [Test]
        public void Stop_and_pause_need_play_mode()
        {
            Assert.AreEqual("{\"error\":\"not playing\"}", SceneOps.Stop(new ConsoleBuffer()).Body);
            Assert.AreEqual("{\"error\":\"not playing\"}", SceneOps.Pause().Body);
            Assert.AreEqual("edit", SceneOps.PlayState());
        }
    }
}
