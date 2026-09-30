using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityGraph.Bridge.Tests
{
    public class EditOpsTests : OpsFixture
    {
        private const string Unsaved = "  (Main.unity modified, not saved)";
        private const string Written = "  (written " + PrefabPath + ")";

        [Test]
        public void Create_objects_primitives_and_prefab_instances()
        {
            Assert.AreEqual(Lines("created Level/Spawn" + Unsaved), Ask("/object/create", "name", "Spawn", "parent", "Level"));
            Assert.AreEqual(Lines("created Cube (Cube primitive)" + Unsaved), Ask("/object/create", "primitive", "cube"));
            Assert.AreEqual(Lines("created Level/Turret (Turret prefab instance)" + Unsaved), Ask("/object/create", "prefab", PrefabPath, "parent", "Level"));
            Assert.IsTrue(PrefabUtility.IsOutermostPrefabInstanceRoot(Find("Level/Turret")));
            Assert.IsTrue(SceneManager.GetActiveScene().isDirty);

            Undo.PerformUndo();
            Assert.AreEqual("not found: Turret in Level", Error("Level/Turret"));

            Assert.AreEqual(Err("no primitive Blob (Sphere, Capsule, Cylinder, Cube, Plane, Quad)"), Ask("/object/create", "primitive", "Blob"));
            Assert.AreEqual(Err("not a prefab: " + MaterialPath), Ask("/object/create", "prefab", MaterialPath));
            Assert.AreEqual(Err("names with / cannot be addressed by ug: pick another name"), Ask("/object/create", "name", "a/b"));
        }

        [Test]
        public void Delete_rename_and_active()
        {
            Assert.AreEqual(Lines("deleted Level/Enemy#2" + Unsaved), Ask("/object/delete", "object", "Level/Enemy#2"));
            Assert.AreEqual(Lines("renamed Level/Enemy -> Level/Boss" + Unsaved), Ask("/object/rename", "object", "Level/Enemy", "name", "Boss"));
            Assert.AreEqual(Lines("Level/Boss  active true -> false" + Unsaved), Ask("/object/active", "object", "Level/Boss", "value", "false"));
            Assert.IsFalse(Find("Level/Boss").activeSelf);
            Assert.AreEqual(Err("not true or false: maybe"), Ask("/object/active", "object", "Level/Boss", "value", "maybe"));

            Assert.AreEqual(Lines("deleted Level and 3 objects under it" + Unsaved), Ask("/object/delete", "object", "Level"));
            Undo.PerformUndo();
            Assert.AreEqual("Model", Find("Level/Player/Model").name);
        }

        [Test]
        public void Move_reparents_reorders_and_changes_scene()
        {
            Assert.AreEqual(Lines("moved Level/Player -> Player" + Unsaved), Ask("/object/move", "object", "Level/Player", "parent", ""));
            Assert.AreEqual(Lines("moved Player -> Player at index 0" + Unsaved), Ask("/object/move", "object", "Player", "index", "0"));
            Assert.AreEqual("Player", SceneManager.GetActiveScene().GetRootGameObjects()[0].name);
            Assert.AreEqual(Err("cannot move Player under itself or its children"), Ask("/object/move", "object", "Player", "parent", "Player/Model"));
            Assert.AreEqual(Lines("moved Camera -> Level/Camera" + Unsaved), Ask("/object/move", "object", "Camera", "parent", "Level"));

            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(OtherPath, UnityEditor.SceneManagement.OpenSceneMode.Additive);
            Assert.AreEqual(
                Lines("moved Main:Player -> Other:Player  (Other.unity modified, not saved)"),
                Ask("/object/move", "object", "Player", "scene", "Other"));
            Assert.AreEqual(Err("--scene moves an object to a scene's root: give no --parent"), Ask("/object/move", "object", "Other:Player", "scene", "Main", "parent", "Main:Level"));
        }

        [Test]
        public void Duplicate_keeps_prefab_links_and_the_selection()
        {
            Selection.activeGameObject = Find("Camera");
            Ask("/object/create", "prefab", PrefabPath);

            string answer = Ask("/object/duplicate", "object", "Turret");
            StringAssert.StartsWith("{\"lines\":[\"duplicated Turret -> Turret", answer);
            Assert.AreEqual(4, SceneManager.GetActiveScene().rootCount);
            GameObject copy = SceneManager.GetActiveScene().GetRootGameObjects()[3];
            Assert.IsTrue(PrefabUtility.IsOutermostPrefabInstanceRoot(copy));
            Assert.AreSame(Find("Camera"), Selection.activeGameObject);

            Assert.AreEqual(Lines("duplicated Level/Player -> Level/Hero" + Unsaved), Ask("/object/duplicate", "object", "Level/Player", "name", "Hero"));
            Assert.AreEqual("Model", Find("Level/Hero/Model").name);
        }

        [Test]
        public void Prefab_instance_parts_are_edited_in_the_prefab()
        {
            Ask("/object/create", "prefab", PrefabPath);

            string refusal = "Turret/Barrel is part of the prefab instance Turret: edit " + PrefabPath + ":Barrel, or ug prefab unpack Turret first";
            Assert.AreEqual(Err(refusal), Ask("/object/delete", "object", "Turret/Barrel"));
            Assert.AreEqual(Err(refusal), Ask("/object/move", "object", "Turret/Barrel", "parent", ""));
        }

        [Test]
        public void Prefab_asset_edits_write_the_asset()
        {
            Assert.AreEqual(
                Lines("renamed " + PrefabPath + ":Barrel/Muzzle -> " + PrefabPath + ":Barrel/Tip" + Written),
                Ask("/object/rename", "object", PrefabPath + ":Barrel/Muzzle", "name", "Tip"));
            Assert.AreEqual("Tip", Find(PrefabPath + ":Barrel/Tip").name);

            Assert.AreEqual(Lines("created " + PrefabPath + ":Barrel/Sight" + Written), Ask("/object/create", "name", "Sight", "parent", PrefabPath + ":Barrel"));
            Assert.AreEqual(Lines("added Rigidbody to " + PrefabPath + Written), Ask("/component/add", "object", PrefabPath, "type", "Rigidbody"));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath).GetComponent<Rigidbody>());
            Assert.AreEqual(
                Lines(PrefabPath + ":Barrel  Transform.localPosition  0, 0, 0 -> 0, 1, 0" + Written),
                Ask("/set", "object", PrefabPath + ":Barrel", "field", "position", "value", "0,1,0"));

            Assert.AreEqual(Err("the root of " + PrefabPath + " cannot be deleted: ug asset delete " + PrefabPath), Ask("/object/delete", "object", PrefabPath));
            Assert.AreEqual(Err("a prefab cannot contain itself: " + PrefabPath), Ask("/object/create", "prefab", PrefabPath, "parent", PrefabPath));
            Assert.AreEqual(
                Err("Packages/com.example/Thing.prefab is in a package, which ug does not edit: copy it into Assets/ first"),
                Ask("/object/rename", "object", "Packages/com.example/Thing.prefab", "name", "X"));
        }

        [Test]
        public void Components_add_and_remove()
        {
            Assert.AreEqual(Lines("added Rigidbody to Level/Player" + Unsaved), Ask("/component/add", "object", "Level/Player", "type", "rigidbody"));
            Assert.AreEqual(Lines("added BoxCollider to Level/Player" + Unsaved), Ask("/component/add", "object", "Level/Player", "type", "BoxCollider"));
            Assert.AreEqual(Lines("added BoxCollider#2 to Level/Player" + Unsaved), Ask("/component/add", "object", "Level/Player", "type", "BoxCollider"));
            StringAssert.StartsWith("{\"error\":\"no component type Rigidbod (near: Rigidbody", Ask("/component/add", "object", "Level/Player", "type", "Rigidbod"));
            Assert.AreEqual(Err("Level/Player already has a Transform"), Ask("/component/add", "object", "Level/Player", "type", "Transform"));

            Ask("/component/add", "object", "Level/Enemy#1", "type", "HingeJoint");
            Assert.AreEqual(
                Err("Rigidbody is required by HingeJoint on Level/Enemy#1: remove HingeJoint first"),
                Ask("/component/remove", "object", "Level/Enemy#1", "component", "Rigidbody"));
            Assert.AreEqual(Err("the Transform cannot be removed: ug object delete Level/Player removes the object"), Ask("/component/remove", "object", "Level/Player", "component", "Transform"));
            Assert.AreEqual(Lines("removed BoxCollider#2 from Level/Player" + Unsaved), Ask("/component/remove", "object", "Level/Player", "component", "BoxCollider#2"));
            Assert.AreEqual(Lines("removed UgProbe from Level/Player" + Unsaved), Ask("/component/remove", "object", "Level/Player", "component", "UgProbe"));
            Assert.IsNull(Find("Level/Player").GetComponent<UgProbe>());

            Undo.PerformUndo();
            Assert.IsNotNull(Find("Level/Player").GetComponent<UgProbe>());
        }

        [Test]
        public void Set_fields_by_type()
        {
            Assert.AreEqual(Lines("Level/Player  UgProbe.speed  5 -> 7.5" + Unsaved), Ask("/set", "object", "Level/Player", "field", "speed", "value", "7.5"));
            Assert.AreEqual(7.5f, Find("Level/Player").GetComponent<UgProbe>().speed);
            Assert.AreEqual(Lines("Level/Player  UgProbe.mode  Run -> Fly" + Unsaved), Ask("/set", "object", "Level/Player", "component", "UgProbe", "field", "mode", "value", "fly"));
            Assert.AreEqual(Lines("Level/Player  UgProbe.tint  1, 0, 0, 1 -> 0, 1, 0, 1" + Unsaved), Ask("/set", "object", "Level/Player", "field", "tint", "value", "#00ff00"));
            Assert.AreEqual(Lines("Level/Player  UgProbe.target  null -> Level/Enemy#1" + Unsaved), Ask("/set", "object", "Level/Player", "field", "target", "value", "Level/Enemy#1"));
            Assert.AreEqual(Lines("Level/Player  UgProbe.guns.size  2 -> 3" + Unsaved), Ask("/set", "object", "Level/Player", "field", "guns.size", "value", "3"));
            Assert.AreEqual(Lines("Level/Player  UgProbe.waves[0].count  3 -> 4" + Unsaved), Ask("/set", "object", "Level/Player", "field", "waves[0].count", "value", "4"));
            Assert.AreEqual(Lines("Level/Player  Transform.localPosition  0, 0, 0 -> 1, 2, 3" + Unsaved), Ask("/set", "object", "Level/Player", "field", "position", "value", "1,2,3"));
            Assert.AreEqual(Lines("Level/Player  name  \"Player\" -> \"Hero\"" + Unsaved), Ask("/set", "object", "Level/Player", "field", "name", "value", "Hero"));

            Assert.AreEqual(Err("Level/Hero  UgProbe.speed: not a number: fast"), Ask("/set", "object", "Level/Hero", "field", "speed", "value", "fast"));
            Assert.AreEqual(Err("no layer Nope"), Ask("/set", "object", "Level/Hero", "field", "layer", "value", "Nope"));
            StringAssert.StartsWith("{\"error\":\"no tag Nope (tags: Untagged", Ask("/set", "object", "Level/Hero", "field", "tag", "value", "Nope"));
            Assert.AreEqual(
                Err("no field nothing on Level/Hero or its components (Transform, UgProbe)"),
                Ask("/set", "object", "Level/Hero", "field", "nothing", "value", "1"));
            Assert.AreEqual(Err("no value given"), Ask("/set", "object", "Level/Hero", "field", "speed"));
        }

        [Test]
        public void Set_material_properties()
        {
            Assert.AreEqual(
                Lines(MaterialPath + "  _Color  1, 1, 1, 1 -> 1, 0, 0, 1  (" + MaterialPath + " modified, not saved)"),
                Ask("/set", "object", MaterialPath, "field", "color", "value", "#ff0000"));
            Assert.AreEqual(Color.red, AssetDatabase.LoadAssetAtPath<Material>(MaterialPath).color);
            StringAssert.StartsWith("{\"error\":\"no property glow on " + MaterialPath, Ask("/set", "object", MaterialPath, "field", "glow", "value", "1"));
        }

        [Test]
        public void Undo_redo_and_selection()
        {
            Ask("/object/rename", "object", "Level/Player", "name", "Hero");
            Assert.AreEqual(Lines("undid ug object rename"), Ask("/undo"));
            Assert.AreEqual("Player", Find("Level/Player").name);
            Assert.AreEqual(Lines("redid ug object rename"), Ask("/redo"));
            Assert.AreEqual("Hero", Find("Level/Hero").name);

            Assert.AreEqual(Lines("selected Level/Hero"), Ask("/select", "object", "Level/Hero"));
            Assert.AreSame(Find("Level/Hero"), Selection.activeGameObject);
            Assert.AreEqual(Lines("Level/Hero"), Ask("/select"));
            Assert.AreEqual(Lines("nothing selected"), Ask("/select", "clear", "true"));
            Assert.AreEqual(Lines("nothing selected"), Ask("/select"));
        }
    }
}
